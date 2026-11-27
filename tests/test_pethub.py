"""Multi-pet hub with leader handoff (linux/claudia/pethub.py): real Unix sockets, one process.
Run with python -B tests/test_pethub.py."""

import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "linux"))
from gi.repository import GLib  # noqa: E402

from claudia import pethub, store  # noqa: E402


class Family:
    """The leader lock, shared by the fake pets of one test (stands in for run/leader.lock)."""

    def __init__(self, path):
        self.path, self.owner, self.running = path, None, set()   # running: ids whose instance lock is still held


class FakePet:
    """Stands in for Pet: keeps its own name / placement, records what the hub asked of it."""

    def __init__(self, pid, family):
        self.pid, self.family = pid, family
        self.name, self.mode = "", "claude"
        self.hub, self.applied, self.notices, self.signals = None, [], [], []
        self.roster_changes, self.launched, self.app_changes = 0, [], 0
        self.lost = self.user_quit = self.left = False
        self.launch_ok = True
        self.tokens = {}   # id → the join token of the last launch
        self.sessions = []   # every session-state batch the hub delivered

    # leader-side setup the real Pet does in become_leader / give_up_leading
    def lead(self):
        self.family.owner = self.pid
        self.hub = pethub.HubServer(self, self.family.path, self_id=self.pid)
        self.hub.start()

    def is_leader(self):
        return isinstance(self.hub, pethub.HubServer) and not self.hub.closed

    def hub_info(self):
        return {"name": self.name, "mode": self.mode}

    def hub_apply(self, action, value):
        self.applied.append((action, value))
        if action == "rename":
            self.name = value
        elif action == "mode":
            self.mode = value
        if action == "dismiss":
            self.left = True
        else:
            self.hub.report()

    def hub_roster_changed(self):
        self.roster_changes += 1

    def hub_notice(self, text):
        self.notices.append(text)

    def hub_launch(self, pid, mode, join=None):
        self.launched.append((pid, mode))
        self.tokens[pid] = join
        return self.launch_ok

    def hub_signal(self, what):
        self.signals.append(what)
        if what == "quit":
            self.left = True

    def hub_toggle(self):
        self.signals.append("toggle")

    def hub_sessions(self, states):
        self.sessions.append([s["sid"] for s in states])

    def hub_app_changed(self):
        self.app_changes += 1

    def hub_user_quit(self):
        self.user_quit = True

    def hub_leave_self(self):
        self.family.owner = None
        self.hub.close()
        self.left = True

    def hub_try_lead(self):
        if self.family.owner is not None:
            return False
        self.lead()
        self.hub.adopt()   # like Pet.hub_try_lead
        return True

    def hub_main_lost(self):
        self.lost = True

    def hub_running(self, pid):
        return pid in self.family.running

    def hub_leaving(self):
        return self.left


def pump(cond, timeout=4.0):
    ctx, end = GLib.MainContext.default(), time.monotonic() + timeout
    while time.monotonic() < end:
        if cond():
            return True
        ctx.iteration(False)
        time.sleep(0.002)
    return cond()


def ids(roster):
    return [(p["index"], p["id"]) for p in roster]


class PartsTests(unittest.TestCase):
    """The two small pieces the hub is built from, without any sockets"""

    def test_session_book_keeps_newest_skips_bad_and_expires(self):
        book, now = pethub.SessionBook(), time.time()
        got = book.merge([{"sid": "a", "ts": now - 5, "pose": "old"}, {"sid": "a", "ts": now, "pose": "new"},
                          {"sid": "b", "ts": now - 900}, {"sid": "c", "ts": float("nan")}, {"sid": "", "ts": now}])
        self.assertEqual([s["sid"] for s in got], ["a", "a", "b"], "bad records are dropped, good ones kept in order")
        self.assertEqual(list(book), ["a"], "expired ones are not kept")
        book.merge([{"sid": "a", "ts": now - 1, "pose": "late"}])
        self.assertEqual(book.values()[0]["pose"], "new", "an older state arriving late does not win")

    def test_pet_adds_states(self):
        adds = pethub.PetAdds()
        replies = []
        t1 = adds.begin("2", lambda ok, e="": replies.append(("first", ok)))
        self.assertIn("2", adds)
        self.assertFalse(adds.free("2", 0, 30), "a number waiting to report in is taken")
        self.assertIsNone(adds.end("2", "someone-else", True, 0), "another add's timeout does not touch this one")
        adds.end("2", t1, True, 100)(False, "")
        self.assertFalse(adds.free("2", 110, 30), "a number just cancelled is held back")
        self.assertTrue(adds.free("2", 131, 30), "and freed after the hold")
        adds.begin("3", None)
        adds.end("3", adds.token("3"), False, 100)
        self.assertTrue(adds.free("3", 100, 30), "a launch that never started is not held back")
        adds.begin("4", lambda ok, e="": replies.append(("four", ok)))
        adds.confirm("4")(True)
        self.assertNotIn("4", adds)
        self.assertEqual(replies, [("first", False), ("four", True)])


class HubTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        data = Path(tmp.name)
        p = patch.multiple(store, DATA=data, RUN=data / "run", PETS_FILE=data / "pets.json", APP_FILE=data / "app.json",
                           app={}, pet_id="main")
        p.start()
        self.addCleanup(p.stop)
        for name, ms in (("RELAUNCH_MS", 100), ("ADD_WAIT_MS", 600)):
            fast = patch.object(pethub.HubServer, name, ms)
            fast.start()
            self.addCleanup(fast.stop)
        self.family = Family(data / "run" / "pets.sock")
        store.save_roster(["main", "2", "3"])
        self.pets = {"main": FakePet("main", self.family)}
        self.pets["main"].lead()
        for pid in ("2", "3"):
            self.join(pid)
        self.assertTrue(pump(lambda: set(self.leader().hub.clients) == {"2", "3"}))
        self.addCleanup(lambda: [p.hub.close() for p in self.pets.values() if p.hub])

    def join(self, pid, token=None):
        pet = FakePet(pid, self.family)
        pet.hub = pethub.HubClient(pet, self.family.path, self_id=pid, join=token)
        pet.hub.STEP_MS, pet.hub.LOST_WAIT_MS = 400, 1500
        pet.hub.start()
        self.pets[pid] = pet
        return pet

    def leader(self):
        return self.pets[self.family.owner]

    def test_roster_order_is_the_display_number(self):
        two = self.pets["2"]
        self.assertTrue(pump(lambda: len(two.hub.pets) == 3 and all(p["online"] for p in two.hub.pets)))
        self.assertEqual(ids(two.hub.roster()), [(1, "main"), (2, "2"), (3, "3")])
        self.assertEqual([p["self"] for p in two.hub.roster()], [False, True, False])

    def test_commands_to_another_pet_are_forwarded_in_order_and_answered(self):
        two, three = self.pets["2"], self.pets["3"]
        replies = []
        # Two commands back to back: the old command file kept only the last one
        two.hub.request("pet", "3", "rename", "小三", lambda ok, error="": replies.append(ok))
        two.hub.request("pet", "3", "mode", "desktop", lambda ok, error="": replies.append(ok))
        self.assertTrue(pump(lambda: len(replies) == 2))
        self.assertEqual(replies, [True, True])
        self.assertEqual(three.applied, [("rename", "小三"), ("mode", "desktop")])
        seen = lambda: next((p for p in two.hub.pets if p["id"] == "3"), {})  # noqa: E731
        self.assertTrue(pump(lambda: seen().get("name") == "小三" and seen().get("mode") == "desktop"))

    def test_two_dismissals_at_once_both_leave_the_roster(self):
        self.pets["2"].hub.request("pet", "2", "dismiss")
        self.pets["3"].hub.request("leave")
        self.assertTrue(pump(lambda: self.pets["2"].left and self.pets["3"].left))
        self.assertEqual(store.roster(), ["main"])
        self.assertFalse(self.pets["main"].user_quit)

    def test_add_uses_the_lowest_free_number_and_confirms_once_it_reports_in(self):
        replies = []
        self.pets["2"].hub.request("add", value="desktop", reply=lambda ok, error="": replies.append((ok, error)))
        self.assertTrue(pump(lambda: self.pets["main"].launched))
        self.assertEqual(self.pets["main"].launched, [("4", "desktop")])
        self.assertEqual(store.roster(), ["main", "2", "3"], "not written to the roster before it reports in")
        new = next(p for p in self.leader().hub.roster() if p["id"] == "4")
        self.assertEqual((new["mode"], new["online"]), ("desktop", False), "before it reports in, it shows where it was asked to go")
        pump(lambda: False, timeout=0.2)
        self.assertEqual(replies, [], "success is only confirmed once the new pet reports in")
        self.join("4", self.pets["main"].tokens["4"])
        self.assertTrue(pump(lambda: replies))
        self.assertEqual(replies, [(True, "")])
        self.assertEqual(store.roster(), ["main", "2", "3", "4"])

    def test_late_old_process_from_a_cancelled_add_is_not_taken_for_the_next_add(self):
        replies = []
        two, main = self.pets["2"].hub, self.pets["main"]
        two.request("add", value="claude", reply=lambda ok, error="": replies.append(("first", ok)))
        self.assertTrue(pump(lambda: main.launched))
        old_token = main.tokens["4"]
        two.request("pet", "4", "dismiss")   # still on its way: cancel this add
        self.assertTrue(pump(lambda: ("first", False) in replies))
        two.request("add", value="desktop", reply=lambda ok, error="": replies.append(("second", ok)))
        self.assertTrue(pump(lambda: len(main.launched) == 2))
        self.assertEqual(main.launched[-1], ("5", "desktop"), "a number just cancelled is not reused at once")
        # The old process for 4 reports in late: it is sent away and does not count as anything
        stale = self.join("4", old_token)
        self.assertTrue(pump(lambda: ("dismiss", "") in stale.applied))
        self.assertNotIn("4", store.roster())
        self.assertNotIn(("second", True), replies)
        self.join("5", main.tokens["5"])
        self.assertTrue(pump(lambda: ("second", True) in replies))
        self.assertEqual(store.roster(), ["main", "2", "3", "5"])

    def test_same_number_and_wrong_token_is_sent_away(self):
        replies = []
        self.pets["2"].hub.request("add", value="claude", reply=lambda ok, error="": replies.append(ok))
        self.assertTrue(pump(lambda: self.pets["main"].launched))
        impostor = self.join("4", "not-this-add")
        self.assertTrue(pump(lambda: ("dismiss", "") in impostor.applied))
        self.assertEqual(replies, [])
        self.join("4", self.pets["main"].tokens["4"])
        self.assertTrue(pump(lambda: replies == [True]))

    def test_add_not_reported_in_before_the_leader_goes_is_not_kept(self):
        self.pets["2"].hub.request("add", value="claude")
        self.assertTrue(pump(lambda: self.pets["main"].launched))
        self.leader().hub.request("quitAll")
        self.assertTrue(pump(lambda: self.pets["2"].left))
        self.assertEqual(store.roster(), ["main", "2", "3"], "next start must not bring back a pet that never joined")

    def test_add_that_cannot_be_written_when_it_reports_in_is_sent_away(self):
        replies = []
        self.pets["2"].hub.request("add", value="claude", reply=lambda ok, error="": replies.append((ok, error)))
        self.assertTrue(pump(lambda: self.pets["main"].launched))
        with patch.object(store, "save_roster", side_effect=OSError("disk full")):
            newcomer = self.join("4", self.pets["main"].tokens["4"])
            self.assertTrue(pump(lambda: replies and ("dismiss", "") in newcomer.applied))
        self.assertFalse(replies[0][0])
        self.assertEqual(store.roster(), ["main", "2", "3"])
        store.save_roster(["main", "2", "3", "4", "5"])
        self.leader().hub.request("add", value="claude")
        self.assertTrue(self.pets["main"].notices and "住不下" in self.pets["main"].notices[-1])

    def test_hide_show_reach_everyone_including_the_leader(self):
        self.pets["3"].hub.request("hide")
        self.pets["3"].hub.request("show")
        self.assertTrue(pump(lambda: all(p.signals == ["hide", "show"] for p in self.pets.values())))

    def test_command_line_goes_to_the_leader(self):
        self.assertTrue(pethub.send_cli("toggle", self.family.path))
        self.assertTrue(pethub.send_cli("show", self.family.path))
        self.assertTrue(pump(lambda: self.pets["main"].signals == ["toggle", "show"] and self.pets["2"].signals == ["show"]))

    def test_shared_settings_are_written_only_by_the_leader(self):
        replies = []
        self.pets["2"].hub.request("appSet", None, "clickThrough", "true", lambda ok, error="": replies.append(ok))
        self.pets["3"].hub.request("appSet", None, "deskLayer", "top", lambda ok, error="": replies.append(ok))
        self.pets["3"].hub.request("appSet", None, "hideHotkey", "nonsense", lambda ok, error="": replies.append(ok))
        self.assertTrue(pump(lambda: len(replies) == 3))
        self.assertEqual(sorted(replies), [False, True, True])
        # Two pets changing different keys at once: both survive (each used to write its own stale copy)
        self.assertEqual(store.read_json(store.APP_FILE), {"clickThrough": True, "deskLayer": "top"})
        self.assertTrue(pump(lambda: all(p.app_changes == 2 for p in self.pets.values())))

    def test_add_skips_a_number_still_in_use(self):
        # 3 was just dismissed but is still waving with its lock held: the new pet must not reuse its number
        self.pets["2"].hub.request("pet", "3", "dismiss")
        self.assertTrue(pump(lambda: self.pets["3"].left))
        self.family.running.add("3")
        self.leader().hub.request("add", value="claude")
        self.assertEqual(self.pets["main"].launched, [("4", "claude")])

    def test_add_that_never_reports_in_frees_the_slot(self):
        replies = []
        self.pets["2"].hub.request("add", value="claude", reply=lambda ok, error="": replies.append((ok, error)))
        self.assertTrue(pump(lambda: replies, timeout=3))
        self.assertFalse(replies[0][0])
        self.assertEqual(store.roster(), ["main", "2", "3"])
        self.assertTrue(pump(lambda: [p["id"] for p in self.pets["2"].hub.pets] == ["main", "2", "3"]))

    def test_add_that_fails_to_launch_frees_the_slot_at_once(self):
        self.pets["main"].launch_ok = False
        replies = []
        self.pets["2"].hub.request("add", value="claude", reply=lambda ok, error="": replies.append((ok, error)))
        self.assertTrue(pump(lambda: replies))
        self.assertEqual((replies[0][0], store.roster()), (False, ["main", "2", "3"]))
        # Nothing started, so the number is free again at once (not held back like a cancelled one)
        self.pets["main"].launch_ok = True
        self.pets["2"].hub.request("add", value="claude")
        self.assertTrue(pump(lambda: len(self.pets["main"].launched) == 2))
        self.assertEqual(self.pets["main"].launched[-1][0], "4")

    def test_add_waits_while_every_free_number_is_still_waving(self):
        self.family.running.update({"4", "5", "6"})   # 6 numbers for 5 places: every spare one is still waving
        replies = []
        self.pets["2"].hub.request("add", value="claude", reply=lambda ok, error="": replies.append((ok, error)))
        self.assertTrue(pump(lambda: replies))
        self.assertFalse(replies[0][0])
        self.assertIn("等它走了再试", replies[0][1])
        self.assertEqual(self.pets["main"].launched, [])

    def test_shared_setting_that_cannot_be_saved_is_not_applied(self):
        replies = []
        with patch.object(store, "write_json", side_effect=OSError("disk full")):
            self.pets["2"].hub.request("appSet", None, "clickThrough", "true", lambda ok, error="": replies.append((ok, error)))
            self.assertTrue(pump(lambda: replies))
        self.assertFalse(replies[0][0])
        self.assertNotIn("clickThrough", store.app)
        pump(lambda: False, timeout=0.2)
        self.assertTrue(all(p.app_changes == 0 for p in self.pets.values()))

    def test_last_roster_marks_a_pet_that_dropped_during_the_broadcast_offline(self):
        self.assertTrue(pump(lambda: len(self.pets["2"].hub.pets) == 3))
        server = self.leader().hub
        server.clients = {"3": server.clients["3"], "2": server.clients["2"]}   # 3 gets the broadcast first
        three_hub = self.pets["3"].hub
        three_hub.leaving = True   # 3 is really gone: it will not reconnect
        three_hub.link.sock.close()
        server.report()   # sending to 3 fails first; the rest of that round must not leave a stale roster behind
        three = lambda: next(p for p in self.pets["2"].hub.pets if p["id"] == "3")  # noqa: E731
        self.assertTrue(pump(lambda: three()["online"] is False))
        pump(lambda: False, timeout=0.3)
        self.assertIs(three()["online"], False, "a stale roster must not mark it online again")

    def test_roster_write_failure_is_reported(self):
        replies = []
        with patch.object(store, "save_roster", side_effect=OSError("disk full")):
            self.pets["2"].hub.request("leave", reply=lambda ok, error="": replies.append((ok, error)))
            self.assertTrue(pump(lambda: replies))
        self.assertFalse(replies[0][0])
        self.assertFalse(self.pets["2"].left)
        self.assertEqual(store.roster(), ["main", "2", "3"])

    def test_late_arrivals_after_quit_all_follow_instead_of_taking_over(self):
        self.leader().hub.request("quitAll")
        self.assertTrue(pump(lambda: self.pets["2"].left and self.pets["3"].left))
        # A pet that was still starting reports in while the leader waves: it is told to go too
        late = self.join("3")
        self.assertTrue(pump(lambda: "quit" in late.signals))
        # Once the leader is gone, one still starting must not take over and bring everyone back
        self.family.owner = None
        self.pets["main"].hub.close()
        later = self.join("2")
        self.assertTrue(pump(lambda: later.lost))
        self.assertIsNone(self.family.owner)
        self.assertEqual(store.roster(), ["main", "2", "3"])
        # Much later still (the old 30-second window): until the user reopens, nobody takes over
        import os
        old = time.time() - 3600
        os.utime(pethub.stop_path(), (old, old))
        latest = self.join("3")
        self.assertTrue(pump(lambda: latest.lost))
        self.assertIsNone(self.family.owner)

    def test_quit_all_keeps_the_roster(self):
        self.pets["3"].hub.request("quitAll")
        self.assertTrue(pump(lambda: all(p.left for p in self.pets.values())))
        self.assertEqual(store.roster(), ["main", "2", "3"])
        self.assertTrue(self.pets["main"].user_quit)

    def test_last_pet_leaving_marks_user_quit(self):
        self.pets["2"].hub.request("leave")
        self.pets["3"].hub.request("leave")
        self.assertTrue(pump(lambda: self.pets["2"].left and self.pets["3"].left))
        self.assertFalse(self.pets["main"].user_quit)
        self.leader().hub.request("leave")
        self.assertTrue(self.pets["main"].left and self.pets["main"].user_quit)
        self.assertEqual(store.roster(), [])

    def test_session_states_are_scanned_once_and_shared(self):
        now = time.time()
        hub = self.leader().hub
        hub.share_sessions([{"sid": "a", "pose": "reading", "ts": now}, {"sid": "old", "pose": "thinking", "ts": now - 900}])
        self.assertTrue(pump(lambda: all(p.sessions for p in (self.pets["2"], self.pets["3"]))))
        self.assertEqual(self.pets["2"].sessions[0], ["a", "old"], "changes are passed on as scanned")
        hub.share_sessions([{"sid": "b", "pose": "writing", "ts": now}])
        self.assertEqual(sorted(hub.sessions), ["a", "b"], "only the last 10 minutes are kept for newcomers")
        late = self.join("4")
        store.save_roster(["main", "2", "3", "4"])
        late.hub.close()
        newcomer = self.join("4")
        self.assertTrue(pump(lambda: newcomer.sessions))
        self.assertEqual(sorted(newcomer.sessions[0]), ["a", "b"], "a pet that reports in gets the full current list first")

    def test_bad_session_records_are_skipped_one_by_one(self):
        now = time.time()
        hub = self.leader().hub
        hub.share_sessions([{"sid": ["x"], "ts": now}, {"sid": "s", "ts": "soon"}, {"sid": "t", "ts": True}, "junk",
                            {"sid": "nan", "ts": float("nan")}, {"sid": "inf", "ts": float("inf")},
                            {"sid": "good", "pose": "reading", "ts": now}])
        self.assertTrue(pump(lambda: self.pets["2"].sessions))
        self.assertEqual(self.pets["2"].sessions[0], ["good"], "the good record still goes out")
        self.assertEqual(list(hub.sessions), ["good"])

    def test_pet_dismissed_before_it_connected_is_told_to_leave(self):
        late = self.join("5")   # not in the roster
        self.assertTrue(pump(lambda: ("dismiss", "") in late.applied))
        self.assertNotIn("5", self.leader().hub.clients)

    def test_leader_leaving_hands_over_to_the_next_in_line_and_numbers_shift(self):
        # 1 (main) leaves, 2 takes over, 3 becomes number 2
        self.leader().hub.request("leave")
        self.assertTrue(pump(lambda: self.family.owner == "2" and self.pets["2"].is_leader()))
        self.assertTrue(pump(lambda: "3" in self.pets["2"].hub.clients))
        three = self.pets["3"]
        self.assertTrue(pump(lambda: ids(three.hub.roster()) == [(1, "2"), (2, "3")]))
        self.assertTrue(self.pets["main"].left)
        self.assertFalse(three.lost or self.pets["2"].lost)
        # The new leader works like the old one
        replies = []
        three.hub.request("pet", "2", "rename", "新老大", lambda ok, error="": replies.append(ok))
        self.assertTrue(pump(lambda: replies == [True] and self.pets["2"].name == "新老大"))

    def test_leader_crash_goes_to_the_next_in_line(self):
        self.assertTrue(pump(lambda: all(len(self.pets[p].hub.pets) == 3 for p in ("2", "3"))))
        self.family.owner = None
        self.pets["main"].hub.close()   # gone without a word
        self.assertTrue(pump(lambda: self.family.owner is not None))
        self.assertEqual(self.family.owner, "2", "the next in the roster must take over first")
        self.assertTrue(pump(lambda: "3" in self.pets["2"].hub.clients))
        self.assertFalse(self.pets["3"].lost)
        # The crashed old leader is still on the roster: the new leader brings it back
        self.assertTrue(pump(lambda: ("main", None) in self.pets["2"].launched))

    def test_crashed_pet_is_brought_back_but_one_that_said_bye_is_not(self):
        three = self.pets["3"]
        three.hub.leaving = True
        three.hub.link.close()   # dropped without saying bye
        self.assertTrue(pump(lambda: ("3", None) in self.pets["main"].launched))
        self.pets["2"].hub.close()   # normal exit (Claude closed, quit): says bye first
        self.assertTrue(pump(lambda: "2" not in self.leader().hub.clients))
        pump(lambda: False, timeout=0.4)
        self.assertNotIn(("2", None), self.pets["main"].launched)

    def test_pet_with_no_leader_and_no_turn_gives_up(self):
        late = self.join("5")   # not in the roster: never in line to lead
        late.hub.close()
        store.save_roster(["main", "2", "3"])
        self.family.owner = "someone-else"   # nobody can take the lock
        self.pets["main"].hub.close()
        self.assertTrue(pump(lambda: self.pets["2"].lost and self.pets["3"].lost, timeout=6))


if __name__ == "__main__":
    unittest.main()
