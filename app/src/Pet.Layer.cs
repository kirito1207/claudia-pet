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
        // ---- 藏起来 / 露出来 ----
        // 「隐身」：窗口一直开着，不透明度 0、鼠标穿过整个窗口、取消置顶（不压在全屏游戏 / 视频上面），网页动画暂停。
        // （以前是 WPF 窗口：反复 Hide / Show 有概率让 WPF 渲染线程挂掉，所以一直这么藏；换成 DirectHost 后照旧，省得窗口来回建）
        bool concealed, sessionLocked;
        void SetConcealed(bool c)
        {
            if (Host == null || concealed == c) return;
            concealed = c;
            if (c) { openMenu?.Close(); Host.SetVisible(false); if (hwnd != IntPtr.Zero) Native.DropFromTop(hwnd); }
            else Host.SetVisible(true);
            // 藏着时鼠标穿过整个窗口：不透明度 0 也照样接鼠标（Windows 看的是每个像素自己的透明度，见 ConcealStyle），
            // DirectHost 平时又按鼠标位置切穿透，所以交给它一直穿着
            Host.ForcePassThrough = c || ClickThrough;   // 开着鼠标穿透时露出来也照样穿（Pet.Fullscreen）
            if (!c) UpdateDeskLayer();   // 桌面模式：出来后马上按层级设置摆好
            UpdateFollowMark();   // 藏起来就不算排在 Claude 上面那一组里了
            UpdatePageHidden();
        }
        void UpdatePageHidden() => Js($"setPageHidden({(concealed || sessionLocked || !shownInClaude ? "true" : "false")})");

        // 跟着 Claude 时层级由 PlaceAbove 排、不能置顶；桌面模式的层级见 Pet.Fullscreen 的 UpdateDeskLayer
        void ApplyLayer()
        {
            if (desktopMode || hwnd == IntPtr.Zero) return;
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & 0x8 /* WS_EX_TOPMOST */) != 0)
                Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }
}
