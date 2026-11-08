using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace V5Sharp.App;

/// <summary>
/// 配色、字体与 GDI+ 绘制辅助。
///
/// 视觉语言按 macOS / iOS 的浅色界面来做：中性灰底（不带蓝调）、纯白分组卡片、
/// 单一系统蓝，靠字号与字重的层级而不是彩色装饰线来建立结构。
/// 灰阶取 Apple 的 label / separator 体系，但整体压深一档，保证小字号中文可读。
/// </summary>
internal static class Theme
{
    // ==================== 文字 ====================
    /// <summary>label：正文与标题</summary>
    public static readonly Color TextPrimary = Color.FromArgb(29, 29, 31);
    /// <summary>secondaryLabel：说明文字、表单标签</summary>
    public static readonly Color TextSecondary = Color.FromArgb(74, 74, 79);
    /// <summary>tertiaryLabel：表头、占位、次要信息</summary>
    public static readonly Color TextMuted = Color.FromArgb(134, 134, 139);
    public static readonly Color TextOnAccent = Color.White;

    // ==================== 强调色（systemBlue）====================
    public static readonly Color Accent = Color.FromArgb(0, 122, 255);
    public static readonly Color AccentHover = Color.FromArgb(26, 140, 255);
    public static readonly Color AccentPress = Color.FromArgb(0, 98, 204);
    /// <summary>tinted 底：选中行、浅色胶囊（iOS 的二次按钮底色）</summary>
    public static readonly Color AccentSoft = Color.FromArgb(235, 243, 255);
    /// <summary>浅蓝描边</summary>
    public static readonly Color AccentLine = Color.FromArgb(201, 224, 255);

    public static readonly Color Success = Color.FromArgb(52, 199, 89);
    /// <summary>绿色文字：systemGreen 当 9pt 小字太浅（白底上对比不足），压深一档</summary>
    public static readonly Color SuccessText = Color.FromArgb(36, 138, 61);
    public static readonly Color Danger = Color.FromArgb(255, 59, 48);
    public static readonly Color DangerSoft = Color.FromArgb(255, 240, 239);
    public static readonly Color Warn = Color.FromArgb(255, 149, 0);
    /// <summary>橙色文字：同理压深</summary>
    public static readonly Color WarnText = Color.FromArgb(194, 106, 0);

    // ==================== 表面 ====================
    /// <summary>窗口底：systemGray6 附近的中性灰，上下几乎持平，只留一丝明暗</summary>
    public static readonly Color WindowTop = Color.FromArgb(247, 247, 249);
    public static readonly Color WindowBottom = Color.FromArgb(237, 237, 241);

    public static readonly Color CardFill = Color.FromArgb(255, 255, 255);
    public static readonly Color CardBorder = Color.FromArgb(229, 229, 234);
    public static readonly Color FieldFill = Color.FromArgb(255, 255, 255);
    /// <summary>输入框描边：separator 再深一点，白底上要能看出边界</summary>
    public static readonly Color FieldBorder = Color.FromArgb(209, 209, 214);
    public static readonly Color ControlHover = Color.FromArgb(242, 242, 247);
    public static readonly Color DividerLine = Color.FromArgb(229, 229, 234);

    /// <summary>分组底（小贴士）：systemGray6，不用白底也不用蓝底</summary>
    public static readonly Color TintFill = Color.FromArgb(242, 242, 247);
    public static readonly Color TintBorder = Color.FromArgb(229, 229, 234);
    /// <summary>日志面板底：比卡片灰一档，形成"内嵌"关系</summary>
    public static readonly Color InsetFill = Color.FromArgb(246, 246, 248);

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

            // 标题用 Medium：比 Bold 轻一档，更接近 macOS 的 headline
            FontFamily head = FontLoader.Medium ?? ui;
            FontTitle = new Font(head, 11.5f, FontStyle.Regular, GraphicsUnit.Point);
            FontHero = new Font(head, 13f, FontStyle.Regular, GraphicsUnit.Point);
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

    /// <summary>
    /// 分组卡片：纯白填充 + 1px separator 描边。
    /// Apple 的浅色卡片几乎不投影，层次靠"灰底上放白块"和一道极细的边界，不做渐变。
    /// </summary>
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
    /// 窗口最外圈。原来的"液态玻璃"有折射圈、乳白透镜、底部重阴影，
    /// 在浅色界面上会显出一种塑料感；Apple 的窗口是一块干净的圆角板，
    /// 只有一道中性描边和顶边的一点光。这里按后者重做。
    /// </summary>
    public static void PaintWindowFrame(Graphics g, RectangleF r, float radius)
    {
        if (r.Width < 20 || r.Height < 20) return;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundRect(r, radius);

        // 1) 底部极淡内阴影：只为了不让下沿"糊"进桌面，强度低到几乎察觉不到
        float bh = Math.Min(r.Height * 0.20f, 40f);
        var band = RectangleF.FromLTRB(r.X, r.Bottom - bh, r.Right, r.Bottom);
        using (var bp = RoundRect(band, radius))
        using (var bb = new LinearGradientBrush(
                   new RectangleF(band.X, band.Y, Math.Max(1, band.Width), Math.Max(1, band.Height)),
                   Color.FromArgb(0, 60, 60, 67), Color.FromArgb(10, 60, 60, 67), 90f))
            g.FillPath(bb, bp);

        // 2) 轮廓：一道中性灰细线，不做彩色折射
        using (var pen = new Pen(Color.FromArgb(160, 209, 209, 214), 1f))
            g.DrawPath(pen, path);

        // 3) 顶边高光：光从上方落下，只亮上半圈，到腰线归零
        var ringRect = Deflate(r, 1f);
        using (var ring = RoundRect(ringRect, Math.Max(2f, radius - 1f)))
        using (var brush = new LinearGradientBrush(ringRect, Color.White, Color.White, 90f))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(132, 255, 255, 255),
                    Color.FromArgb(44, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255)
                },
                Positions = new[] { 0f, 0.42f, 1f }
            };
            using var pen = new Pen(brush, 1f);
            g.DrawPath(pen, ring);
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
