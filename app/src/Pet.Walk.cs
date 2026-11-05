using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 自己走路 ----------------
        // 网页定方向、速度（物理像素 / 秒）、走多久，发来 {walk: {dir, speed, ms}}；这里逐帧平移窗口，只走水平方向、不出 Claude 窗口。
        // 走够了、走到边、这边没地方走、被拖动 / 隐藏 / Claude 不见了都停下，记下新位置，再告诉网页 walkEnded(原因)。
        // 网页发 {walk: null} 是它自己先停了（换了别的状态），这里停下、记位置，不再回话
        bool walking;
        int walkDir;
        double walkSpeed, walkX;
        long walkUntil, walkLast;
        static readonly Stopwatch walkClock = Stopwatch.StartNew();
        const int WalkMinRoom = 24;   // 这边离 Claude 窗口边不到这么多像素就算没地方走

        void HandleWalk(Dictionary<string, object> w)
        {
            if (w == null) { StopWalk(null); return; }
            if (!w.TryGetValue("dir", out var d) || !w.TryGetValue("speed", out var s) || !w.TryGetValue("ms", out var t)) return;
            double dir, speed, ms;
            try { dir = Convert.ToDouble(d); speed = Convert.ToDouble(s); ms = Convert.ToDouble(t); } catch { return; }
            if (double.IsNaN(speed) || speed <= 0 || speed > 2000 || double.IsNaN(ms) || ms <= 0 || ms > 60000 || (dir != 1 && dir != -1)) return;
            StopWalk(null);
            if (dragging || flying || concealed || sessionLocked || hwnd == IntPtr.Zero || !ClaudeRect(out var c)) { Js("walkEnded('blocked')"); return; }
            Native.GetWindowRect(hwnd, out var r);
            var room = dir < 0 ? r.Left - c.Left : c.Right - r.Right;
            if (room < WalkMinRoom) { Js("walkEnded('blocked')"); return; }
            walking = true; walkDir = (int)dir; walkSpeed = speed; walkX = r.Left;
            walkLast = walkClock.ElapsedMilliseconds; walkUntil = walkLast + (long)ms;
            CompositionTarget.Rendering -= WalkFrame; CompositionTarget.Rendering += WalkFrame;
        }

        void WalkFrame(object sender, EventArgs e)
        {
            if (!walking) { CompositionTarget.Rendering -= WalkFrame; return; }
            if (dragging || flying) { StopWalk(null); return; }   // 拖动接管
            if (concealed || sessionLocked || !ClaudeRect(out var c)) { StopWalk("done"); return; }
            var now = walkClock.ElapsedMilliseconds;
            var dt = Math.Min(0.1, (now - walkLast) / 1000.0); walkLast = now;
            Native.GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            walkX += walkDir * walkSpeed * dt;
            var edge = false;
            if (walkX <= c.Left) { walkX = c.Left; edge = true; }
            if (walkX >= c.Right - w) { walkX = c.Right - w; edge = true; }
            var x = (int)Math.Round(walkX);
            if (x != r.Left) Native.MoveTo(hwnd, x, r.Top);
            // 只在内存里跟着改离右下角的距离：Claude 窗口这时候挪了，跟随也按走到的新位置摆
            offRight = c.Right - (x + w); offBottom = c.Bottom - (r.Top + h);
            if (edge) StopWalk("edge");
            else if (now >= walkUntil) StopWalk("done");
        }

        /// <summary>停下并记下位置；reason 不为空时告诉网页</summary>
        void StopWalk(string reason)
        {
            if (!walking) return;
            walking = false;
            CompositionTarget.Rendering -= WalkFrame;
            if (hwnd != IntPtr.Zero) SavePosition();
            if (reason != null) Js($"walkEnded('{reason}')");
        }
    }
}
