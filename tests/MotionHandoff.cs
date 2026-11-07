using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
            var input = new Pet(null) { Window = new Window { Width = 260 }, hit = new Canvas(), ready = true };
            input.hitRects = new object[] { new object[] { 10, 15, 50, 30 } };
            input.UpdateHitRects();
            var box = (FrameworkElement)input.hit.Children[0];
            Check(Canvas.GetLeft(box) == 10 && box.Width == 50, "Reported hit rectangle was not applied");
            input.UpdateHitRects();
            Check(input.hit.Children[0] == box, "Unchanged hit geometry was rebuilt");
            input.Window.Width = 520;
            input.UpdateHitRects();
            box = (FrameworkElement)input.hit.Children[0];
            Check(Canvas.GetLeft(box) == 20 && box.Width == 100, "Cached hit rectangle was not rescaled");
            input.Window.Close();
            Console.WriteLine("PASS: reported hit geometry is reused and rescaled on resize");
            foreach (var direction in new[] { -1, 1 })
            {
                var pet = new Pet(null);
                pet.Window = new Window { Width = 260, Height = 362, Left = 400, Top = 300,
                    WindowStyle = WindowStyle.None, AllowsTransparency = true };
                pet.hwnd = new WindowInteropHelper(pet.Window).EnsureHandle();
                var moves = 0;
                HwndSource.FromHwnd(pet.hwnd).AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x0046) moves++; // WM_WINDOWPOSCHANGING
                    return IntPtr.Zero;
                });
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
                pet.Window.Close();
            }
            Console.WriteLine("PASS: real WPF handoff preserves position and skips stale and duplicate window moves");
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
