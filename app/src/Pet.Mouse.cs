using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 能点到的区域 ----------------
        // 网页报来（hitRects 消息，画布坐标）：桌宠本体、气泡、名牌、额度。换成窗口里的物理像素交给 DirectHost：
        // 鼠标在这几块上窗口才接，别处整个穿过去，点击直接落到后面的窗口（窗口比桌宠大，是给动作、气泡留的空间）
        object[] hitRects, appliedHitRects;
        int appliedHitWidth;
        void UpdateHitRects()
        {
            if (Host == null || !ready || hitRects == null) return;
            var width = Host.Width;   // 物理像素
            if (hitRects == appliedHitRects && width == appliedHitWidth) return;
            var zoom = width / pageW;   // 画布像素 → 窗口里的物理像素（画布可能因为文字大小比 380 宽）
            Host.SetHitRects(HitRectsToWindow(hitRects, zoom));
            appliedHitRects = hitRects; appliedHitWidth = width;
        }
        static IEnumerable<System.Drawing.Rectangle> HitRectsToWindow(object[] rects, double zoom) =>
            rects.OfType<object[]>().Where(r => r.Length == 4).Select(r => new System.Drawing.Rectangle(
                (int)Math.Floor(Convert.ToDouble(r[0]) * zoom), (int)Math.Floor(Convert.ToDouble(r[1]) * zoom),
                (int)Math.Ceiling(Convert.ToDouble(r[2]) * zoom), (int)Math.Ceiling(Convert.ToDouble(r[3]) * zoom)));

        // ---------------- 鼠标：单击摸头、拖动换位置、右键菜单 ----------------
        // 网页收到鼠标后交来（pointer 消息，见 index.html 的 setPointerBridge）。以前是 WPF 窗口里一层透明画布自己接鼠标。
        // 拖动开始以后网页就不再报了：轨迹由每帧从系统鼠标历史取，松手按物理按键状态判断（见 Pet.Physics.cs 的 PumpOnFrame）
        double downX, downY;   // 左键按下时鼠标在屏幕上的位置（物理像素）：拖动从哪儿拎起来的
        bool pressed;          // 按下了、还没开始拖、也还没单击
        bool HandlePointer(Dictionary<string, object> m)
        {
            if (!(m.TryGetValue("pointer", out var pv) && pv is string pointer)) return false;
            switch (pointer)
            {
                case "down":
                    StopMotion(); releasedEarly = false;   // 飞着的时候一把抓住
                    var cursor = Forms.Cursor.Position;
                    downX = cursor.X; downY = cursor.Y; pressed = true;
                    break;
                case "drag":
                    if (!pressed || dragging) break;
                    pressed = false;
                    // 消息晚到时手可能早就松开了：按键确实还按着才开始拖动
                    if (PrimaryButtonDown()) BeginDrag(downX, downY);
                    break;
                case "click":
                    pressed = false;
                    double x = m.TryGetValue("x", out var xv) ? Convert.ToDouble(xv) : 0, y = m.TryGetValue("y", out var yv) ? Convert.ToDouble(yv) : 0;
                    Js($"handleClick('pat', {Num(x)}, {Num(y)})");
                    break;
                case "menu":
                    pressed = false;
                    ui.BeginInvoke(new Action(ShowMenu));   // 等网页控件的回调返回再弹
                    break;
            }
            return true;
        }

        void ShowMenu()
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
                ui.BeginInvoke(new Action(menu.Dispose));
            };
            openMenu = menu;
            menu.Show(Forms.Cursor.Position);
        }

        /// <summary>开着的右键菜单：藏起来（全屏、快捷键）时一起关掉，不然桌宠藏了、菜单还单独挂在屏幕上</summary>
        Forms.ContextMenuStrip openMenu;

        static bool MenuCovers(Forms.ToolStripDropDown d, System.Drawing.Point p) =>
            d.Visible && (d.Bounds.Contains(p) || d.Items.OfType<Forms.ToolStripDropDownItem>().Any(i => i.HasDropDownItems && MenuCovers(i.DropDown, p)));
    }
}
