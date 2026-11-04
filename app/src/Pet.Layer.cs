using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Claudia
{
    sealed partial class Pet
    {
        long followEventGeneration;

        // ---- 藏起来 / 露出来 ----
        // 不用 Window.Hide() / Show()：透明分层窗口里嵌着显卡渲染的 WebView2，反复藏和放有概率让 WPF 的渲染线程挂掉
        // （0x88980406 UCEERR_RENDERTHREADFAILURE），之后画面冻住、摸头没反应，只有网页还在跑。
        // 改成「隐身」：窗口一直开着，不透明度 0、鼠标穿透、取消置顶（不压在全屏游戏 / 视频上面），网页动画暂停
        bool concealed, sessionLocked;
        void SetConcealed(bool c)
        {
            if (Window == null || concealed == c) return;
            concealed = c;
            if (c) { openMenu?.Close(); Window.Opacity = 0; if (hwnd != IntPtr.Zero) Native.DropFromTop(hwnd); }
            else { Window.Opacity = 1; }
            // 藏着时鼠标穿过整个窗口（只靠不透明度 0 点不透，原因见 ConcealStyle.ClickThrough）
            if (hwnd != IntPtr.Zero) Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ConcealStyle.ClickThrough(Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE), c));
            PassThroughBrowserWindows();
            UpdatePageHidden();
        }

        /// <summary>网页控件（WebView2CompositionControl）把网页画进我们的窗口，但背后还留着真正的浏览器窗口（msedgewebview2 进程，
        /// Chrome_WidgetWin_1）：位置大小和桌宠窗口一样，排在所有程序下面、桌面上面，看不见却会接住桌面上的点击和拖放——
        /// 藏起来后原来那块点不透、拖文件过去变成「复制」。
        /// 网页从来不用收鼠标（摸头、拖动、右键都是外壳的 hit 层处理），给它也加上鼠标穿透。
        /// 它属于另一个进程、建好的时间不定：网页加载完、藏起来 / 叫回来时各补一次，加过的不再动</summary>
        void PassThroughBrowserWindows()
        {
            if (hwnd == IntPtr.Zero || env == null) return;
            HashSet<uint> pids;
            try { pids = new HashSet<uint>(env.GetProcessInfos().Select(p => (uint)p.ProcessId)); } catch { return; }
            Native.GetWindowRect(hwnd, out var me);
            int n = 0;
            Native.EnumWindows((h, _) =>
            {
                // 只动我们自己的 WebView2 进程里、和桌宠窗口重叠的顶层窗口（别的程序也用 msedgewebview2，不能碰）
                if (h != hwnd && Native.IsWindowVisible(h) && Native.GetWindowThreadProcessId(h, out var pid) != 0 && pids.Contains(pid)
                    && Native.GetWindowRect(h, out var r) && r.Left < me.Right && r.Right > me.Left && r.Top < me.Bottom && r.Bottom > me.Top)
                {
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE), want = ConcealStyle.ClickThrough(ex, true);
                    if (want != ex) { Native.SetWindowLong(h, Native.GWL_EXSTYLE, want); n++; }
                }
                return true;
            }, IntPtr.Zero);
            if (n > 0) Store.Log($"给网页控件背后的浏览器窗口加上鼠标穿透（{n} 个）");
        }
        void UpdatePageHidden() => Js($"setPageHidden({(concealed || sessionLocked || !shownInClaude ? "true" : "false")})");

        // Claude window following owns z-order; only manual hiding is retained.
        void ApplyLayer() { if (Window != null && Window.Topmost) Window.Topmost = false; }
    }
}
