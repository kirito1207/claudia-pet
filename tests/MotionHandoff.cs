using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;

namespace Claudia
{
    // Test-only partial class: calls the real mouse-up handoff against an unshown WPF HWND.
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
            foreach (var direction in new[] { -1, 1 })
            {
                var pet = new Pet(null);
                pet.Window = new Window { Width = 260, Height = 362, Left = 400, Top = 300,
                    WindowStyle = WindowStyle.None, AllowsTransparency = true };
                pet.hwnd = new WindowInteropHelper(pet.Window).EnsureHandle();
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
                pet.AcceptMotion(new Dictionary<string, object> { ["id"] = 72, ["phase"] = "flight", ["x"] = before.Left - direction * 100, ["y"] = before.Top });
                Native.GetWindowRect(pet.hwnd, out after);
                Check(after.Left == before.Left + direction * 10, "Old gesture frame was accepted");
                pet.StopMotion(false);
                pet.Window.Close();
            }
            Console.WriteLine("PASS: real WPF handoff preserves position and rejects stale drag/gesture frames");
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
    static class MotionHandoffRunner
    {
        [STAThread]
        static int Main()
        {
            try { Pet.TestHandoff(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
