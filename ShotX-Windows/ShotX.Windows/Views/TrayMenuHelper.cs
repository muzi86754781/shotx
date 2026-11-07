using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ShotX.Windows.Views;

/// <summary>
/// 负责 Windows 端托盘右键菜单的 Fluent / macOS 风格自绘渲染器与矢量图标生成器。
/// 1:1 还原 macOS 端菜单项图标、快捷键右对齐及“全能截图”首项高亮样式。
/// </summary>
public static class TrayMenuHelper
{
    public static readonly Color HeroBg = Color.FromArgb(0, 122, 255);        // macOS Vibrant Blue #007AFF
    public static readonly Color HeroHoverBg = Color.FromArgb(0, 98, 204);     // #0062CC
    public static readonly Color TextColor = Color.FromArgb(30, 41, 59);        // #1E293B
    public static readonly Color ShortcutColor = Color.FromArgb(100, 116, 139); // #64748B
    public static readonly Color HoverBg = Color.FromArgb(241, 245, 249);      // #F1F5F9
    public static readonly Color BorderColor = Color.FromArgb(226, 232, 240);   // #E2E8F0
    public static readonly Color MenuBg = Color.FromArgb(255, 255, 255);        // #FFFFFF

    public static Bitmap CreateIcon(string name, Color color, int size = 16)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);

        float s = size;
        float p = s * 0.12f;

        switch (name.ToLowerInvariant())
        {
            case "all_in_one": // 4 squares in 2x2 grid
                float sz = s * 0.32f;
                float gap = s * 0.14f;
                float x0 = p + 0.5f;
                float y0 = p + 0.5f;
                float x1 = x0 + sz + gap;
                float y1 = y0 + sz + gap;
                FillRoundRect(g, brush, x0, y0, sz, sz, 1.2f);
                FillRoundRect(g, brush, x1, y0, sz, sz, 1.2f);
                FillRoundRect(g, brush, x0, y1, sz, sz, 1.2f);
                FillRoundRect(g, brush, x1, y1, sz, sz, 1.2f);
                break;

            case "area": // dashed selection rect
                pen.DashStyle = DashStyle.Dash;
                pen.DashPattern = new float[] { 2.5f, 2.0f };
                g.DrawRectangle(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1);
                break;

            case "prev_area": // clockwise arrow
                g.DrawArc(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1, 45, 270);
                var arrowHead = new PointF[]
                {
                    new(s - p - 1, p),
                    new(s - p + 2.5f, p + 3.5f),
                    new(s - p - 3.5f, p + 3.5f)
                };
                g.FillPolygon(brush, arrowHead);
                break;

            case "fullscreen": // monitor
                float mw = s - 2 * p - 1;
                float mh = (s - 2 * p - 1) * 0.72f;
                g.DrawRectangle(pen, p + 0.5f, p + 0.5f, mw, mh);
                g.DrawLine(pen, s * 0.5f, p + mh + 0.5f, s * 0.5f, s - p - 1);
                g.DrawLine(pen, s * 0.3f, s - p - 1, s * 0.7f, s - p - 1);
                break;

            case "window": // app window
                float ww = s - 2 * p - 1;
                float wh = s - 2 * p - 1;
                g.DrawRectangle(pen, p + 0.5f, p + 0.5f, ww, wh);
                g.DrawLine(pen, p + 0.5f, p + wh * 0.32f, p + ww + 0.5f, p + wh * 0.32f);
                g.FillEllipse(brush, p + 2f, p + 2f, 1.8f, 1.8f);
                g.FillEllipse(brush, p + 4.5f, p + 2f, 1.8f, 1.8f);
                break;

            case "timer": // stopwatch
                g.DrawEllipse(pen, p + 0.5f, p + 2f, s - 2 * p - 1, s - 2 * p - 3);
                g.DrawLine(pen, s * 0.5f, p, s * 0.5f, p + 2f);
                g.DrawLine(pen, s * 0.5f, p + (s - 2 * p - 1) * 0.5f + 1, s * 0.5f, p + 4.5f);
                g.DrawLine(pen, s * 0.5f, p + (s - 2 * p - 1) * 0.5f + 1, s * 0.7f, p + (s - 2 * p - 1) * 0.5f + 1);
                break;

            case "ocr": // viewfinder brackets + text
                float bLen = s * 0.22f;
                // top-left
                g.DrawLines(pen, new PointF[] { new(p + bLen, p), new(p, p), new(p, p + bLen) });
                // top-right
                g.DrawLines(pen, new PointF[] { new(s - p - bLen, p), new(s - p, p), new(s - p, p + bLen) });
                // bottom-left
                g.DrawLines(pen, new PointF[] { new(p, s - p - bLen), new(p, s - p), new(p + bLen, s - p) });
                // bottom-right
                g.DrawLines(pen, new PointF[] { new(s - p - bLen, s - p), new(s - p, s - p), new(s - p, s - p - bLen) });
                // "T" in center
                g.DrawLine(pen, s * 0.35f, s * 0.42f, s * 0.65f, s * 0.42f);
                g.DrawLine(pen, s * 0.5f, s * 0.42f, s * 0.5f, s * 0.68f);
                break;

            case "pin": // pushpin
                g.DrawLine(pen, s * 0.25f, s * 0.45f, s * 0.55f, s * 0.15f);
                g.DrawLine(pen, s * 0.55f, s * 0.15f, s * 0.85f, s * 0.45f);
                g.DrawLine(pen, s * 0.85f, s * 0.45f, s * 0.55f, s * 0.75f);
                g.DrawLine(pen, s * 0.55f, s * 0.75f, s * 0.25f, s * 0.45f);
                pen.Width = 1.6f;
                g.DrawLine(pen, s * 0.4f, s * 0.6f, s * 0.15f, s * 0.85f);
                break;

            case "open_image": // folder
                var folderPath = new GraphicsPath();
                folderPath.AddLines(new PointF[]
                {
                    new(p, p + 2),
                    new(p + s * 0.35f, p + 2),
                    new(p + s * 0.45f, p + 4),
                    new(s - p, p + 4),
                    new(s - p, s - p),
                    new(p, s - p)
                });
                folderPath.CloseFigure();
                g.DrawPath(pen, folderPath);
                break;

            case "edit_clipboard": // clipboard with image/edit
                g.DrawRectangle(pen, p + 1.5f, p + 2.5f, s - 2 * p - 3, s - 2 * p - 3.5f);
                g.FillRectangle(brush, s * 0.38f, p + 1f, s * 0.24f, 2.5f);
                // mini landscape mountain inside
                g.DrawLines(pen, new PointF[]
                {
                    new(p + 3, s - p - 3),
                    new(s * 0.42f, s * 0.55f),
                    new(s * 0.58f, s * 0.68f),
                    new(s * 0.72f, s * 0.5f),
                    new(s - p - 3, s - p - 3)
                });
                break;

            case "color_picker": // eyedropper
                var eyePath = new GraphicsPath();
                eyePath.AddLine(s * 0.7f, s * 0.15f, s * 0.85f, s * 0.3f);
                eyePath.AddLine(s * 0.85f, s * 0.3f, s * 0.55f, s * 0.6f);
                eyePath.AddLine(s * 0.55f, s * 0.6f, s * 0.35f, s * 0.7f);
                eyePath.AddLine(s * 0.35f, s * 0.7f, s * 0.15f, s * 0.85f);
                eyePath.AddLine(s * 0.15f, s * 0.85f, s * 0.3f, s * 0.65f);
                eyePath.AddLine(s * 0.3f, s * 0.65f, s * 0.4f, s * 0.45f);
                eyePath.CloseFigure();
                g.DrawPath(pen, eyePath);
                break;

            case "screen_record": // video camera
                g.DrawRectangle(pen, p, p + 2, s * 0.55f, s - 2 * p - 4);
                var lens = new PointF[]
                {
                    new(p + s * 0.55f, s * 0.42f),
                    new(s - p, p + 3),
                    new(s - p, s - p - 3),
                    new(p + s * 0.55f, s * 0.58f)
                };
                g.FillPolygon(brush, lens);
                break;

            case "audio_record": // mic
                float micW = s * 0.26f;
                float micH = s * 0.46f;
                FillRoundRect(g, brush, s * 0.5f - micW * 0.5f, p + 0.5f, micW, micH, 2.5f);
                g.DrawArc(pen, s * 0.26f, p + 2f, s * 0.48f, s * 0.46f, 0, 180);
                g.DrawLine(pen, s * 0.5f, p + 2f + s * 0.46f, s * 0.5f, s - p - 1.5f);
                g.DrawLine(pen, s * 0.35f, s - p - 1.5f, s * 0.65f, s - p - 1.5f);
                break;

            case "translate_selection": // character / dictionary "字" or "A"
                g.DrawRectangle(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1);
                using (var f = new Font("Arial", 8f, GraphicsUnit.Pixel))
                {
                    g.DrawString("A", f, brush, new PointF(p + 1.5f, p + 1f));
                }
                break;

            case "translate_screenshot": // viewfinder target
                g.DrawEllipse(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1);
                g.FillEllipse(brush, s * 0.5f - 1.5f, s * 0.5f - 1.5f, 3f, 3f);
                break;

            case "translate_input": // globe
                g.DrawEllipse(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1);
                g.DrawEllipse(pen, s * 0.34f, p + 0.5f, s * 0.32f, s - 2 * p - 1);
                g.DrawLine(pen, p + 0.5f, s * 0.5f, s - p - 0.5f, s * 0.5f);
                break;

            case "clipboard_history": // clipboard list
                g.DrawRectangle(pen, p + 1f, p + 2.5f, s - 2 * p - 2, s - 2 * p - 3.5f);
                g.FillRectangle(brush, s * 0.38f, p + 1f, s * 0.24f, 2.5f);
                g.DrawLine(pen, p + 3.5f, s * 0.45f, s - p - 3.5f, s * 0.45f);
                g.DrawLine(pen, p + 3.5f, s * 0.62f, s - p - 3.5f, s * 0.62f);
                g.DrawLine(pen, p + 3.5f, s * 0.79f, s * 0.65f, s * 0.79f);
                break;

            case "desktop_icons": // eye
                var eye = new GraphicsPath();
                eye.AddArc(p, s * 0.26f, s - 2 * p, s * 0.48f, 0, 180);
                eye.AddArc(p, s * 0.26f, s - 2 * p, s * 0.48f, 180, 180);
                g.DrawPath(pen, eye);
                g.FillEllipse(brush, s * 0.5f - 2f, s * 0.5f - 2f, 4f, 4f);
                break;

            case "desktop_icons_slash": // eye slash
                var eye2 = new GraphicsPath();
                eye2.AddArc(p, s * 0.26f, s - 2 * p, s * 0.48f, 0, 180);
                eye2.AddArc(p, s * 0.26f, s - 2 * p, s * 0.48f, 180, 180);
                g.DrawPath(pen, eye2);
                g.FillEllipse(brush, s * 0.5f - 2f, s * 0.5f - 2f, 4f, 4f);
                g.DrawLine(pen, p, p, s - p, s - p);
                break;

            case "history": // clock with circular arrow
                g.DrawArc(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1, 60, 280);
                g.DrawLine(pen, s * 0.5f, s * 0.5f, s * 0.5f, p + 3f);
                g.DrawLine(pen, s * 0.5f, s * 0.5f, s * 0.68f, s * 0.5f);
                break;

            case "support_author": // coffee cup
                float cw = s * 0.55f;
                float ch = s * 0.42f;
                g.DrawRectangle(pen, p + 0.5f, p + 3.5f, cw, ch);
                g.DrawArc(pen, p + cw - 1f, p + 5f, s * 0.22f, ch * 0.6f, -90, 180);
                g.DrawLine(pen, p, p + 3.5f + ch + 1.5f, p + cw + 3.5f, p + 3.5f + ch + 1.5f);
                break;

            case "preferences": // gear
                g.DrawEllipse(pen, s * 0.35f, s * 0.35f, s * 0.3f, s * 0.3f);
                pen.Width = 1.6f;
                for (int i = 0; i < 6; i++)
                {
                    double angle = i * Math.PI / 3;
                    float gx1 = s * 0.5f + (float)(Math.Cos(angle) * (s * 0.28f));
                    float gy1 = s * 0.5f + (float)(Math.Sin(angle) * (s * 0.28f));
                    float gx2 = s * 0.5f + (float)(Math.Cos(angle) * (s * 0.45f));
                    float gy2 = s * 0.5f + (float)(Math.Sin(angle) * (s * 0.45f));
                    g.DrawLine(pen, gx1, gy1, gx2, gy2);
                }
                break;

            case "check_update": // circle with down arrow
                g.DrawEllipse(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1);
                g.DrawLine(pen, s * 0.5f, p + 3f, s * 0.5f, s - p - 3.5f);
                g.DrawLine(pen, s * 0.35f, s * 0.55f, s * 0.5f, s - p - 3.5f);
                g.DrawLine(pen, s * 0.65f, s * 0.55f, s * 0.5f, s - p - 3.5f);
                break;

            case "about": // info circle
                g.DrawEllipse(pen, p + 0.5f, p + 0.5f, s - 2 * p - 1, s - 2 * p - 1);
                g.FillEllipse(brush, s * 0.5f - 1f, p + 3.5f, 2f, 2f);
                pen.Width = 1.5f;
                g.DrawLine(pen, s * 0.5f, s * 0.45f, s * 0.5f, s - p - 3.5f);
                break;

            case "exit": // power
                g.DrawArc(pen, p + 0.5f, p + 1.5f, s - 2 * p - 1, s - 2 * p - 2, -60, 300);
                g.DrawLine(pen, s * 0.5f, p, s * 0.5f, s * 0.48f);
                break;

            default:
                g.FillEllipse(brush, s * 0.35f, s * 0.35f, s * 0.3f, s * 0.3f);
                break;
        }

        return bmp;
    }

    private static void FillRoundRect(Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        using var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    /// <summary>
    /// 现代化 Fluent / macOS 托盘右键菜单自绘渲染器
    /// </summary>
    public class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        public ModernMenuRenderer() : base(new ModernColorTable())
        {
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);

            bool isHero = (e.Item.Tag as string) == "Hero";

            if (isHero)
            {
                var bg = e.Item.Selected ? HeroHoverBg : HeroBg;
                using var brush = new SolidBrush(bg);
                FillRoundedRectangle(g, brush, bounds, 5);
                return;
            }

            if (e.Item.Selected && e.Item.Enabled)
            {
                using var brush = new SolidBrush(HoverBg);
                FillRoundedRectangle(g, brush, bounds, 4);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool isHero = (e.Item.Tag as string) == "Hero";
            if (isHero)
            {
                e.TextColor = Color.White;
            }
            else if (!e.Item.Enabled)
            {
                e.TextColor = Color.FromArgb(148, 163, 184);
            }
            else
            {
                e.TextColor = TextColor;
            }

            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            int y = e.Item.Height / 2;
            using var pen = new Pen(BorderColor, 1);
            g.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            using var pen = new Pen(BorderColor, 1);
            g.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        private static void FillRoundedRectangle(Graphics g, Brush brush, Rectangle rect, int radius)
        {
            using var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }

    private class ModernColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => BorderColor;
        public override Color ToolStripDropDownBackground => MenuBg;
        public override Color ImageMarginGradientBegin => MenuBg;
        public override Color ImageMarginGradientMiddle => MenuBg;
        public override Color ImageMarginGradientEnd => MenuBg;
        public override Color SeparatorDark => BorderColor;
        public override Color SeparatorLight => Color.Transparent;
        public override Color MenuItemSelected => HoverBg;
        public override Color MenuItemBorder => Color.Transparent;
    }
}

