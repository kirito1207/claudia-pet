using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Web.WebView2.WinForms;
using Forms = System.Windows.Forms;

namespace Claudia
{
    /// <summary>
    /// 放桌宠网页的窗口，照 CrossPet（app/windows/src/DirectHost.cs，MIT）改的：普通 WinForms 窗口去掉重定向画布
    /// （WS_EX_NOREDIRECTIONBITMAP）+ 普通 WebView2 子窗口，网页由系统（DWM）直接合成，透明的地方逐像素透出后面。
    /// 以前用 WPF 透明窗口 + WebView2CompositionControl（把网页每一帧截下来贴进 WPF）：
    /// - 换屏 / 换系统缩放后网页区域停在旧缩放的像素上，补对齐、换新控件都不行，只有重启才对（2026-11-22，WebView2 157）；
    /// - 外壳占用和网页帧率成正比（每一帧都要拷进透明窗口，2026-11-06 实测待机外壳 15%）；
    /// - 界面线程一停，画面就冻在半截；反复藏、放还有概率让 WPF 渲染线程挂掉（UCEERR_RENDERTHREADFAILURE）。
    /// 普通 WebView2 子窗口自己跟着显示器缩放走，画面也不经界面线程。
    /// 鼠标：分层窗口（WS_EX_LAYERED，整体不透明度 255）+ 按鼠标位置切 WS_EX_TRANSPARENT——鼠标在能点的那几块上才接，别处穿到后面的窗口。
    /// 网页直接收鼠标，setPointerBridge(true) 后把按下、拖动、单击、右键交给外壳（见 index.html 和 Pet.Mouse.cs）。
    /// 窗口位置、大小一律是物理像素。置顶不在这里管：跟着 Claude 时由 Pet.Follow 的 PlaceAbove 排层级，桌面模式由 Pet.Fullscreen 的 UpdateDeskLayer 按三档摆
    /// </summary>
    sealed class DirectHost : Forms.Form
    {
        public WebView2 Web { get; private set; } = NewWeb();
        static WebView2 NewWeb() => new WebView2 { DefaultBackgroundColor = Color.Transparent, Dock = Forms.DockStyle.Fill };
        readonly DispatcherTimer passTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        List<Rectangle> hitRects = new List<Rectangle>();   // 能点的那几块，窗口内的物理像素
        byte alpha = 255;

        /// <summary>窗口收到的消息先给它看（第一只的全局快捷键 WM_HOTKEY）：返回 true = 处理掉了</summary>
        public Func<int, IntPtr, bool> MessageHook { get; set; }
        /// <summary>每条窗口消息都通知一下（msg、wParam、lParam），不拦截</summary>
        public event Action<int, IntPtr, IntPtr> RawMessage;

        /// <summary>一直让鼠标穿过（藏起来时）：不管鼠标在哪都不接</summary>
        public bool ForcePassThrough
        {
            get => forcePass;
            set { forcePass = value; UpdatePassThrough(); }
        }
        bool forcePass;

        public DirectHost(string title, int width, int height)
        {
            Text = title;
            FormBorderStyle = Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = Forms.FormStartPosition.Manual;
            AutoScaleMode = Forms.AutoScaleMode.None;   // 大小由外壳按画布 × 人物大小 × 系统缩放算好，不让 WinForms 再缩
            Bounds = new Rectangle(-10000, -10000, width, height);   // 先放屏幕外，拿到窗口句柄后按像素摆到该在的位置
            Controls.Add(Web);
            passTimer.Tick += (_, __) => UpdatePassThrough();
            passTimer.Start();
        }

        protected override bool ShowWithoutActivation => true;
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);
        }
        protected override void OnPaintBackground(Forms.PaintEventArgs e) { }   // 没有重定向画布，画了也看不见
        protected override void WndProc(ref Forms.Message m)
        {
            RawMessage?.Invoke(m.Msg, m.WParam, m.LParam);   // 只通知、不拦（AppBar 的全屏通知要 lParam，见 Pet.Fullscreen）
            if (MessageHook != null && MessageHook(m.Msg, m.WParam)) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
            // 换到缩放不同的屏幕、改了系统缩放：外壳按新缩放重设物理像素大小（Pet.Start 里接）。
            // 不靠 Form.DpiChanged：.NET Framework 上它要在 App.config 里另开 WinForms 的按显示器缩放才会来
            if (m.Msg == WM_DPICHANGED) DpiScaleChanged?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>窗口所在屏幕的系统缩放变了（WM_DPICHANGED）</summary>
        public event EventHandler DpiScaleChanged;
        const int WM_DPICHANGED = 0x02E0;

        /// <summary>浏览器进程挂了（网页控件废了）：换一个新的网页控件，返回旧的（调用方 Dispose）</summary>
        public WebView2 ReplaceWeb()
        {
            var old = Web;
            Web = NewWeb();
            Controls.Remove(old);
            Controls.Add(Web);
            return old;
        }

        /// <summary>整个窗口的不透明度：藏起来时 0（以前 WPF 窗口的 Opacity = 0）</summary>
        public void SetVisible(bool on)
        {
            alpha = on ? (byte)255 : (byte)0;
            if (IsHandleCreated) SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);
        }

        public void SetHitRects(IEnumerable<Rectangle> rects) { hitRects = rects.ToList(); UpdatePassThrough(); }
        /// <summary>现在用着的能点区域（外壳测试 tests/MotionHandoff.cs 看）</summary>
        public IReadOnlyList<Rectangle> HitRects => hitRects;

        /// <summary>鼠标在能点的那几块上才接。按住左键时不改：拖动中（窗口跟着鼠标走），或在别的窗口上按着拖过来</summary>
        void UpdatePassThrough()
        {
            if (!IsHandleCreated || IsDisposed) return;
            bool over = false;
            if (!forcePass)
            {
                if ((Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0) return;
                Native.GetWindowRect(Handle, out var wr);
                var p = Forms.Cursor.Position;
                var local = new Point(p.X - wr.Left, p.Y - wr.Top);
                over = hitRects.Any(r => r.Contains(local));
            }
            var ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            var want = over ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
            if (want != ex) Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, want);
        }

        /// <summary>窗口所在屏幕的系统缩放（1 = 100%）</summary>
        public double DpiScale
        {
            get
            {
                if (!IsHandleCreated) return 1;
                try { var d = GetDpiForWindow(Handle); return d > 0 ? d / 96.0 : 1; } catch { return 1; }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) passTimer.Stop();
            base.Dispose(disposing);
        }

        const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        const uint LWA_ALPHA = 2;
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    }
}
