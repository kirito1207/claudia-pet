using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Claudia
{
    /// <summary>用到的几个 Windows 系统调用：窗口样式、层级、按像素摆位置和前台进程识别。</summary>
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        /// <summary>不抢焦点、不出现在 Alt+Tab 里</summary>
        public static void MakeToolWindow(IntPtr hwnd) =>
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

        static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        /// <summary>取消置顶（变成普通窗口，会被别的窗口挡住）</summary>
        /// 取消置顶后系统会把它放在所有普通窗口最前面，还是压在你正在用的窗口上，要等那个窗口再激活一次才盖过来：
        /// 所以再把它挪到当前前台窗口的后面（就像你刚点了一下那个窗口）
        public static void DropFromTop(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            var front = GetForegroundWindow();
            if (front == IntPtr.Zero || front == hwnd) return;
            var root = GetAncestor(front, 2); if (root != IntPtr.Zero) front = root;   // GA_ROOT
            var cls = new StringBuilder(64); GetClassName(front, cls, 64);
            GetWindowThreadProcessId(front, out var pid);
            if (ZOrderRule.TuckBelow(cls.ToString(), pid == MyPid))   // 桌面、任务栏、自己的菜单下面不插（见 ZOrderRule）
                SetWindowPos(hwnd, front, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        public static bool MoveTo(IntPtr hwnd, int x, int y) =>
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        public static uint ForegroundPid()
        {
            var front = GetForegroundWindow();
            if (front == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(front, out var pid); return pid;
        }
    }
}
