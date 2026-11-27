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
        self.path, self.owner = path, None


class FakePet:
    """Stands in for Pet: keeps its own name / placement, records what the hub asked of it."""

    def __init__(self, pid, family):
        self.pid, self.family = pid, family
        self.name, self.mode = "", "claude"
        self.hub, self.applied, self.notices, self.signals = None, [], [], []
        self.roster_changes, self.launched, self.app_changes = 0, [], 0
        self.lost = self.user_quit = self.left = False

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

    def hub_launch(self, pid, mode):
        self.launched.append((pid, mode))

    def hub_signal(self, what):
        self.signals.append(what)
        if what == "quit":
            self.left = True

    def hub_toggle(self):
        self.signals.append("toggle")

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
        return False   # the hub asks before relaunching; in these tests nobody that dropped is still running

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


class HubTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        data = Path(tmp.name)
        p = patch.multiple(store, DATA=data, RUN=data / "run", PETS_FILE=data / "pets.json", pet_id="main")
        p.start()
        self.addCleanup(p.stop)
        fast = patch.object(pethub.HubServer, "RELAUNCH_MS", 100)
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

    def join(self, pid):
        pet = FakePet(pid, self.family)
        pet.hub = pethub.HubClient(pet, self.family.path, self_id=pid)
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

    def test_add_uses_the_lowest_free_number_and_refuses_when_full(self):
        replies = []
        self.pets["2"].hub.request("add", value="desktop", reply=lambda ok, error="": replies.append((ok, error)))
        self.assertTrue(pump(lambda: replies))
        self.assertEqual(self.pets["main"].launched, [("4", "desktop")])
        self.assertEqual(store.roster(), ["main", "2", "3", "4"])
        new = next(p for p in self.leader().hub.roster() if p["id"] == "4")
        self.assertEqual((new["mode"], new["online"]), ("desktop", False), "before it reports in, it shows where it was asked to go")
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

    def test_shared_settings_change_reaches_everyone(self):
        self.pets["2"].hub.request("app")
        self.assertTrue(pump(lambda: all(p.app_changes == 1 for p in self.pets.values())))

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
