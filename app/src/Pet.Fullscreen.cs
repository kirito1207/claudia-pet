using System;
using System.Runtime.InteropServices;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 桌面上的层级、全屏时让开、鼠标穿透（2026-11-26） ----------------
        // 用户定三档（settings 的 deskLayer）：
        //   top    始终在最上面：什么情况都不让开（独占全屏的游戏会接管整块屏幕，多半还是看不到她）
        //   yield  默认：她所在的屏上有全屏的东西就让开，退出后回来——独占全屏游戏、PPT 放映、浏览器 / TikTok 的视频全屏、
        //          本机播放器全屏都算（11-26 用户实测：原来只认系统报的独占全屏 / 演示模式，PPT 放映和网页全屏系统只报「全屏程序」，没让开）
        //   normal 不置顶：普通窗口的层级，谁都能盖住她
        // 不定时去查：把自己登记成 AppBar，有程序全屏打开 / 关闭时系统发 ABN_FULLSCREENAPP 过来（任务栏就是这么知道的），
        // 再用 SHQueryUserNotificationState 看是不是独占全屏 / 演示模式（系统决定要不要弹通知也看它）。
        // 前台窗口换了（Pet.Follow 的前台钩子）也重排一次：被别的置顶窗口挤下去时由它钉回来，替掉原来每 5 秒一次的 KeepOnTop（登记全屏通知失败时才退回每 5 秒查一次，见 DesktopPlace）。
        static string DeskLayer => Store.App.TryGetValue("deskLayer", out var v) && v is string s &&   // 所有桌宠共用（2026-11-27）
            (s == "top" || s == "yield" || s == "normal") ? s : "yield";
        /// <summary>鼠标穿透（settings 的 clickThrough，默认关）：整个人点不到，点击落到后面的窗口；只能从托盘菜单 / 设置页关</summary>
        static bool ClickThrough => Store.AppOn("clickThrough");   // 所有桌宠一起开关（共用设置，2026-11-27）

        uint appBarMsg;
        bool appBarOn, yielding;
        string lastYieldNote;

        void RegisterAppBar()
        {
            if (appBarOn || hwnd == IntPtr.Zero) return;
            try
            {
                appBarMsg = FullscreenNative.RegisterWindowMessage("ClaudiaPetAppBar");
                var d = FullscreenNative.Data(hwnd, appBarMsg);
                appBarOn = FullscreenNative.SHAppBarMessage(FullscreenNative.ABM_NEW, ref d) != IntPtr.Zero;   // 只登记、不占位置（不调 ABM_SETPOS），工作区不变
                if (!appBarOn) Store.Log("登记全屏通知失败：全屏时不会自动让开");
            }
            catch (Exception e) { Store.Log("登记全屏通知出错: " + e.Message); }
        }

        void UnregisterAppBar()
        {
            if (!appBarOn) return;
            appBarOn = false;
            try { var d = FullscreenNative.Data(hwnd, appBarMsg); FullscreenNative.SHAppBarMessage(FullscreenNative.ABM_REMOVE, ref d); }
            catch (Exception e) { Store.Log("注销全屏通知出错: " + e.Message); }
        }

        /// <summary>DirectHost.RawMessage：AppBar 的回调（wParam = 通知种类，ABN_FULLSCREENAPP 时 lParam = 打开 1 / 关闭 0）</summary>
        void AppBarMessage(int msg, IntPtr wParam, IntPtr lParam)
        {
            if (!appBarOn || msg != (int)appBarMsg || wParam.ToInt64() != FullscreenNative.ABN_FULLSCREENAPP) return;
            Store.Log($"有程序全屏{(lParam != IntPtr.Zero ? "打开" : "关闭")}（系统状态 {FullscreenNative.State()}；{FullscreenNative.Detail(FullscreenNative.GetForegroundWindow(), hwnd)}）");
            UpdateDeskLayer();
        }

        /// <summary>该不该让开：选了「全屏时让开」，而且她这块屏上的前台窗口铺满了整块屏（不是最大化：要盖住任务栏、没有标题栏），
        /// 或者系统说现在是独占全屏（D3D，有的游戏前台窗口量不准）/ 演示模式</summary>
        bool ShouldYield()
        {
            if (DeskLayer != "yield") return false;
            var front = FullscreenNative.GetForegroundWindow();
            string why = null;
            var covers = front != IntPtr.Zero && FullscreenNative.SameMonitor(front, hwnd) && FullscreenNative.CoversMonitor(front, out why);
            // 排查用：她这块屏上的前台窗口铺满整屏却没算全屏（最大化 / 有标题栏）时记一笔，同一个窗口同一个原因只记一次
            var note = why == null ? null : $"{FullscreenNative.Describe(front)} {front.ToInt64():X}：{why}";
            if (note != lastYieldNote) { lastYieldNote = note; if (note != null) Store.Log("前台窗口铺满整屏，" + note); }
            if (covers) return true;
            var st = FullscreenNative.State();
            if (st != FullscreenNative.QUNS_RUNNING_D3D_FULL_SCREEN && st != FullscreenNative.QUNS_PRESENTATION_MODE) return false;
            return front == IntPtr.Zero || FullscreenNative.SameMonitor(front, hwnd);
        }

        /// <summary>桌面模式下按三档摆层级。全屏通知、前台窗口换了、刚摆到桌面上、从隐藏出来、改设置时调；不定时</summary>
        void UpdateDeskLayer()
        {
            if (!desktopMode || concealed || hwnd == IntPtr.Zero) return;
            var yieldNow = ShouldYield();
            if (yieldNow != yielding) Store.Log(yieldNow ? $"有全屏窗口（{FullscreenNative.Describe(FullscreenNative.GetForegroundWindow())}，系统状态 {FullscreenNative.State()}）：让开" : "全屏结束：回到最上面");
            yielding = yieldNow;
            if (DeskLayer == "normal" || yieldNow) Native.DropFromTop(hwnd);
            else Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void SetDeskLayer(object value)
        {
            if (!(value is string s) || (s != "top" && s != "yield" && s != "normal")) return;
            Store.Log("桌面上的层级：" + (s == "top" ? "始终在最上面" : s == "yield" ? "全屏时让开" : "不置顶"));
            SetApp("deskLayer", s);   // 大家一起换
        }

        void SetClickThrough(bool on)
        {
            Store.Log(on ? "鼠标穿透：开" : "鼠标穿透：关");
            SetApp("clickThrough", on);
        }

        /// <summary>改一项共用设置：交给负责人（只有它写 app.json，写完叫大家重读，自己也在里面）</summary>
        void SetApp(string key, object value)
        {
            var text = value is bool b ? (b ? "true" : "false") : value as string ?? "";
            if (hub != null) { hub.Request("appSet", null, key, text); return; }
            if (Store.SetApp(key, text, out var error)) HubAppChanged();
            else HubNotice(error);
        }
    }

    static class FullscreenNative
    {
        public const uint ABM_NEW = 0, ABM_REMOVE = 1;
        public const long ABN_FULLSCREENAPP = 2;
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge;
            public Native.RECT rc; public IntPtr lParam;
        }
        public static APPBARDATA Data(IntPtr hwnd, uint msg) =>
            new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = hwnd, uCallbackMessage = msg };

        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        /// <summary>系统的「用户现在在干嘛」：2 全屏程序 / 3 独占全屏（D3D）/ 4 演示模式 / 5 正常……拿不到时 0</summary>
        public static int State() { try { return SHQueryUserNotificationState(out var s) == 0 ? s : 0; } catch { return 0; } }
        public static bool SameMonitor(IntPtr a, IntPtr b) => MonitorFromWindow(a, 2) == MonitorFromWindow(b, 2);   // MONITOR_DEFAULTTONEAREST

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public Native.RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
        static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        static string ClassOf(IntPtr h) { var b = new System.Text.StringBuilder(64); GetClassName(h, b, 64); return b.ToString(); }
        public static string Describe(IntPtr h) => h == IntPtr.Zero ? "无前台窗口" : ClassOf(GetAncestor(h, 2) is var r && r != IntPtr.Zero ? r : h);

        /// <summary>窗口铺满了它所在的整块屏（显示器范围，不是工作区）：浏览器 / TikTok 视频全屏、PPT 放映、无边框全屏游戏。
        /// 桌面、任务栏、自己的窗口不算；最大化的、带标题栏的不算：用 MyDockFinder 之类、系统任务栏不在 / 自动隐藏时，
        /// 最大化的 Edge 也会铺满整块屏（11-26 用户提醒），但它是最大化状态；视频全屏、F11、PPT 放映、无边框游戏都不是</summary>
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out var v, 4) == 0 && v != 0;

        /// <summary>排查用：层级里排在她上面、看得见、和她有重叠的第一个别人的窗口（类名 + 是否置顶），没有就 null</summary>
        public static string CoveredBy(IntPtr pet)
        {
            if (!Native.GetWindowRect(pet, out var me)) return null;
            var h = pet;
            for (int i = 0; i < 500; i++)
            {
                h = GetWindow(h, 3 /* GW_HWNDPREV：层级里在它上面的那个 */);
                if (h == IntPtr.Zero) return null;
                if (!IsWindowVisible(h) || IsIconic(h) || Cloaked(h)) continue;
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pid == MyPid) continue;
                if (!Native.GetWindowRect(h, out var r) || r.Right <= me.Left || r.Left >= me.Right || r.Bottom <= me.Top || r.Top >= me.Bottom) continue;
                var top = (Native.GetWindowLong(h, Native.GWL_EXSTYLE) & 0x8) != 0;
                return $"{ClassOf(h)} {h.ToInt64():X}（{(top ? "置顶" : "普通")}，进程 {pid}）";
            }
            return null;
        }

        /// <summary>排查用：前台窗口的全部情况（类名、进程、窗口和显示器范围、最大化、样式、和她是不是同一块屏）</summary>
        public static string Detail(IntPtr front, IntPtr pet)
        {
            if (front == IntPtr.Zero) return "无前台窗口";
            var h = GetAncestor(front, 2); if (h == IntPtr.Zero) h = front;
            Native.GetWindowThreadProcessId(h, out var pid);
            Native.GetWindowRect(h, out var r);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
            var m = mi.rcMonitor;
            return $"前台 {ClassOf(h)} {h.ToInt64():X}{(h != front ? $"（子窗口 {ClassOf(front)}）" : "")} 进程 {pid}，" +
                   $"窗口 {r.Left},{r.Top}-{r.Right},{r.Bottom}，屏 {m.Left},{m.Top}-{m.Right},{m.Bottom}，" +
                   $"可见 {IsWindowVisible(h)} 最小化 {IsIconic(h)} 最大化 {IsZoomed(h)} 样式 {Native.GetWindowLong(h, -16):X8}，" +
                   $"和她同屏 {(pet == IntPtr.Zero ? "?" : SameMonitor(h, pet).ToString())}，判断 {(CoversMonitor(h, out var why) ? "全屏" : "不是")}{(why != null ? "（" + why + "）" : "")}";
        }

        public static bool CoversMonitor(IntPtr front) => CoversMonitor(front, out _);
        /// <param name="why">铺满了整屏时说明算不算：「算全屏」「最大化，不算」「有标题栏，不算」；没铺满、不是普通窗口时为 null</param>
        public static bool CoversMonitor(IntPtr front, out string why)
        {
            why = null;
            var h = GetAncestor(front, 2); if (h == IntPtr.Zero) h = front;   // GA_ROOT
            if (!IsWindowVisible(h) || IsIconic(h)) return false;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == MyPid) return false;
            var cls = ClassOf(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            // 系统自己铺满整屏的界面不算：Win11 的 Alt+Tab / 任务视图是 XamlExplorerHostIslandWindow（11-26 日志：按 Alt+Tab 她就让开一下）
            if (cls == "XamlExplorerHostIslandWindow" || cls == "MultitaskingViewFrame" || cls == "ForegroundStaging" || cls == "TaskSwitcherWnd") return false;
            if (!Native.GetWindowRect(h, out var r)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
            var m = mi.rcMonitor;
            if (!(r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom)) return false;
            var style = Native.GetWindowLong(h, -16 /* GWL_STYLE */);
            // 最大化的：带标题栏或可拖动边框（WS_THICKFRAME）的是普通最大化（MyDockFinder 下最大化的 Edge 也铺满整屏），不算；
            // 两样都没有的算全屏——Edge / Chrome 按 F11 或网页视频全屏时窗口仍是最大化，只是去掉了标题栏和边框（11-26 日志）
            if (IsZoomed(h))
            {
                if ((style & 0x00C40000 /* WS_CAPTION | WS_THICKFRAME */) != 0) { why = $"最大化（样式 {style:X8}），不算"; return false; }
                why = $"最大化但没有标题栏和边框（样式 {style:X8}），算全屏"; return true;
            }
            if ((style & 0x00C00000 /* WS_CAPTION */) == 0x00C00000) { why = $"有标题栏（样式 {style:X8}），不算"; return false; }
            why = "算全屏"; return true;
        }
    }
}
