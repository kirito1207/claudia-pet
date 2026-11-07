using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 跟着 Claude 窗口 ----------------
        // 她待在 Claude 桌面端的窗口里：紧贴在它上方一层、但不置顶，别的程序盖住 Claude 时也盖住她；
        // Claude 最小化 / 收进托盘时移到屏幕外；Claude 关了就退出（由 Claude Code 插件或桌面端 MCP 拉起，下次打开 Claude 会再出来）。
        // 位置按「离 Claude 窗口右下角多远」记：Claude 窗口挪动、改大小，她跟着走；拖到窗口外松手会被推回窗口里。
        // 找 Claude 主窗口时只认有标题的 Chrome_WidgetWin_1：Process.MainWindowHandle 有时会拿到 Claude 留下的空白阴影窗口（SysShadow）。
        const int DefaultOffRight = 40, DefaultOffBottom = 100;
        int offRight = DefaultOffRight, offBottom = DefaultOffBottom;
        Native.RECT? lastClaude;
        bool shownInClaude, dragging;
        IntPtr claudeHwnd = IntPtr.Zero;
        HashSet<uint> claudePids = new HashSet<uint>();
        DateTime pidsAt = DateTime.MinValue, claudeSeenAt = DateTime.MinValue;
        readonly DateTime followSince = DateTime.UtcNow;

        void StartFollow()
        {
            Every(TimeSpan.FromMilliseconds(100), FollowTick);
            // 切换窗口时 Claude 被激活，系统会把它提到最上面、压住桌宠，等下一次 FollowTick（最多 0.1 秒）才摆回来：
            // 别的窗口盖过又移开、点回 Claude 时她会消失一下再出现（2026-11-07 用户说「窗口覆盖的时候会闪」，实测压在下面约 60 ms）。
            // 前台换了不一定马上提：Claude 是在激活之后才把自己提到最上面的（实测前台事件到时它还没动）。
            // 所以前台一换，接下来 0.5 秒由后台线程每 1 毫秒查一次，被压住就摆回去：按界面线程每帧查（16 毫秒一次）实测仍压住 10–30 毫秒。
            // 后台线程只调 Windows 的窗口函数，摆位置用 SWP_ASYNCWINDOWPOS（不等界面线程处理完）；平时不跑，只在切换窗口后跑 0.5 秒
            foregroundHook = FollowNative.OnForeground(() =>
            {
                guardUntil = Environment.TickCount + 500;
                if (System.Threading.Interlocked.Exchange(ref guarding, 1) == 1) return;   // 已经在查了，延长时间就行
                new System.Threading.Thread(GuardLayer) { IsBackground = true, Name = "layer-guard" }.Start();
            });
        }
        IntPtr foregroundHook;
        volatile int guardUntil;
        int guarding;
        void GuardLayer()
        {
            FollowNative.timeBeginPeriod(1);   // Sleep(1) 真的睡 1 毫秒左右（默认可能 15 毫秒）
            try
            {
                while (unchecked(Environment.TickCount - guardUntil) <= 0)
                {
                    IntPtr pet = hwnd, claude = claudeHwnd;
                    if (shownInClaude && !concealed && pet != IntPtr.Zero && claude != IntPtr.Zero) FollowNative.PlaceAbove(pet, claude, true);
                    System.Threading.Thread.Sleep(1);
                }
            }
            catch (Exception e) { Store.Log("层级守护出错: " + e.Message); }
            finally { FollowNative.timeEndPeriod(1); System.Threading.Interlocked.Exchange(ref guarding, 0); }
        }

        /// <summary>读上次拖到的位置（离 Claude 右下角多远）。窗口第一次摆进 Claude（PlaceInClaude）之前就要读，不然启动时按默认位置摆</summary>
        bool offsetsLoaded;
        void LoadFollowOffsets()
        {
            if (offsetsLoaded) return;
            offsetsLoaded = true;
            if (Store.Settings.TryGetValue("follow", out var f) && f is Dictionary<string, object> d)
            {
                if (d.TryGetValue("right", out var r)) offRight = Convert.ToInt32(r);
                if (d.TryGetValue("bottom", out var b)) offBottom = Convert.ToInt32(b);
            }
        }

        void FollowTick()
        {
            if (hwnd == IntPtr.Zero) return;
            var now = DateTime.UtcNow;
            if ((now - pidsAt).TotalSeconds >= 1)
            {
                pidsAt = now;
                claudePids = FollowNative.ClaudeAppPids();
                claudeHwnd = claudePids.Count > 0 ? FollowNative.FindClaudeMain(claudePids) : IntPtr.Zero;
            }
            if (claudePids.Count > 0) claudeSeenAt = now;
            else
            {
                // 开着时看不到 Claude 了：等 2 秒确认是真的关了（更新重启也算关），再退出；从一开始就没看到 Claude，等 30 秒
                var never = claudeSeenAt == DateTime.MinValue;
                if ((now - (never ? followSince : claudeSeenAt)).TotalSeconds > (never ? 30 : 2))
                {
                    Store.Log(never ? "没找到 Claude 桌面端，" + Store.AppName + " 退出" : "Claude 关了，" + Store.AppName + " 跟着退出");
                    Quit();
                    return;
                }
            }

            Native.GetWindowRect(hwnd, out var pr);
            int w = pr.Right - pr.Left, h = pr.Bottom - pr.Top;
            if (!ClaudeRect(out var c))
            {
                StopMotion(false);
                if (shownInClaude || pr.Left > -5000) { Native.MoveTo(hwnd, -10000, -10000); shownInClaude = false; UpdatePageHidden(); }
                return;
            }
            if (dragging || flying) { UpdateMotionGeometry(); return; }   // 你在拖：随你

            var changed = lastClaude == null || !Same(lastClaude.Value, c);
            int x, y;
            if (!shownInClaude || changed) { x = c.Right - w - offRight; y = c.Bottom - h - offBottom; }
            else { x = pr.Left; y = pr.Top; }
            x = Math.Max(c.Left, Math.Min(x, c.Right - w));
            y = Math.Max(c.Top, Math.Min(y, c.Bottom - h));
            if (x != pr.Left || y != pr.Top) Native.MoveTo(hwnd, x, y);
            lastClaude = c;
            var wasShown = shownInClaude;
            shownInClaude = true;
            if (!wasShown) UpdatePageHidden();
            FollowNative.PlaceAbove(hwnd, claudeHwnd);
        }

        static bool Same(Native.RECT a, Native.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        /// <summary>Claude 主窗口在屏幕上、没最小化时给出它的范围（不含系统画的阴影）</summary>
        bool ClaudeRect(out Native.RECT r)
        {
            r = default;
            if (claudeHwnd == IntPtr.Zero || !Native.IsWindowVisible(claudeHwnd) || FollowNative.IsIconic(claudeHwnd)) return false;
            if (FollowNative.DwmGetWindowAttribute(claudeHwnd, 9, out r, Marshal.SizeOf(typeof(Native.RECT))) == 0) return r.Right > r.Left;
            return Native.GetWindowRect(claudeHwnd, out r) && r.Right > r.Left;
        }

        /// <summary>启动时 / 回到默认位置：窗口一出来就直接摆进 Claude 窗口（找不到 Claude 就先放屏幕外），不先在别处闪一下</summary>
        void PlaceInClaude()
        {
            LoadFollowOffsets();
            pidsAt = DateTime.UtcNow;
            claudePids = FollowNative.ClaudeAppPids();
            claudeHwnd = claudePids.Count > 0 ? FollowNative.FindClaudeMain(claudePids) : IntPtr.Zero;
            Native.GetWindowRect(hwnd, out var pr);
            int w = pr.Right - pr.Left, h = pr.Bottom - pr.Top;
            if (!ClaudeRect(out var c)) { Native.MoveTo(hwnd, -10000, -10000); shownInClaude = false; return; }
            int x = Math.Max(c.Left, Math.Min(c.Right - w - offRight, c.Right - w));
            int y = Math.Max(c.Top, Math.Min(c.Bottom - h - offBottom, c.Bottom - h));
            Native.MoveTo(hwnd, x, y);
            lastClaude = c; shownInClaude = true;
            FollowNative.PlaceAbove(hwnd, claudeHwnd);
        }

        /// <summary>拖完松手：拖出 Claude 窗口就推回来，并记下离右下角多远（下次启动、Claude 窗口挪了都按这个位置）</summary>
        void SaveFollowOffsets()
        {
            if (!ClaudeRect(out var c)) return;
            Native.GetWindowRect(hwnd, out var pr);
            int w = pr.Right - pr.Left, h = pr.Bottom - pr.Top;
            int x = Math.Max(c.Left, Math.Min(pr.Left, c.Right - w));
            int y = Math.Max(c.Top, Math.Min(pr.Top, c.Bottom - h));
            if (x != pr.Left || y != pr.Top) Native.MoveTo(hwnd, x, y);
            offRight = c.Right - (x + w); offBottom = c.Bottom - (y + h);
            lastClaude = c;
            Store.Settings["follow"] = new Dictionary<string, object> { ["right"] = offRight, ["bottom"] = offBottom };
            Store.SaveSettings();
        }

        void ResetFollowOffsets()
        {
            offRight = DefaultOffRight; offBottom = DefaultOffBottom;
            Store.Settings.Remove("follow"); Store.SaveSettings();
            PlaceInClaude();
        }

        static class FollowNative
        {
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
            delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
            [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
            static WinEventProc foregroundProc;   // 留着引用：委托被回收后系统再回调会崩
            /// <summary>前台窗口换了就调 a（EVENT_SYSTEM_FOREGROUND；WINEVENT_OUTOFCONTEXT，自己进程的不算）</summary>
            public static IntPtr OnForeground(Action a)
            {
                foregroundProc = (_, __, ___, ____, _____, ______, _______) => { try { a(); } catch (Exception e) { Store.Log("前台切换处理出错: " + e.Message); } };
                return SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, foregroundProc, 0, 0, 0x0000 | 0x0002);
            }
            [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
            [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);
            [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

            static string PathOf(uint pid)
            {
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
                try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
                finally { CloseHandle(h); }
            }

            /// <summary>Claude 桌面端的进程（WindowsApps\Claude_*\app\Claude.exe）；名字同样叫 claude.exe 的 Claude Code 命令行不算</summary>
            public static HashSet<uint> ClaudeAppPids()
            {
                var set = new HashSet<uint>();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("claude"))
                {
                    try
                    {
                        var path = PathOf((uint)p.Id);
                        if (path != null && path.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0
                            && path.EndsWith(@"\app\claude.exe", StringComparison.OrdinalIgnoreCase)) set.Add((uint)p.Id);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                return set;
            }

            /// <summary>Claude 的主窗口：Chrome_WidgetWin_1、有标题、不是工具窗口，挑最大的</summary>
            public static IntPtr FindClaudeMain(HashSet<uint> pids)
            {
                IntPtr best = IntPtr.Zero; long bestArea = 0;
                Native.EnumWindows((h, _) =>
                {
                    Native.GetWindowThreadProcessId(h, out var pid);
                    if (!pids.Contains(pid) || !Native.IsWindowVisible(h)) return true;
                    var sb = new StringBuilder(64); GetClassName(h, sb, 64);
                    if (sb.ToString() != "Chrome_WidgetWin_1" || GetWindowTextLength(h) == 0) return true;
                    if ((Native.GetWindowLong(h, -20) & 0x80) != 0) return true;   // WS_EX_TOOLWINDOW
                    if (!Native.GetWindowRect(h, out var r)) return true;
                    long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (a > bestArea) { bestArea = a; best = h; }
                    return true;
                }, IntPtr.Zero);
                return best;
            }

            /// <summary>把她放在 Claude 窗口正上方一层（不置顶）：Claude 上面已经是她就不动，免得每 0.1 秒都重排一次</summary>
            /// <param name="async">从后台线程调时用 SWP_ASYNCWINDOWPOS：交给窗口所在线程去处理，不在这里等</param>
            public static void PlaceAbove(IntPtr pet, IntPtr claude, bool async = false)
            {
                if (claude == IntPtr.Zero) return;
                var prev = GetWindow(claude, 3);   // GW_HWNDPREV：紧挨在 Claude 上面的那个窗口
                if (prev == pet) return;
                var after = IntPtr.Zero;           // HWND_TOP：普通窗口里的最上面
                if (prev != IntPtr.Zero && (Native.GetWindowLong(prev, -20) & 0x8) == 0) after = prev;   // 0x8 = WS_EX_TOPMOST
                Native.SetWindowPos(pet, after, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | (async ? 0x4000u : 0));   // NOSIZE | NOMOVE | NOACTIVATE (| ASYNCWINDOWPOS)
            }
            [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
            [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        }
    }
}
