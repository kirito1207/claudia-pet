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
        sealed class FakeFamily { public string Owner; public string Pipe; public readonly Dictionary<string, FakeHubHost> Pets = new Dictionary<string, FakeHubHost>(); }

        sealed class FakeHubHost : IPetHubHost
        {
            public readonly System.Collections.Concurrent.ConcurrentQueue<Action> Queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            readonly List<Timer> timers = new List<Timer>();
            public readonly string Id; readonly FakeFamily family;
            public string Name = "", Mode = "claude";
            public IPetHub Hub;
            public readonly List<string> Applied = new List<string>(), Signals = new List<string>(), Launched = new List<string>();
            public bool Lost, Left, UserQuit;
            public FakeHubHost(string id, FakeFamily family) { Id = id; this.family = family; family.Pets[id] = this; }
            public void Lead() { family.Owner = Id; var s = new PetHubServer(this, family.Pipe, Id) { RelaunchMs = 100 }; Hub = s; s.Start(); }
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
            public void HubAppChanged() { }
            public void HubLaunch(string id, string mode) => Launched.Add(id + ":" + mode);
            public bool HubRunning(string id) => false;
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
        }

        [STAThread]
        static int Main()
        {
            var id = "test-" + Guid.NewGuid().ToString("N");
            var data = Path.Combine(Path.GetTempPath(), "claudia-" + id);
            Environment.SetEnvironmentVariable("CLAUDIA_DATA_DIR", data);
            Environment.SetEnvironmentVariable("CLAUDIA_STATE_DIR", Path.Combine(data, "state"));
            try { Pet.TestHandoff(); TestCharacterMigration(id); TestLegacyMirrorMigration(id + "-legacy"); TestPetHub(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
        }
    }
}
