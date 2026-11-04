using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Claudia
{
    /// <summary>
    /// 本地加的：右键菜单换成柔和样式，参照 dsh-pet 的菜单（白底、圆角、细浅边、柔和阴影、圆角悬停块），
    /// 悬停色用 Claude 娘自己的主题橙（#e8a33d 淡化），不用 dsh-pet 的浅蓝。
    /// 圆角和阴影交给 Windows 11 的 DWM（菜单窗口本身变圆角），Windows 10 上退回直角，其他样式照常。
    /// </summary>
    static class SoftMenu
    {
        static readonly Color Back = Color.White;
        static readonly Color Text = Color.FromArgb(0x2b, 0x2b, 0x2b);
        static readonly Color TextDisabled = Color.FromArgb(0xa8, 0xa8, 0xa8);
        static readonly Color Hover = Color.FromArgb(0xFB, 0xEE, 0xDB);        // #e8a33d 约 18% 叠在白底上
        static readonly Color HoverEdge = Color.FromArgb(0xF3, 0xD9, 0xB0);    // 悬停块的一圈淡橙描边
        static readonly Color Arrow = Color.FromArgb(0x9a, 0xa0, 0xa6);
        static readonly Color Line = Color.FromArgb(0xEE, 0xEA, 0xE5);
        static readonly Color Border = Color.FromArgb(0xE2, 0xDD, 0xD6);       // 约等于 rgba(0,0,0,.12)，略带暖色
        static Font font;
        static readonly Renderer renderer = new Renderer();

        public static void Apply(Forms.ToolStripDropDown menu)
        {
            if (font == null)
            {
                try { font = new Font("Microsoft YaHei UI", 9.75f); } catch { font = Forms.SystemInformation.MenuFont; }
            }
            Style(menu);
            if (!(menu.Tag is string t && t == "soft"))
            {
                menu.Tag = "soft";
                menu.Opened += (_, __) => Round(menu);
            }
        }

        static void Style(Forms.ToolStripDropDown dd)
        {
            dd.Renderer = renderer;
            dd.Font = font;
            dd.BackColor = Back;
            dd.Padding = new Forms.Padding(4, 4, 4, 4);
            dd.MinimumSize = new Size(150, 0);
            if (dd is Forms.ToolStripDropDownMenu m) { m.ShowImageMargin = false; m.ShowCheckMargin = false; }
            foreach (Forms.ToolStripItem it in dd.Items)
            {
                it.Font = font;
                it.ForeColor = Text;
                if (it is Forms.ToolStripSeparator) continue;
                it.Padding = new Forms.Padding(6, 4, 6, 4);
                if (it is Forms.ToolStripMenuItem mi && mi.HasDropDownItems)
                {
                    var sub = mi.DropDown;
                    Style(sub);
                    if (!(sub.Tag is string t && t == "soft"))
                    {
                        sub.Tag = "soft";
                        sub.Opened += (_, __) => Round(sub);
                    }
                }
            }
        }

        // ---- Windows 11：让菜单窗口本身变圆角，并给一圈浅色细边（阴影 DWM 会自动给圆角窗口加） ----
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;
        static void Round(Forms.ToolStripDropDown dd)
        {
            try
            {
                if (!dd.IsHandleCreated) return;
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(dd.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                int color = Border.R | (Border.G << 8) | (Border.B << 16);   // COLORREF = 0x00BBGGRR
                DwmSetWindowAttribute(dd.Handle, DWMWA_BORDER_COLOR, ref color, sizeof(int));
            }
            catch { /* 老系统没有这两个属性：保持直角 */ }
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        sealed class Renderer : Forms.ToolStripProfessionalRenderer
        {
            public Renderer() { RoundedEdges = false; }

            protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) => e.Graphics.Clear(Back);
            protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e) { /* 边框交给 DWM（圆角窗口自带细边） */ }
            protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

            protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(1, 1, e.Item.Width - 3, e.Item.Height - 3);
                using (var path = RoundRect(r, 6))
                using (var fill = new SolidBrush(Hover))
                using (var pen = new Pen(HoverEdge))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
            }

            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : TextDisabled;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Arrow;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
            }
        }
    }
}
