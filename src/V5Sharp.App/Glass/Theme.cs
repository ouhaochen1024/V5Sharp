using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace V5Sharp.App;

/// <summary>
/// 配色、字体与 GDI+ 绘制辅助。
///
/// 整体风格参照 Clash Verge：浅灰底 + 纯白卡片 + 单一蓝色强调色，
/// 全屏只允许出现一种强调色，不再使用多色渐变。
/// 唯一保留"液态玻璃"的地方是窗口最外圈的那道半透明边框。
/// </summary>
internal static class Theme
{
    // ==================== 文字 ====================
    public static readonly Color TextPrimary = Color.FromArgb(31, 35, 41);
    public static readonly Color TextSecondary = Color.FromArgb(72, 82, 96);
    public static readonly Color TextMuted = Color.FromArgb(118, 128, 142);
    public static readonly Color TextOnAccent = Color.White;

    // ==================== 强调色（只有一个蓝）====================
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentPress = Color.FromArgb(30, 64, 175);
    /// <summary>浅蓝底：选中项、胶囊、按钮悬停</summary>
    public static readonly Color AccentSoft = Color.FromArgb(239, 245, 255);
    /// <summary>浅蓝描边</summary>
    public static readonly Color AccentLine = Color.FromArgb(198, 219, 255);

    public static readonly Color Success = Color.FromArgb(22, 163, 74);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color DangerSoft = Color.FromArgb(254, 242, 242);
    public static readonly Color Warn = Color.FromArgb(217, 119, 6);

    // ==================== 表面 ====================
    public static readonly Color WindowTop = Color.FromArgb(248, 250, 253);
    public static readonly Color WindowBottom = Color.FromArgb(234, 238, 245);

    public static readonly Color CardFill = Color.FromArgb(255, 255, 255);
    public static readonly Color CardBorder = Color.FromArgb(231, 233, 238);
    public static readonly Color FieldFill = Color.FromArgb(255, 255, 255);
    public static readonly Color FieldBorder = Color.FromArgb(225, 229, 237);
    public static readonly Color ControlHover = Color.FromArgb(244, 246, 250);
    public static readonly Color DividerLine = Color.FromArgb(237, 240, 245);

    /// <summary>浅色提示卡（小贴士）</summary>
    public static readonly Color TintFill = Color.FromArgb(247, 249, 252);
    public static readonly Color TintBorder = Color.FromArgb(232, 237, 245);

    // ==================== 字体 ====================
    public static readonly Font FontUI;
    public static readonly Font FontSmall;
    public static readonly Font FontBold;
    public static readonly Font FontTitle;
    public static readonly Font FontMono;
    public static readonly Font FontHero;
    /// <summary>日志正文：和界面同一套 MiSans。等宽字体没有中文字形，混排会掉回宋体</summary>
    public static readonly Font FontLog;
    /// <summary>Medium：按钮、强调文本（比 Bold 轻一档，MiSans 的 500）</summary>
    public static readonly Font FontMedium;
    /// <summary>小号 Medium：胶囊按钮</summary>
    public static readonly Font FontSmallMedium;

    static Theme()
    {
        string mono = PickFont("Cascadia Mono", "Consolas", "Courier New");
        FontMono = new Font(mono, 9f, FontStyle.Regular, GraphicsUnit.Point);

        // 界面字体：随包内嵌的 MiSans（小米），拿不到才退回系统字体
        FontLoader.EnsureLoaded();

        if (FontLoader.Regular is FontFamily ui)
        {
            FontUI = new Font(ui, 10f, FontStyle.Regular, GraphicsUnit.Point);
            FontSmall = new Font(ui, 9f, FontStyle.Regular, GraphicsUnit.Point);
            FontBold = new Font(ui, 10f, FontStyle.Bold, GraphicsUnit.Point);

            // 日志必须用 FontFamily 对象构造：按族名字符串 new Font("MiSans") 会走 GDI+ 的
            // 已安装字体查找，内嵌字体不在其中，会静默退成 Microsoft Sans Serif。
            FontLog = new Font(ui, 9f, FontStyle.Regular, GraphicsUnit.Point);

            // 标题用 Medium：比 Bold 轻一档，更接近 Clash / macOS 的标题观感
            FontFamily head = FontLoader.Medium ?? ui;
            FontTitle = new Font(head, 11f, FontStyle.Regular, GraphicsUnit.Point);
            FontHero = new Font(head, 12.5f, FontStyle.Regular, GraphicsUnit.Point);
            FontMedium = new Font(head, 10f, FontStyle.Regular, GraphicsUnit.Point);
            FontSmallMedium = new Font(head, 9f, FontStyle.Regular, GraphicsUnit.Point);
            return;
        }

        string fallback = PickFont("MiSans", "Microsoft YaHei UI", "微软雅黑", "Segoe UI");
        FontUI = new Font(fallback, 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        FontSmall = new Font(fallback, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        FontLog = new Font(fallback, 9f, FontStyle.Regular, GraphicsUnit.Point);
        FontBold = new Font(fallback, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        FontTitle = new Font(fallback, 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        FontHero = new Font(fallback, 12f, FontStyle.Bold, GraphicsUnit.Point);
        FontMedium = new Font(fallback, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        FontSmallMedium = new Font(fallback, 9f, FontStyle.Bold, GraphicsUnit.Point);
    }

    private static string PickFont(params string[] candidates)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string c in candidates)
            if (names.Contains(c)) return c;
        return candidates[^1];
    }

    // ==================== 绘制辅助 ====================

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f || r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>白卡片：纯色填充 + 1px 细描边（Clash 的卡片就是这样，没有阴影没有渐变）</summary>
    public static void PaintCard(Graphics g, RectangleF r, float radius, Color fill, Color border)
    {
        if (r.Width < 4 || r.Height < 4) return;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = RoundRect(r, radius))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);

        if (border.A > 0)
        {
            using var path = RoundRect(Deflate(r, 0.5f), Math.Max(1f, radius - 0.5f));
            using var pen = new Pen(border, 1f);
            g.DrawPath(pen, path);
        }

        g.SmoothingMode = old;
    }

    /// <summary>
    /// 窗口最外圈：半透明液态玻璃边。
    /// 由外到内：环形折射描边（上亮下暗）→ 极淡乳白"厚玻璃"→ 底部内阴影 → 顶部镜面高光。
    /// 只作用在窗口边缘，不影响内部扁平卡片。
    /// </summary>
    public static void PaintWindowFrame(Graphics g, RectangleF r, float radius)
    {
        if (r.Width < 20 || r.Height < 20) return;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundRect(r, radius);

        // 1) 玻璃厚度：沿边缘一圈很淡的乳白，向中心迅速消隐（透镜感）
        using (var lens = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(0, 255, 255, 255),
            SurroundColors = new[] { Color.FromArgb(96, 255, 255, 255) },
            FocusScales = new PointF(0.93f, 0.93f)
        })
            g.FillPath(lens, path);

        // 2) 底部内阴影：玻璃下沿压暗一点，"厚度"才立得住
        float bh = Math.Min(r.Height * 0.30f, 64f);
        var band = new RectangleF(r.X, r.Bottom - bh, r.Width, bh);
        using (var bp = RoundRect(band, radius))
        using (var bb = new LinearGradientBrush(
                   new RectangleF(band.X, band.Y, Math.Max(1, band.Width), Math.Max(1, band.Height)),
                   Color.FromArgb(0, 78, 98, 130), Color.FromArgb(20, 78, 98, 130), 90f))
            g.FillPath(bb, bp);

        // 3) 外沿：半透明冷色细线，界定窗口轮廓
        using (var pen = new Pen(Color.FromArgb(112, 108, 126, 156), 1f))
            g.DrawPath(pen, path);

        // 4) 环形描边：上亮下暗，像光从上方穿过玻璃边缘
        var ringRect = Deflate(r, 1f);
        using (var ring = RoundRect(ringRect, Math.Max(2f, radius - 1f)))
        using (var brush = new LinearGradientBrush(ringRect, Color.White, Color.White, 90f))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(236, 255, 255, 255),
                    Color.FromArgb(150, 255, 255, 255),
                    Color.FromArgb(110, 224, 233, 246),
                    Color.FromArgb(176, 255, 255, 255)
                },
                Positions = new[] { 0f, 0.26f, 0.74f, 1f }
            };
            using var pen = new Pen(brush, 1.3f);
            g.DrawPath(pen, ring);
        }

        // 5) 顶部镜面：两角弧线 + 顶边渐隐亮线
        float d = Math.Max(8f, (radius - 1f) * 2f);
        using (var pen = new Pen(Color.FromArgb(198, 255, 255, 255), 1.2f))
        {
            g.DrawArc(pen, new RectangleF(r.X + 1.5f, r.Y + 1.5f, d, d), 196, 66);
            g.DrawArc(pen, new RectangleF(r.Right - d - 1.5f, r.Y + 1.5f, d, d), 278, 66);
        }

        float x0 = r.X + d * 0.5f;
        float bw = Math.Max(6f, r.Width - d);
        var hi = new RectangleF(x0, r.Y + 1.2f, bw, 1.4f);
        using (var brush = new LinearGradientBrush(hi, Color.White, Color.White, 0f))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb(132, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255)
                },
                Positions = new[] { 0f, 0.5f, 1f }
            };
            g.FillRectangle(brush, hi);
        }

        g.SmoothingMode = old;
    }

    public static RectangleF Deflate(RectangleF r, float v)
        => new(r.X + v, r.Y + v, Math.Max(1f, r.Width - v * 2), Math.Max(1f, r.Height - v * 2));

    public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds,
                                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
    {
        TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPrefix);
    }

    public static void DrawTextEllipsis(Graphics g, string text, Font font, Color color, Rectangle bounds,
                                        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
    {
        TextRenderer.DrawText(g, text, font, bounds, color,
            flags | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.PathEllipsis);
    }

    /// <summary>按 alpha 在两个颜色之间插值（用于悬停态，纯色插值不会出现渐变接缝）</summary>
    public static Color Blend(Color fg, Color bg, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        return Color.FromArgb(
            (int)(fg.R * alpha + bg.R * (1 - alpha)),
            (int)(fg.G * alpha + bg.G * (1 - alpha)),
            (int)(fg.B * alpha + bg.B * (1 - alpha)));
    }

    // ==================== 文本度量 ====================

    public static Size MeasureSingle(string text, Font font)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;
        return TextRenderer.MeasureText(text, font, new Size(10000, 10000),
                                        TextFormatFlags.SingleLine);
    }

    public static int MeasureWidth(string text, Font font) => MeasureSingle(text, font).Width;

    public static int MeasureHeight(string text, Font font) => MeasureSingle(text, font).Height;

    public static int MeasureWrappedHeight(string text, Font font, int width)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return TextRenderer.MeasureText(text, font, new Size(Math.Max(16, width), 10000),
                                        TextFormatFlags.WordBreak).Height;
    }

    /// <summary>画一条两端淡出的水平分隔线</summary>
    public static void DrawFadedLine(Graphics g, float x, float y, float w, Color color, float h = 1f)
    {
        if (w <= 2) return;
        var rect = new Rectangle((int)Math.Round(x), (int)Math.Round(y),
                                 (int)Math.Round(w), (int)Math.Round(h));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        int mid = rect.Width / 2;
        var left = new Rectangle(rect.X, rect.Y, mid, rect.Height);
        var right = new Rectangle(rect.X + mid, rect.Y, rect.Width - mid, rect.Height);

        using (var b = new LinearGradientBrush(left, Color.FromArgb(0, color), color, LinearGradientMode.Horizontal))
            g.FillRectangle(b, left);
        using (var b = new LinearGradientBrush(right, color, Color.FromArgb(0, color), LinearGradientMode.Horizontal))
            g.FillRectangle(b, right);
    }
}
