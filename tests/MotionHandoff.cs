using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;

namespace Claudia
{
    // Test-only partial class: calls the real mouse-up handoff against an unshown DirectHost HWND.
    sealed partial class Pet
    {
        public static void TestHandoff()
        {
            var policy = new TaskNoticePolicy(100);
            Check(!policy.Accept("old", "complete", 99, 101, true, false, true), "Startup replay was not suppressed");
            Check(!policy.Accept("front", "waiting", 101, 101, true, true, true), "Foreground notification was not suppressed");
            Check(!policy.Accept("front", "waiting", 101, 102, true, false, true), "Suppressed event replayed later");
            Check(policy.Accept("one", "complete", 102, 102, true, false, true), "Fresh completion was suppressed");
            Check(!policy.Accept("one", "complete", 102, 112, true, false, true), "Duplicate event replayed");
            Check(!policy.Accept("burst", "complete", 103, 103, true, false, true), "Burst was not coalesced");
            Check(!policy.Accept("off", "error", 104, 104, false, false, true), "Disabled category notified");
            Check(policy.Accept("anywhere", "waiting", 113, 113, true, true, false), "Always-notify option failed");
            Console.WriteLine("PASS: notification freshness, foreground gating, duplicate/burst suppression and category switches");
            Store.Settings = new Dictionary<string, object>(); // Test process only; no user files touched.

            // Hit rectangles arrive in canvas pixels and must reach the window in physical pixels (window width / canvas width).
            var input = new Pet(null) { Host = new DirectHost("test", 380, 362), ready = true };   // 380 = default canvas width (Pet.BaseWidth)
            input.hitRects = new object[] { new object[] { 10, 15, 50, 30 } };
            input.UpdateHitRects();
            var applied = input.Host.HitRects;
            Check(applied.Count == 1 && applied[0] == new Rectangle(10, 15, 50, 30), "Reported hit rectangle was not applied");
            input.UpdateHitRects();
            Check(ReferenceEquals(input.Host.HitRects, applied), "Unchanged hit geometry was rebuilt");
            input.Host.Width = 760;   // twice the 380 canvas
            input.UpdateHitRects();
            Check(input.Host.HitRects.Count == 1 && input.Host.HitRects[0] == new Rectangle(20, 30, 100, 60), "Cached hit rectangle was not rescaled");
            input.Host.Dispose();
            Console.WriteLine("PASS: reported hit geometry is reused and rescaled on resize");

            foreach (var direction in new[] { -1, 1 })
            {
                var pet = new Pet(null);
                pet.Host = new DirectHost("test", 260, 362) { Bounds = new Rectangle(400, 300, 260, 362) };
                var moves = 0;
                pet.Host.MessageHook = (msg, wp) => { if (msg == 0x0046) moves++; return false; };   // WM_WINDOWPOSCHANGING
                pet.hwnd = pet.Host.Handle;   // creates the HWND without showing it
                Native.GetWindowRect(pet.hwnd, out var before);
                pet.motionId = 73;
                pet.dragging = true;
                pet.dragTick0 = Environment.TickCount;
                pet.EndDrag();
                Native.GetWindowRect(pet.hwnd, out var after);
                Check(before.Left == after.Left && before.Top == after.Top, "Mouse-up moved the actual HWND");
                Check(pet.flying && !pet.dragging, "Host failed to enter flight handoff");
                // A delayed pre-release browser frame must never pull the window backwards.
                pet.AcceptMotion(new Dictionary<string, object> { ["id"] = 73, ["phase"] = "drag", ["x"] = before.Left - 50, ["y"] = before.Top });
                Native.GetWindowRect(pet.hwnd, out after);
                Check(after.Left == before.Left, "Late drag frame was accepted after release");
                pet.AcceptMotion(new Dictionary<string, object> { ["id"] = 73, ["phase"] = "flight", ["x"] = before.Left + direction * 10, ["y"] = before.Top });
                Native.GetWindowRect(pet.hwnd, out after);
                Check(after.Left == before.Left + direction * 10, "Flight frame was not applied");
                Check(moves > 0, "The HWND did not report a position change");
                var appliedMoves = moves;
                pet.AcceptMotion(new Dictionary<string, object> { ["id"] = 73, ["phase"] = "flight", ["x"] = after.Left + 0.2, ["y"] = after.Top });
                Check(moves == appliedMoves, "Duplicate rounded flight position moved the HWND again");
                pet.AcceptMotion(new Dictionary<string, object> { ["id"] = 72, ["phase"] = "flight", ["x"] = before.Left - direction * 100, ["y"] = before.Top });
                Native.GetWindowRect(pet.hwnd, out after);
                Check(after.Left == before.Left + direction * 10, "Old gesture frame was accepted");
                pet.StopMotion(false);
                pet.Host.Dispose();
            }
            Console.WriteLine("PASS: real DirectHost handoff preserves position and skips stale and duplicate window moves");
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
    static class MotionHandoffRunner
    {
        static void TestCharacterMigration(string id)
        {
            Store.LoadSettings();
            var source = Path.Combine(Store.BuiltinCharacters, id);
            var data = Path.Combine(Store.Characters, id);
            try
            {
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(source, "character.json"), "{}");
                File.Copy(Path.Combine(source, "character.json"), Path.Combine(data, "character.json"));
                File.WriteAllText(Path.Combine(source, "idle.png"), "original");
                File.Copy(Path.Combine(source, "idle.png"), Path.Combine(data, "idle.png"));
                File.WriteAllText(Path.Combine(data, "idle.png"), "modified");
                File.SetLastWriteTimeUtc(Path.Combine(data, "idle.png"), File.GetLastWriteTimeUtc(Path.Combine(source, "idle.png")));
                Store.SyncResources();
                Store.Manifest();
                if (Store.CharacterDir(id) != data ||
                    Store.CharactersRootFor("/characters/" + id + "/idle.png") != Store.Characters)
                    throw new Exception("A same-size local edit was removed or hidden");

                File.Delete(Path.Combine(data, "idle.png"));
                Store.Manifest();
                if (Store.CharacterDir(id) != source) throw new Exception("An incomplete local role hid the built-in");
                File.Copy(Path.Combine(source, "idle.png"), Path.Combine(data, "idle.png"));
                Store.SyncResources();
                Store.SyncResources();
                Store.Manifest();
                if (Directory.Exists(data) || Store.CharacterDir(id) != source ||
                    Store.CharactersRootFor("/characters/" + id + "/idle.png") != Store.BuiltinCharacters)
                    throw new Exception("The verified mirror was not retired or the built-in route failed");
                Console.WriteLine("PASS: built-in asset routing, exact mirror cleanup and preservation of local edits");
            }
            finally { if (Directory.Exists(source)) Directory.Delete(source, true); }
        }

        static void TestLegacyMirrorMigration(string id)
        {
            var source = Path.Combine(Store.Data, "test-install");
            var installed = Path.Combine(source, id);
            var local = Path.Combine(Store.Characters, id);
            Directory.CreateDirectory(Path.Combine(installed, "anim"));
            Directory.CreateDirectory(Path.Combine(local, "anim"));
            const string character = "{\"animations\":{\"idle\":{\"sheets\":[\"待机\"]}}}";
            File.WriteAllText(Path.Combine(installed, "character.json"), character);
            File.WriteAllText(Path.Combine(local, "character.json"), character);
            var video = Path.Combine(installed, "anim", "待机.webm");
            var sheet = Path.Combine(local, "anim", "待机.webp");
            File.WriteAllText(sheet, "released");
            var files = new Dictionary<string, object>();
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                object[] Fingerprint(string path)
                {
                    var bytes = File.ReadAllBytes(path);
                    return new object[] { bytes.Length, BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "") };
                }
                files["character.json"] = Fingerprint(Path.Combine(local, "character.json"));
                files["anim"] = new Dictionary<string, object> { ["待机.webp"] = Fingerprint(sheet) };
            }
            Store.WriteJson(Path.Combine(source, "legacy-mirrors.json"), new Dictionary<string, object>
            {
                [id] = new Dictionary<string, object> { ["v0.4.2"] = files },
            });

            Store.SyncResources(source);
            if (!File.Exists(sheet)) throw new Exception("Legacy mirror moved without a usable installed replacement");
            File.WriteAllText(video, "new video");
            File.WriteAllText(sheet, "modified");
            Store.SyncResources(source);
            if (File.ReadAllText(sheet) != "modified") throw new Exception("Legacy mirror same-size edit was not preserved");
            File.WriteAllText(sheet, "released");
            var history = Path.Combine(Store.Data, "builtin-resource-sync", id);
            var previous = Path.Combine(history, "previous");
            Directory.CreateDirectory(previous);
            Store.SyncResources(source);
            Store.SyncResources(source);
            var backups = Directory.GetDirectories(history, "retired-*");
            if (backups.Length != 1 || File.ReadAllText(Path.Combine(backups[0], "anim", "待机.webp")) != "released" ||
                Directory.Exists(local) || !Directory.Exists(previous)) throw new Exception("Legacy mirror was not safely backed up");
            Console.WriteLine("PASS: legacy mirrors are backed up, same-size edits are preserved, and a usable replacement is required");
        }

        // 多开协调（PetHub，2026-11-27 起每只平级、负责人可以交接）：真的命名管道，一个进程里开几只；回调都排进队列，在测试线程上跑
        sealed class FakeFamily
        {
            public string Owner; public string Pipe;
            public readonly Dictionary<string, FakeHubHost> Pets = new Dictionary<string, FakeHubHost>();
            public readonly HashSet<string> Running = new HashSet<string>();   // 实例锁还被占着的编号
        }

        sealed class FakeHubHost : IPetHubHost
        {
            public readonly System.Collections.Concurrent.ConcurrentQueue<Action> Queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            readonly List<Timer> timers = new List<Timer>();
            public readonly string Id; readonly FakeFamily family;
            public string Name = "", Mode = "claude";
            public IPetHub Hub;
            public readonly List<string> Applied = new List<string>(), Signals = new List<string>(), Launched = new List<string>();
            public bool Lost, Left, UserQuit;
            public int AppChanges;
            public FakeHubHost(string id, FakeFamily family) { Id = id; this.family = family; family.Pets[id] = this; }
            public void Lead() { family.Owner = Id; var s = new PetHubServer(this, family.Pipe, Id) { RelaunchMs = 100, AddWaitMs = 800 }; Hub = s; s.Start(); }
            public bool IsLeader => Hub is PetHubServer s && !s.Closed;
            public Dictionary<string, object> HubInfo() => new Dictionary<string, object> { ["name"] = Name, ["mode"] = Mode };
            public void HubApply(string action, string value)
            {
                Applied.Add(action + ":" + value);
                if (action == "rename") Name = value; else if (action == "mode") Mode = value;
                if (action == "dismiss") Left = true; else Hub.Report();
            }
            public void HubRosterChanged() { }
            public void HubNotice(string text) { }
            public void HubSignal(string what) { Signals.Add(what); if (what == "quit") Left = true; }
            public void HubAppChanged() => AppChanges++;
            public int UpdateChecks;
            public void HubCheckUpdate() => UpdateChecks++;
            public readonly List<List<string>> Sessions = new List<List<string>>();
            public void HubSessions(List<Dictionary<string, object>> states) => Sessions.Add(states.Select(x => (string)x["sid"]).ToList());
            public readonly Dictionary<string, string> Tokens = new Dictionary<string, string>();   // 编号 → 最近一次叫出来带的标记
            public bool HubLaunch(string id, string mode, string join) { Launched.Add(id + ":" + mode); Tokens[id] = join; return true; }
            public bool HubRunning(string id) => family.Running.Contains(id);
            public bool HubLeaving() => Left;
            public void HubToggle() => Signals.Add("toggle");
            public void HubUserQuit() => UserQuit = true;
            public void HubLeaveSelf() { family.Owner = null; Hub.Close(); Left = true; }
            public bool HubTryLead() { if (family.Owner != null) return false; Lead(); ((PetHubServer)Hub).Adopt(); return true; }
            public void HubMainLost() => Lost = true;
            public void Post(Action a) => Queue.Enqueue(a);
            public void Later(int ms, Action a) { lock (timers) timers.Add(new Timer(_ => Queue.Enqueue(a), null, ms, Timeout.Infinite)); }
        }

        static bool Pump(FakeFamily f, Func<bool> done, int ms = 6000)
        {
            for (var end = Environment.TickCount + ms; Environment.TickCount < end;)
            {
                if (done()) return true;
                var ran = false;
                foreach (var h in f.Pets.Values.ToList()) while (h.Queue.TryDequeue(out var a)) { a(); ran = true; }
                if (!ran) Thread.Sleep(5);
            }
            return done();
        }

        static string Order(IPetHub hub, string self) => string.Join(",", hub.Roster(self).Select(p => p["index"] + ":" + p["id"]));

        static FakeFamily NewFamily()
        {
            var f = new FakeFamily { Pipe = "Claudia-Pets-test-" + Guid.NewGuid().ToString("N") };
            Store.SaveRoster(new[] { "main", "2", "3" });
            new FakeHubHost("main", f).Lead();
            foreach (var id in new[] { "2", "3" })
            {
                var h = new FakeHubHost(id, f);
                h.Hub = new PetHubClient(h, id, f.Pipe) { StepMs = 400, LostWaitMs = 1500 };
                h.Hub.Start();
            }
            if (!Pump(f, () => ((PetHubServer)f.Pets["main"].Hub).Connected.Count() == 2 && Order(f.Pets["3"].Hub, "3") == "1:main,2:2,3:3"))
                throw new Exception("Pets did not join the hub");
            return f;
        }

        static void CloseFamily(FakeFamily f) { foreach (var h in f.Pets.Values) h.Hub?.Close(); }

        static FakeHubHost JoinLate(FakeFamily f, string id, string token = null)
        {
            var h = new FakeHubHost(id, f);
            h.Hub = new PetHubClient(h, id, f.Pipe, token) { StepMs = 400, LostWaitMs = 1500 };
            h.Hub.Start();
            return h;
        }

        static void TestPetHub()
        {
            var f = NewFamily();
            try
            {
                FakeHubHost main = f.Pets["main"], two = f.Pets["2"], three = f.Pets["3"];
                // Two commands back to back to the same pet: the old command file kept only the last one
                var replies = new List<bool>();
                two.Hub.Request("pet", "3", "rename", "小三", (ok, e) => replies.Add(ok));
                two.Hub.Request("pet", "3", "mode", "desktop", (ok, e) => replies.Add(ok));
                if (!Pump(f, () => replies.Count == 2) || !replies.All(r => r)) throw new Exception("Forwarded commands were not answered");
                if (!three.Applied.SequenceEqual(new[] { "rename:小三", "mode:desktop" })) throw new Exception("Forwarded commands were lost or reordered: " + string.Join(",", three.Applied));

                two.Hub.Request("hide"); two.Hub.Request("show");
                if (!Pump(f, () => f.Pets.Values.All(h => h.Signals.SequenceEqual(new[] { "hide", "show" })))) throw new Exception("Hide / show did not reach every pet including the leader");
                if (!PetHubNames.SendCli("toggle", f.Pipe) || !Pump(f, () => main.Signals.Contains("toggle"))) throw new Exception("Command line did not reach the leader");

                // 1 leaves: 2 takes over, 3 becomes number 2
                main.Hub.Request("leave");
                if (!Pump(f, () => f.Owner == "2" && two.IsLeader && ((PetHubServer)two.Hub).Connected.Contains("3"))) throw new Exception("Leader handoff did not go to the next pet");
                if (!Pump(f, () => Order(three.Hub, "3") == "1:2,2:3")) throw new Exception("Numbers did not shift after the leader left: " + Order(three.Hub, "3"));
                if (!main.Left || three.Lost) throw new Exception("Handoff left the wrong pets");
                // Session states: only the leader scans; changes reach everyone, a pet reporting in gets the full current list
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var leader = (PetHubServer)two.Hub;
                leader.ShareSessions(new List<Dictionary<string, object>> {
                    new Dictionary<string, object> { ["sid"] = "a", ["pose"] = "reading", ["ts"] = now },
                    new Dictionary<string, object> { ["sid"] = "old", ["pose"] = "thinking", ["ts"] = now - 900 },
                    new Dictionary<string, object> { ["sid"] = new object[] { "x" }, ["ts"] = now },   // 坏记录：只跳过这一条
                    new Dictionary<string, object> { ["sid"] = "bad-ts", ["ts"] = "soon" },
                    new Dictionary<string, object> { ["sid"] = "nan", ["ts"] = double.NaN },
                    new Dictionary<string, object> { ["sid"] = "inf", ["ts"] = double.PositiveInfinity } });
                if (!Pump(f, () => three.Sessions.Count == 1) || !three.Sessions[0].SequenceEqual(new[] { "a", "old" })) throw new Exception("Session changes did not reach the other pet");
                if (!leader.SessionIds.SequenceEqual(new[] { "a" })) throw new Exception("Expired sessions were kept for newcomers");
                Store.SaveRoster(Store.Roster().Concat(new[] { "5" }));
                leader.LoadRoster();   // 负责人以自己内存里的为准，文件只是下次用的
                var newcomer = JoinLate(f, "5");
                if (!Pump(f, () => newcomer.Sessions.Count >= 1) || !newcomer.Sessions[0].SequenceEqual(new[] { "a" })) throw new Exception("A pet reporting in did not get the full session list");
                Console.WriteLine("PASS: multi-pet hub forwards commands in order with replies, signals reach everyone, leader handoff goes to the next pet and numbers shift");
            }
            finally { CloseFamily(f); }

            f = NewFamily();
            try
            {
                FakeHubHost main = f.Pets["main"], two = f.Pets["2"], three = f.Pets["3"];
                // Two pets leaving at once: the old roster was rewritten by each, one could come back
                two.Hub.Request("leave"); three.Hub.Request("pet", "3", "dismiss");
                if (!Pump(f, () => two.Left && three.Left)) throw new Exception("Leaving pets were not told to go");
                if (!Store.Roster().SequenceEqual(new[] { "main" })) throw new Exception("Simultaneous leaves left the roster as " + string.Join(",", Store.Roster()));
                if (main.UserQuit) throw new Exception("User-quit was marked while a pet remained");
            }
            finally { CloseFamily(f); }

            f = NewFamily();
            try
            {
                FakeHubHost main = f.Pets["main"], two = f.Pets["2"], three = f.Pets["3"];
                // A pet that drops without saying bye is brought back; the leader crashing goes to the next in line
                ((PetHubClient)three.Hub).Drop();
                if (!Pump(f, () => main.Launched.Contains("3:"))) throw new Exception("A crashed pet was not brought back");
                f.Owner = null; main.Hub.Close();   // the leader is gone without a word
                if (!Pump(f, () => f.Owner != null)) throw new Exception("Nobody took over after the leader crashed");
                if (f.Owner != "2") throw new Exception("The next pet in the roster must take over first, got " + f.Owner);
                if (!Pump(f, () => two.Launched.Contains("main:"))) throw new Exception("The new leader did not bring back the crashed one");
                Console.WriteLine("PASS: two pets leave at once, crashed pets are brought back, and a crashed leader is replaced by the next in line");
            }
            finally { CloseFamily(f); }

            Store.App = new Dictionary<string, object>();
            PetHubNames.ClearStopping();
            f = NewFamily();
            try
            {
                FakeHubHost main = f.Pets["main"], two = f.Pets["2"], three = f.Pets["3"];
                // Shared settings go through the leader: two pets changing different keys at once both survive
                var replies = new List<bool>();
                two.Hub.Request("appSet", null, "clickThrough", "true", (ok, e) => replies.Add(ok));
                three.Hub.Request("appSet", null, "deskLayer", "top", (ok, e) => replies.Add(ok));
                three.Hub.Request("appSet", null, "hideHotkey", "nonsense", (ok, e) => replies.Add(ok));
                if (!Pump(f, () => replies.Count == 3) || replies.Count(r => r) != 2) throw new Exception("appSet replies were wrong");
                var saved = Store.ReadJson(Path.Combine(Store.Data, "app.json"));
                if (!(saved.TryGetValue("clickThrough", out var ct) && ct is bool b && b) || !(saved.TryGetValue("deskLayer", out var dl) && (string)dl == "top"))
                    throw new Exception("Concurrent shared-setting changes overwrote each other");
                if (!Pump(f, () => f.Pets.Values.All(h => h.AppChanges == 2))) throw new Exception("Shared-setting change did not reach every pet");

                // A number whose pet is still waving goodbye is not reused
                two.Hub.Request("pet", "3", "dismiss");
                if (!Pump(f, () => three.Left)) throw new Exception("Dismissed pet was not told to go");
                f.Running.Add("3");
                main.Hub.Request("add", value: "claude");
                if (!main.Launched.SequenceEqual(new[] { "4:claude" })) throw new Exception("Add reused a number still in use: " + string.Join(",", main.Launched));

                // Quit all: a pet still starting follows instead of taking over and bringing everyone back
                main.Hub.Request("quitAll");
                if (!Pump(f, () => two.Left)) throw new Exception("Quit all did not reach the pets");
                var late = JoinLate(f, "4");
                if (!Pump(f, () => late.Signals.Contains("quit"))) throw new Exception("A pet reporting in during quit all was not told to go");
                f.Owner = null; main.Hub.Close();
                var later = JoinLate(f, "2");
                if (!Pump(f, () => later.Lost) || f.Owner != null) throw new Exception("A pet still starting after quit all took over");
                Console.WriteLine("PASS: shared settings have one writer, busy numbers are not reused, and quit all is not undone by late starters");
            }
            finally { CloseFamily(f); PetHubNames.ClearStopping(); }

            f = NewFamily();
            try
            {
                FakeHubHost main = f.Pets["main"], two = f.Pets["2"];
                // Adding is confirmed only once the new pet reports in
                var added = new List<string>();
                two.Hub.Request("add", value: "desktop", reply: (ok, e) => added.Add(ok + ":" + e));
                if (!Pump(f, () => main.Launched.Count == 1)) throw new Exception("Add did not launch");
                Pump(f, () => false, 200);
                if (added.Count != 0) throw new Exception("Add was confirmed before the new pet reported in");
                JoinLate(f, "4", main.Tokens["4"]);
                if (!Pump(f, () => added.Count == 1) || added[0] != "True:") throw new Exception("Add was not confirmed when the new pet reported in: " + string.Join(",", added));

                // One that never reports in is taken off the roster and the slot is freed
                added.Clear();
                two.Hub.Request("add", value: "claude", reply: (ok, e) => added.Add(ok + ":" + e));
                if (!Pump(f, () => added.Count == 1) || added[0].StartsWith("True")) throw new Exception("A pet that never reported in was confirmed");
                if (Store.Roster().Contains("5")) throw new Exception("A pet that never reported in stayed on the roster");

                // Every spare number still held by a pet waving goodbye: ask to wait instead of launching one that cannot start
                added.Clear();
                f.Running.UnionWith(new[] { "5", "6" });
                main.Launched.Clear();
                two.Hub.Request("add", value: "claude", reply: (ok, e) => added.Add(ok + ":" + e));
                if (!Pump(f, () => added.Count == 1) || !added[0].Contains("等它走了再试") || main.Launched.Count != 0)
                    throw new Exception("Add with no free number did not ask to wait: " + string.Join(",", added));
                Console.WriteLine("PASS: adding waits for the new pet to report in, frees the slot when it never does, and asks to wait while numbers are still in use");
            }
            finally { CloseFamily(f); }

            f = NewFamily();
            try
            {
                FakeHubHost main = f.Pets["main"], two = f.Pets["2"];
                ((PetHubServer)main.Hub).AddWaitMs = 5000;   // 刚取消的编号多久内不再用（跟着它走），留足余量
                // Cancelled on its way, then added again: the old process reporting in late is not taken for the new add
                var replies = new List<string>();
                two.Hub.Request("add", value: "claude", reply: (ok, e) => replies.Add("first:" + ok));
                if (!Pump(f, () => main.Launched.Count == 1)) throw new Exception("Add did not launch");
                var oldToken = main.Tokens["4"];
                two.Hub.Request("pet", "4", "dismiss");
                if (!Pump(f, () => replies.Contains("first:False"))) throw new Exception("Cancelling an add on its way did not answer");
                two.Hub.Request("add", value: "desktop", reply: (ok, e) => replies.Add("second:" + ok));
                if (!Pump(f, () => main.Launched.Count == 2) || main.Launched[1] != "5:desktop") throw new Exception("A number just cancelled was reused: " + string.Join(",", main.Launched));
                var stale = JoinLate(f, "4", oldToken);
                if (!Pump(f, () => stale.Applied.Contains("dismiss:"))) throw new Exception("The late old process was not sent away");
                if (Store.Roster().Contains("4") || replies.Contains("second:True")) throw new Exception("The late old process was taken for the new add");
                JoinLate(f, "5", main.Tokens["5"]);
                if (!Pump(f, () => replies.Contains("second:True")) || !Store.Roster().Contains("5")) throw new Exception("Second add was not confirmed");

                // An add still on its way when everyone quits is not kept for next time
                two.Hub.Request("add", value: "claude");
                if (!Pump(f, () => main.Launched.Count == 3)) throw new Exception("Third add did not launch");
                var third = main.Launched[2].Split(':')[0];
                main.Hub.Request("quitAll");
                if (!Pump(f, () => two.Left)) throw new Exception("Quit all did not reach the pets");
                if (Store.Roster().Contains(third)) throw new Exception("An add that never reported in was kept in the roster");
                Console.WriteLine("PASS: a late process from a cancelled add is sent away, a cancelled number waits before reuse, and a pet is written to the roster only once it reports in");
            }
            finally { CloseFamily(f); PetHubNames.ClearStopping(); }
        }

        // 跟同一个 Claude 窗口的桌宠排成一组（ZOrderRule.InClaudeStack，2026-11-27）：只看从 Claude 往上的窗口序列，不碰真窗口
        /// <summary>协调中心拆出来的两个小类，不连管道直接测</summary>
        static void TestPetParts()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> S(object sid, object ts, string pose = "") => new Dictionary<string, object> { ["sid"] = sid, ["ts"] = ts, ["pose"] = pose };
            var book = new SessionBook();
            var got = book.Merge(new[] { S("a", now - 5, "old"), S("a", now, "new"), S("b", now - 900), S("c", double.NaN), S("", now) });
            if (string.Join(",", got.Select(x => x["sid"])) != "a,a,b") throw new Exception("Session book kept bad records: " + string.Join(",", got.Select(x => x["sid"])));
            if (!book.Ids.SequenceEqual(new[] { "a" })) throw new Exception("Session book kept an expired session");
            book.Merge(new[] { S("a", now - 1, "late") });
            if ((string)book.Values[0]["pose"] != "new") throw new Exception("An older session state arriving late won");

            var adds = new PetAdds();
            var replies = new List<string>();
            var t1 = adds.Begin("2", (ok, e) => replies.Add("first:" + ok));
            if (!adds.Contains("2") || adds.Free("2", 0, 30000)) throw new Exception("A number waiting to report in was free");
            if (adds.End("2", "someone-else", true, 0) != null) throw new Exception("Another add's timeout touched this one");
            adds.End("2", t1, true, 100000)(false, "");
            if (adds.Free("2", 110000, 30000) || !adds.Free("2", 131000, 30000)) throw new Exception("A cancelled number was not held back for exactly the hold time");
            adds.Begin("3", null);
            adds.End("3", adds.Token("3"), false, 100000);
            if (!adds.Free("3", 100000, 30000)) throw new Exception("A launch that never started was held back");
            adds.Begin("4", (ok, e) => replies.Add("four:" + ok));
            adds.Confirm("4")(true, "");
            if (adds.Contains("4") || string.Join(",", replies) != "first:False,four:True") throw new Exception("Add replies went wrong: " + string.Join(",", replies));
            Console.WriteLine("PASS: session book keeps the newest valid state per session for 10 minutes; adds wait, confirm, end and hold numbers as they should");
        }

        /// <summary>检查更新的两条规则：版本号怎么比、GitHub 的回答怎么认（不连网）</summary>
        static void TestUpdateCheck()
        {
            var newer = new[] { ("v0.5.2", "0.5.1", true), ("0.10.0", "0.9.9", true), ("1.0", "0.9.9", true), ("0.5.1", "0.5.1", false),
                ("0.5.1-beta", "0.5.1", false), ("0.4.2", "0.5.1", false), ("latest", "0.5.1", false), ("0.5.2", "dev", false) };
            foreach (var (a, b, want) in newer) if (UpdateCheck.Newer(a, b) != want) throw new Exception($"Newer({a}, {b}) should be {want}");
            var ok = UpdateCheck.Parse("{\"tag_name\": \"v0.6.0\", \"html_url\": \"https://github.com/kirito1207/claudia-pet/releases/tag/v0.6.0\"}");
            if (ok?.version != "0.6.0" || ok?.url != "https://github.com/kirito1207/claudia-pet/releases/tag/v0.6.0") throw new Exception("Release not parsed");
            if (UpdateCheck.Parse("{\"tag_name\": \"0.6.0\", \"html_url\": \"https://evil.example/x\"}")?.url != UpdateCheck.ReleasesPage) throw new Exception("A non-GitHub page was kept");
            if (UpdateCheck.Parse("{\"message\": \"Not Found\"}") != null || UpdateCheck.Parse("not json") != null || UpdateCheck.Parse("{\"tag_name\": \"nightly\"}") != null)
                throw new Exception("An unreadable answer was taken as a release");
            Console.WriteLine("PASS: update check compares version numbers and only takes GitHub release pages");
        }

        static void TestZOrder()
        {
            IntPtr claude = (IntPtr)100, other = (IntPtr)200, a = (IntPtr)1, b = (IntPtr)2, hidden = (IntPtr)9, app = (IntPtr)50;
            (IntPtr, bool, IntPtr) P(IntPtr h, IntPtr following) => (h, true, following);   // 一只看得见的桌宠，跟着 following
            var checks = new (string what, (IntPtr, bool, IntPtr)[] above, IntPtr pet, bool expected)[]
            {
                ("A right above Claude", new[] { P(a, claude) }, a, true),
                ("A → B → Claude: both stay", new[] { P(b, claude), P(a, claude) }, a, true),
                ("A → B → Claude: B stays", new[] { P(b, claude), P(a, claude) }, b, true),
                ("invisible windows in between do not count", new[] { (hidden, false, IntPtr.Zero), P(a, claude) }, a, true),
                ("an app covering Claude below A: A fell out", new[] { (app, true, IntPtr.Zero), P(a, claude) }, a, false),
                ("B in place even if A fell out above an app", new[] { P(b, claude), (app, true, IntPtr.Zero), P(a, claude) }, b, true),
                ("a pet following another Claude is not in the group", new[] { P(b, other), P(a, claude) }, a, false),
                ("nothing above Claude", new (IntPtr, bool, IntPtr)[0], a, false),
            };
            foreach (var (what, above, pet, expected) in checks)
                if (ZOrderRule.InClaudeStack(above, pet, claude) != expected) throw new Exception("Z-order rule failed: " + what);
            Console.WriteLine("PASS: pets following the same Claude window form one group and stay put once stacked; a pet that fell out goes back");
        }

        [STAThread]
        static int Main()
        {
            var id = "test-" + Guid.NewGuid().ToString("N");
            var data = Path.Combine(Path.GetTempPath(), "claudia-" + id);
            Environment.SetEnvironmentVariable("CLAUDIA_DATA_DIR", data);
            Environment.SetEnvironmentVariable("CLAUDIA_STATE_DIR", Path.Combine(data, "state"));
            try { Pet.TestHandoff(); TestCharacterMigration(id); TestLegacyMirrorMigration(id + "-legacy"); TestZOrder(); TestPetParts(); TestUpdateCheck(); TestPetHub(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
        }
    }
}
