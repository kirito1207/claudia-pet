using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

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

        [STAThread]
        static int Main()
        {
            var id = "test-" + Guid.NewGuid().ToString("N");
            var data = Path.Combine(Path.GetTempPath(), "claudia-" + id);
            Environment.SetEnvironmentVariable("CLAUDIA_DATA_DIR", data);
            Environment.SetEnvironmentVariable("CLAUDIA_STATE_DIR", Path.Combine(data, "state"));
            try { Pet.TestHandoff(); TestCharacterMigration(id); TestLegacyMirrorMigration(id + "-legacy"); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
        }
    }
}
