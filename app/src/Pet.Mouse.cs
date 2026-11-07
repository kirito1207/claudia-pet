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
        // ---------------- 能点到的区域 ----------------
        Canvas hit;
        static readonly Brush HitBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        object[] hitRects, appliedHitRects;
        double appliedHitZoom;
        void UpdateHitRects()
        {
            if (hit == null || !ready || hitRects == null || hit.IsMouseCaptured) return;
            var zoom = Window.Width / pageW;   // 网页像素 → 窗口坐标（画布可能因为文字大小比 260 宽）
            if (hitRects == appliedHitRects && zoom == appliedHitZoom) return;
            hit.Children.Clear();
            foreach (var item in hitRects)
            {
                if (!(item is object[] r) || r.Length != 4) continue;
                var box = new System.Windows.Shapes.Rectangle { Fill = HitBrush, Width = Convert.ToDouble(r[2]) * zoom, Height = Convert.ToDouble(r[3]) * zoom };
                Canvas.SetLeft(box, Convert.ToDouble(r[0]) * zoom);
                Canvas.SetTop(box, Convert.ToDouble(r[1]) * zoom);
                hit.Children.Add(box);
            }
            appliedHitRects = hitRects; appliedHitZoom = zoom;
        }

        // ---------------- 鼠标：单击摸头、拖动换位置、右键菜单 ----------------
        void WireMouse(UIElement hit)
        {
            Point down = default; bool pressed = false;
            hit.MouseLeftButtonDown += (_, e) =>
            {
                StopMotion(); releasedEarly = false;
                var cursor = Forms.Cursor.Position;
                down = new Point(cursor.X, cursor.Y); pressed = true; hit.CaptureMouse(); e.Handled = true;
            };
            hit.MouseMove += (_, e) =>
            {
                // 已经按系统按键状态松手了：排在松开消息前面的旧移动消息不能再开始一次拖动
                if (!pressed || releasedEarly || e.LeftButton != MouseButtonState.Pressed) return;
                if (dragging) return;   // 拖动中的轨迹由每帧的 PumpOnFrame 从系统鼠标历史取
                var cursor = Forms.Cursor.Position;
                var p = new Point(cursor.X, cursor.Y);
                if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) <= 4) return;
                // 积压的旧移动消息晚到时，手可能早就松开了：按键确实还按着才开始拖动
                if (!PrimaryButtonDown()) return;
                BeginDrag(down.X, down.Y);
            };
            hit.MouseLeftButtonUp += (_, e) =>
            {
                var wasDragging = dragging || releasedEarly; releasedEarly = false; EndDrag(e.Timestamp);
                var click = pressed && !wasDragging;
                pressed = false; hit.ReleaseMouseCapture();
                if (!click) return;
                var p = e.GetPosition(hit); var z = Math.Max(Window.Width / pageW, 0.01);
                Js($"handleClick('pat', {Num(Math.Round(p.X / z, 1))}, {Num(Math.Round(p.Y / z, 1))})");
            };
            hit.LostMouseCapture += (_, __) => { pressed = false; if (dragging) StopMotion(); UpdateHitRects(); };
            hit.MouseRightButtonUp += (_, __) =>
            {
                openMenu?.Close();
                var menu = new Forms.ContextMenuStrip();
                FillMenu(menu);
                // 菜单靠「程序失去焦点」来关，可桌宠窗口从不拿焦点（WS_EX_NOACTIVATE），点别的程序它收不到，菜单一直挂着。
                // 开着时自己看：鼠标在菜单（含展开的子菜单）外面按下就关
                var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                watch.Tick += (_, ___) =>
                {
                    if (Forms.Control.MouseButtons != Forms.MouseButtons.None && !MenuCovers(menu, Forms.Cursor.Position)) menu.Close();
                };
                menu.Opened += (_, ___) => watch.Start();
                menu.Closed += (_, ___) =>
                {
                    watch.Stop();
                    if (openMenu == menu) openMenu = null;
                    Window.Dispatcher.BeginInvoke(new Action(menu.Dispose));
                };
                openMenu = menu;
                menu.Show(Forms.Cursor.Position);
            };
        }

        /// <summary>开着的右键菜单：藏起来（全屏、快捷键）时一起关掉，不然桌宠藏了、菜单还单独挂在屏幕上</summary>
        Forms.ContextMenuStrip openMenu;

        static bool MenuCovers(Forms.ToolStripDropDown d, System.Drawing.Point p) =>
            d.Visible && (d.Bounds.Contains(p) || d.Items.OfType<Forms.ToolStripDropDownItem>().Any(i => i.HasDropDownItems && MenuCovers(i.DropDown, p)));
    }
}
