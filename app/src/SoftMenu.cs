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
        // 菜单项横向尺寸（96 DPI 下的像素，按屏幕缩放放大）：TextOrigin 是 WinForms 按 Padding.Left = 6 画字的起点，
        // 最长那项文字的最后一笔后面统一留 RightBlank（三级一样）；有子菜单的箭头 WinForms 画在离右边约 12 处，落在这段留白里
        const int TextOrigin = 12, RightBlank = 32, ArrowGap = 8;
        static Font font;
        static readonly Renderer renderer = new Renderer();

        public static void Apply(Forms.ToolStripDropDown menu)
        {
            if (font == null)
            {
                try { font = new Font("Microsoft YaHei UI", 9.75f); } catch { font = Forms.SystemInformation.MenuFont; }
            }
            Style(menu);
        }

        static void Style(Forms.ToolStripDropDown dd)
        {
            dd.Renderer = renderer;
            dd.Font = font;
            dd.BackColor = Back;
            // 不设 Padding / MinimumSize：两个边栏都关掉后 WinForms 不认 Padding（读回来总是左 8 右 1），
            // MinimumSize 撑宽了窗口却不撑宽菜单项，右边留一大块空白，下一级还按菜单项右边缘摆、盖住这块空白
            if (dd is Forms.ToolStripDropDownMenu m) { m.ShowImageMargin = false; m.ShowCheckMargin = false; }
            if (!(dd.Tag is string t && t == "soft"))
            {
                dd.Tag = "soft";
                dd.Opening += (_, __) => FitWidth(dd);
                dd.Opened += (_, __) => { Round(dd); KeepDirection(dd); };
            }
            foreach (Forms.ToolStripItem it in dd.Items)
            {
                it.Font = font;
                it.ForeColor = Text;
                if (it is Forms.ToolStripSeparator) continue;
                it.Padding = new Forms.Padding(6, 4, 6, 4);
                if (it is Forms.ToolStripMenuItem mi && mi.HasDropDownItems) Style(mi.DropDown);
            }
        }

        /// <summary>
        /// 窗口宽度对齐菜单项。两个边栏都关掉时 WinForms 把菜单项排得比窗口宽 11 像素（从 x=0 排起），
        /// 下一级按菜单项右边缘摆，于是向右展开时两级之间空出一条缝，向左展开时又压进上一级。
        /// </summary>
        static void FitWidth(Forms.ToolStripDropDown dd)
        {
            // 菜单项宽度自己定：WinForms 给每项右边预留的一截（箭头列 + 内边距）太长，最长那项文字后面还空 54–68 像素。
            // 改成 文字左边距 + 最长文字 + 统一的右边留白，三级一样宽的留白；有子菜单的箭头画在留白里（见 OnRenderArrow）
            float s = dd.DeviceDpi / 96f;
            int text = 0, hItem = 0;
            foreach (Forms.ToolStripItem it in dd.Items)
            {
                if (!(it is Forms.ToolStripMenuItem) || !it.Available) continue;
                text = Math.Max(text, InkRight(it.Text, it.Font));
            }
            int itemW = (int)Math.Round(TextOrigin * s) + text + (int)Math.Round(RightBlank * s);
            foreach (Forms.ToolStripItem it in dd.Items)
            {
                if (!(it is Forms.ToolStripMenuItem)) continue;
                it.AutoSize = true;
                hItem = it.GetPreferredSize(Size.Empty).Height;
                it.AutoSize = false;
                it.Size = new Size(itemW, hItem);
            }
            dd.AutoSize = true;          // 先让它照常量一遍（托盘菜单每次重填，上次的宽度不能留着）
            dd.PerformLayout();
            int w = 0;
            // 只看菜单项：分隔线会被 WinForms 拉满整个窗口，算进来就把窗口按旧宽度撑住了
            foreach (Forms.ToolStripItem it in dd.Items) if (it.Available && it is Forms.ToolStripMenuItem) w = Math.Max(w, it.Bounds.Right);
            if (w <= 0) return;
            int h = dd.PreferredSize.Height;
            dd.AutoSize = false;
            dd.Size = new Size(w, h);
        }

        /// <summary>
        /// 文字最后一笔（有墨的最右一列）离起笔点多远。量字宽的接口会把字形自带的空白算进去，
        /// 全角「）」一个就多出十来像素，结尾是括号的那级留白会比别的级宽一截，所以画出来找墨
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<string, int> inkCache = new System.Collections.Generic.Dictionary<string, int>();
        static int InkRight(string text, Font f)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var key = f.Name + "|" + f.Size + "|" + text;
            if (inkCache.TryGetValue(key, out var v)) return v;
            var size = Forms.TextRenderer.MeasureText(text, f, Size.Empty, Forms.TextFormatFlags.NoPadding);
            v = size.Width;
            using (var bmp = new Bitmap(size.Width + 8, size.Height + 4))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    Forms.TextRenderer.DrawText(g, text, f, Point.Empty, Color.Black, Color.White, Forms.TextFormatFlags.NoPadding);
                }
                for (int x = bmp.Width - 1; x >= 0; x--)
                {
                    bool ink = false;
                    for (int y = 0; y < bmp.Height && !ink; y++) { var c = bmp.GetPixel(x, y); ink = c.R < 160 && c.G < 160 && c.B < 160; }
                    if (ink) { v = x + 1; break; }
                }
            }
            inkCache[key] = v;
            return v;
        }

        /// <summary>
        /// 这一级是往左开的，它的下一级也往左开。WinForms 每一级都重新先试右边：
        /// 二级因为屏幕右边放不下翻到了一级左边，三级就又朝右开、整块盖在一级上面，鼠标在上面移动时会闪。
        /// 往左放不下时 WinForms 自己会再翻回右边。
        /// </summary>
        static void KeepDirection(Forms.ToolStripDropDown dd)
        {
            var parent = (dd.OwnerItem as Forms.ToolStripItem)?.Owner as Forms.ToolStripDropDown;
            bool leftward = parent != null && dd.Bounds.Right <= parent.Bounds.Left + 1;
            foreach (Forms.ToolStripItem it in dd.Items)
                if (it is Forms.ToolStripMenuItem mi && mi.HasDropDownItems)
                    mi.DropDownDirection = leftward ? Forms.ToolStripDropDownDirection.Left : Forms.ToolStripDropDownDirection.Default;
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
                if (e.Item != null)
                {
                    // 菜单项定宽后 WinForms 把箭头贴到离右边两三像素，挪回离边 ArrowGap（和左边文字的 13 差不多对称）
                    var r = e.ArrowRectangle;
                    float s = (e.Item.Owner?.DeviceDpi ?? 96) / 96f;
                    r.X = e.Item.Width - (int)Math.Round(ArrowGap * s) - r.Width;
                    e.ArrowRectangle = r;
                }
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
