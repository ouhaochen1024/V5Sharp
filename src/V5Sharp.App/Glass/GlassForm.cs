using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace V5Sharp.App;

/// <summary>
/// 无边框窗体：自绘浅色底 + 半透明液态玻璃外框、自绘标题栏、支持拖动/缩放/最大化。
/// 内部一律扁平化（Clash 风格），只有最外圈保留玻璃质感。
/// </summary>
public class GlassForm : Form
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int ResizeBorder = 6;
    private const float FrameRadius = 14f;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private Bitmap? _wallpaper;
    /// <summary>当前背景位图对应的高度（背景只按高度变化，宽度靠横向拉伸）</summary>
    private int _wallpaperH = -1;
    private Rectangle _btnMin, _btnMax, _btnClose, _btnAction;
    private int _hoverButton = -1;
    private bool _hoverAction;

    /// <summary>左上角 "V5" 标识用的字重：缓存，避免每次 OnPaint 都 new Font 泄漏 GDI 对象</summary>
    private static Font? _brandFont;
    private static Font BrandFont => _brandFont ??= new Font(Theme.FontUI.FontFamily, 8f, FontStyle.Bold);

    private string _titleText = "V5Sharp";

    public string TitleText
    {
        get => _titleText;
        set { _titleText = value; Text = value; }   // 同步窗口标题，任务栏/Alt-Tab 才正常
    }

    public string SubtitleText { get; set; } = "";

    private int _titleBarHeight = -1;
    /// <summary>标题栏高度：不显式指定时按 DPI 缩放，与界面其余尺寸保持一致</summary>
    public int TitleBarHeight
    {
        get => _titleBarHeight > 0 ? _titleBarHeight : S(56);
        set => _titleBarHeight = value;
    }

    /// <summary>标题栏右侧的胶囊入口文字（如「官方」），为空则不显示</summary>
    public string TitleActionText { get; set; } = "";

    /// <summary>标题栏胶囊被点击时触发</summary>
    public event EventHandler? TitleActionClicked;

    /// <summary>按显示器 DPI 缩放设计尺寸</summary>
    protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    public GlassForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.WindowTop;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Font = Theme.FontUI;
        Text = _titleText;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, sizeof(int));
        }
        catch { /* Win10 不支持，忽略 */ }
        UpdateMaximizedBounds();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 背景按高度缓存，宽度靠横向拉伸，所以纯横向缩放不需要重建
        UpdateMaximizedBounds();
        Invalidate();
    }

    private void UpdateMaximizedBounds()
    {
        var screen = Screen.FromControl(this);
        if (screen is not null) MaximizedBounds = screen.WorkingArea;
    }

    // ==================== 背景 ====================

    /// <summary>
    /// 纯浅灰底：和 Clash 一样几乎无色彩，只有极轻微的垂直明暗过渡，
    /// 让白色卡片浮得起来，又不会显得花。
    ///
    /// 缓存的是 1×高 的竖向渐变条，绘制时横向拉伸：竖向渐变每一行都是同色，
    /// 拉伸后与整幅位图完全等价，却把每次缩放要分配的 4MB 降到几 KB。
    /// </summary>
    private void EnsureWallpaper()
    {
        if (_wallpaper is not null && _wallpaperH == Height) return;
        if (Width <= 0 || Height <= 0) return;

        _wallpaper?.Dispose();
        var bmp = new Bitmap(1, Height);
        using (var g = Graphics.FromImage(bmp))
        using (var bg = new LinearGradientBrush(
                   new Rectangle(0, 0, 1, Height), Theme.WindowTop, Theme.WindowBottom, 90f))
            g.FillRectangle(bg, 0, 0, 1, Height);

        _wallpaper = bmp;
        _wallpaperH = Height;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        EnsureWallpaper();
        if (_wallpaper is null) { e.Graphics.Clear(BackColor); return; }

        // TileFlipXY：1px 宽的图横向放大时，边缘按镜像取样，不会在左右两侧留下渐变接缝
        using var attr = new ImageAttributes();
        attr.SetWrapMode(WrapMode.TileFlipXY);
        e.Graphics.DrawImage(_wallpaper, new Rectangle(0, 0, Width, Height),
                             0, 0, _wallpaper.Width, _wallpaper.Height, GraphicsUnit.Pixel, attr);
    }

    // ==================== 前景 ====================

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        LayoutTitleButtons();

        // 最外圈：半透明液态玻璃边框（唯一的玻璃元素）。
        // 这些效果只影响边缘一圈，把绘制裁剪到外框带内，GDI+ 不必为整窗（上百万像素）
        // 做路径渐变填充 —— 拖动缩放时的绘制开销能降到十分之一左右。
        var state = g.Save();
        using (var band = BuildFrameBand(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f)))
            g.SetClip(band, CombineMode.Intersect);

        Theme.PaintWindowFrame(g,
            new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), FrameRadius);

        g.Restore(state);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 标题栏与应用标识
        var mark = new Rectangle(S(20), (TitleBarHeight - S(26)) / 2, S(26), S(26));
        using (var p = Theme.RoundRect(mark, S(7)))
        using (var b = new SolidBrush(Theme.Accent))
            g.FillPath(b, p);
        Theme.DrawText(g, "V5", BrandFont, Color.White,
                       mark, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int textX = mark.Right + S(10);
        int titleW = Math.Max(S(40), Width - textX - S(220));
        Theme.DrawText(g, TitleText, Theme.FontTitle, Theme.TextPrimary,
                       new Rectangle(textX, S(9), titleW, S(22)));
        if (!string.IsNullOrEmpty(SubtitleText))
            Theme.DrawText(g, SubtitleText, Theme.FontSmall, Theme.TextMuted,
                           new Rectangle(textX + 1, S(31), titleW, S(18)));

        DrawTitleAction(g);

        DrawChromeButton(g, _btnMin, 0);
        DrawChromeButton(g, _btnMax, 1);
        DrawChromeButton(g, _btnClose, 2);

        // 标题栏与内容区之间的细线
        using (var pen = new Pen(Theme.DividerLine, 1f))
            g.DrawLine(pen, 0, TitleBarHeight - 0.5f, Width, TitleBarHeight - 0.5f);
    }

    /// <summary>
    /// 外框带：整窗矩形减去向内收 <see cref="FrameBand"/> 的矩形。
    /// 液态玻璃的透镜渐变、底部内阴影、描边全部落在这一圈里，带外像素本来就是全透明。
    /// </summary>
    private static Region BuildFrameBand(RectangleF full)
    {
        const float FrameBand = 72f;
        var band = new Region();
        band.MakeEmpty();
        band.Union(full);

        float inX = full.X + FrameBand;
        float inY = full.Y + FrameBand;
        float inW = full.Width - FrameBand * 2;
        float inH = full.Height - FrameBand * 2;
        if (inW > 0 && inH > 0)
            band.Exclude(new RectangleF(inX, inY, inW, inH));

        return band;
    }

    private void LayoutTitleButtons()
    {
        int size = S(30);
        int y = (TitleBarHeight - size) / 2;
        int right = Width - S(18);
        _btnClose = new Rectangle(right - size, y, size, size);
        _btnMax = new Rectangle(_btnClose.Left - size - S(6), y, size, size);
        _btnMin = new Rectangle(_btnMax.Left - size - S(6), y, size, size);

        // 标题栏胶囊入口：放在最小化按钮左侧，宽度按文字自适应
        if (!string.IsNullOrEmpty(TitleActionText))
        {
            int textW = Theme.MeasureWidth(TitleActionText, Theme.FontSmall);
            int h = S(26);
            int w = textW + S(34);          // 文字 + 左侧图标 + 内边距
            int x = _btnMin.Left - S(16) - w;
            int cy = (TitleBarHeight - h) / 2;
            _btnAction = new Rectangle(x, cy, w, h);
        }
        else
        {
            _btnAction = Rectangle.Empty;
        }
    }

    /// <summary>绘制标题栏右侧的胶囊入口（默认「官方」）：浅蓝底 + 细蓝边</summary>
    private void DrawTitleAction(Graphics g)
    {
        if (_btnAction.IsEmpty || string.IsNullOrEmpty(TitleActionText)) return;

        var r = _btnAction;
        bool hover = _hoverAction;

        var fill = hover ? Color.FromArgb(226, 238, 255) : Theme.AccentSoft;
        var line = hover ? Color.FromArgb(166, 197, 255) : Theme.AccentLine;

        using (var p = Theme.RoundRect(r, r.Height / 2f))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, p);
        using (var p = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1),
                                       r.Height / 2f))
        using (var pen = new Pen(line, 1f))
            g.DrawPath(pen, p);

        var fg = hover ? Theme.AccentHover : Theme.Accent;

        // 地球小图标
        float cx = r.Left + 15, cy = r.Y + r.Height / 2f, rad = 5.6f;
        using (var pen = new Pen(fg, 1.1f))
        {
            g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
            g.DrawLine(pen, cx - rad, cy, cx + rad, cy);
            g.DrawEllipse(pen, cx - rad / 2.2f, cy - rad, rad / 1.1f, rad * 2);
        }

        Theme.DrawText(g, TitleActionText, Theme.FontSmall, fg,
                       new Rectangle(r.Left + 24, r.Y, r.Width - 24, r.Height),
                       TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawChromeButton(Graphics g, Rectangle r, int kind)
    {
        bool hover = _hoverButton == kind;
        bool close = kind == 2;

        if (hover)
        {
            using var p = Theme.RoundRect(r, 7);
            using var b = new SolidBrush(close ? Color.FromArgb(232, 17, 35) : Color.FromArgb(240, 242, 246));
            g.FillPath(b, p);
        }

        var color = hover && close ? Color.White : Theme.TextSecondary;
        using var pen = new Pen(color, 1.3f);

        int cx = r.X + r.Width / 2;
        int cy = r.Y + r.Height / 2;

        switch (kind)
        {
            case 0:
                g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                break;
            case 1:
                if (WindowState == FormWindowState.Maximized)
                {
                    g.DrawRectangle(pen, cx - 5, cy - 4, 8, 8);
                    g.DrawRectangle(pen, cx - 3, cy - 6, 8, 8);
                }
                else
                {
                    g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
                }
                break;
            default:
                g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
                g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
                break;
        }
    }

    // ==================== 鼠标交互 ====================

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hover = -1;
        if (_btnClose.Contains(e.Location)) hover = 2;
        else if (_btnMax.Contains(e.Location)) hover = 1;
        else if (_btnMin.Contains(e.Location)) hover = 0;

        bool overAction = !_btnAction.IsEmpty && _btnAction.Contains(e.Location);
        if (overAction != _hoverAction)
        {
            _hoverAction = overAction;
            Invalidate(_btnAction.IsEmpty ? Rectangle.Empty :
                new Rectangle(_btnAction.X - 4, _btnAction.Y - 4, _btnAction.Width + 8, _btnAction.Height + 8));
        }

        if (hover != _hoverButton)
        {
            _hoverButton = hover;
            Cursor = hover >= 0 || overAction ? Cursors.Hand : Cursors.Default;
            Invalidate(new Rectangle(_btnMin.X - 4, _btnMin.Y - 4,
                                     _btnClose.Right - _btnMin.X + 8, _btnMin.Height + 8));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverButton != -1 || _hoverAction)
        {
            _hoverButton = -1;
            _hoverAction = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Y < TitleBarHeight &&
            _hoverButton < 0 && (_btnAction.IsEmpty || !_btnAction.Contains(e.Location)))
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_btnClose.Contains(e.Location)) { Close(); return; }
            if (_btnMin.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
            if (_btnMax.Contains(e.Location))
            {
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                return;
            }
            if (!_btnAction.IsEmpty && _btnAction.Contains(e.Location))
            {
                TitleActionClicked?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        base.OnMouseUp(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        if (PointToClient(MousePosition).Y < TitleBarHeight)
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
            return;
        }
        base.OnDoubleClick(e);
    }

    // ==================== 缩放 ====================

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result != HTCLIENT) return;

            var p = PointToClient(new Point(
                (short)(m.LParam.ToInt64() & 0xFFFF),
                (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));

            bool left = p.X <= ResizeBorder;
            bool right = p.X >= ClientSize.Width - ResizeBorder;
            bool top = p.Y <= ResizeBorder;
            bool bottom = p.Y >= ClientSize.Height - ResizeBorder;

            int hit = -1;
            if (left && top) hit = HTTOPLEFT;
            else if (right && top) hit = HTTOPRIGHT;
            else if (left && bottom) hit = HTBOTTOMLEFT;
            else if (right && bottom) hit = HTBOTTOMRIGHT;
            else if (left) hit = HTLEFT;
            else if (right) hit = HTRIGHT;
            else if (top) hit = HTTOP;
            else if (bottom) hit = HTBOTTOM;

            if (hit != -1) { m.Result = hit; return; }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _wallpaper?.Dispose();
        base.Dispose(disposing);
    }
}
