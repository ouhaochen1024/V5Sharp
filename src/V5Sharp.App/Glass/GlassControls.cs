using System.Drawing.Drawing2D;
using V5Sharp.Core;

namespace V5Sharp.App;

/// <summary>
/// 白卡片：纯色填充 + 1px 细描边，和 Clash 的卡片一致。
/// 不再有毛玻璃/高光/渐变，避免"非主流"的观感。
/// </summary>
public class GlassPanel : Control
{
    public float Radius { get; set; } = 12f;
    public Color Fill { get; set; } = Theme.CardFill;
    public Color Border { get; set; } = Theme.CardBorder;

    public GlassPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);   // 透明背景：先把父级（窗体底）画进来
        Theme.PaintCard(e.Graphics, new RectangleF(0, 0, Width, Height), Radius, Fill, Border);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // 底色与描边已在 PaintCard 里画完
    }
}

/// <summary>分隔线 / 强调条：可实色、可两端淡出、可圆角</summary>
public sealed class Hairline : Control
{
    public Color Tint { get; set; } = Theme.DividerLine;

    /// <summary>true = 两端淡出；false = 实色</summary>
    public bool Fade { get; set; } = true;

    /// <summary>true = 圆角（用于标题下的强调短条）</summary>
    public bool Rounded { get; set; }

    public Hairline()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Height = 1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (Fade)
        {
            Theme.DrawFadedLine(g, 0, 0, Width, Tint, Height);
            return;
        }

        if (Rounded)
        {
            using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), Height / 2f);
            using var b = new SolidBrush(Tint);
            g.FillPath(b, path);
            return;
        }

        using var bb = new SolidBrush(Tint);
        g.FillRectangle(bb, 0, 0, Width, Height);
    }
}

/// <summary>
/// 扁平按钮。
/// Primary：实心蓝底白字；Secondary：白底细边框；Ghost：纯文字；Danger：红色描边文字。
/// 全部为纯色填充 + 1px 描边，不做渐变、不做高光 —— 之前按钮正中间那条亮线
/// 就是 GDI+ 线性渐变在奇数列高上留下的接缝，扁平化后彻底消失。
/// </summary>
public sealed class GlassButton : Control
{
    /// <summary>
    /// Primary 实心强调按钮；Secondary 白底细边；Soft 浅蓝胶囊（标题栏「官方」同款）；
    /// Ghost 纯文字；Danger 危险动作。
    /// </summary>
    public enum Kind { Primary, Secondary, Soft, Ghost, Danger }

    /// <summary>传这个值就是胶囊形（圆角 = 高度的一半）</summary>
    public const float Pill = 999f;

    private readonly System.Windows.Forms.Timer _anim;
    private float _hover;      // 0..1
    private float _press;      // 0..1
    private bool _hovering;
    private bool _pressing;

    public Kind Variant { get; set; } = Kind.Secondary;
    public float Radius { get; set; } = Pill;

    public GlassButton()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = Theme.FontMedium;

        _anim = new System.Windows.Forms.Timer { Interval = 16 };
        _anim.Tick += (_, _) =>
        {
            float th = _hovering ? 1f : 0f;
            float tp = _pressing ? 1f : 0f;
            bool moved = false;

            // 指数收敛：每帧只走剩余距离的一部分，单调逼近目标值，不会在目标两侧来回抖
            float dh = th - _hover;
            if (Math.Abs(dh) > 0.004f) { _hover += dh * 0.32f; moved = true; }
            else if (_hover != th) { _hover = th; moved = true; }

            float dp = tp - _press;
            if (Math.Abs(dp) > 0.004f) { _press += dp * 0.38f; moved = true; }
            else if (_press != tp) { _press = tp; moved = true; }

            if (moved) Invalidate();
            else _anim.Stop();
        };
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; _anim.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; _pressing = false; _anim.Start(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressing = true; _anim.Start(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressing = false; _anim.Start(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        if (rect.Width < 4 || rect.Height < 4) return;

        float corner = Radius >= Pill ? rect.Height / 2f : Radius;
        const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        if (!Enabled)
        {
            using var dp = Theme.RoundRect(rect, corner);
            using var db = new SolidBrush(Color.FromArgb(243, 245, 248));
            g.FillPath(db, dp);
            using var dpen = new Pen(Color.FromArgb(233, 236, 241), 1f);
            g.DrawPath(dpen, dp);
            Theme.DrawText(g, Text, Font, Color.FromArgb(168, 176, 188), ClientRectangle, center);
            return;
        }

        switch (Variant)
        {
            case Kind.Primary:
                {
                    // 纯色：默认 Accent，悬停过渡到 AccentHover，按下再深一档
                    var fill = Theme.Blend(Theme.AccentHover, Theme.Accent, _hover);
                    fill = Theme.Blend(Theme.AccentPress, fill, _press);

                    using (var path = Theme.RoundRect(rect, corner))
                    using (var b = new SolidBrush(fill))
                        g.FillPath(b, path);

                    // 上沿一道极淡的白色内高光：让实心块有"厚度"，不再是平涂色块
                    if (corner > 2f)
                    {
                        using var inner = Theme.RoundRect(Theme.Deflate(rect, 1f), Math.Max(1f, corner - 1f));
                        using var hp = new Pen(Color.FromArgb(34 + (int)(30 * _hover), 255, 255, 255), 1f);
                        g.DrawPath(hp, inner);
                    }

                    Theme.DrawText(g, Text, Font, Theme.TextOnAccent, ClientRectangle, center);
                    break;
                }

            case Kind.Soft:
                {
                    // 标题栏「官方」胶囊的配色：浅蓝底 + 浅蓝细边 + 蓝字
                    var fill = Theme.Blend(Color.FromArgb(224, 236, 255), Theme.AccentSoft, _hover);
                    var line = Theme.Blend(Color.FromArgb(160, 194, 255), Theme.AccentLine, _hover);
                    var fg = _hover > 0.5f ? Theme.AccentHover : Theme.Accent;

                    using (var path = Theme.RoundRect(rect, corner))
                    {
                        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                        using (var pen = new Pen(line, 1f)) g.DrawPath(pen, path);
                    }

                    Theme.DrawText(g, Text, Font, fg, ClientRectangle, center);
                    break;
                }

            case Kind.Ghost:
                {
                    if (_hover > 0.02f)
                    {
                        using var path = Theme.RoundRect(rect, corner);
                        using var b = new SolidBrush(Color.FromArgb((int)(255 * _hover), 238, 241, 246));
                        g.FillPath(b, path);
                    }
                    Theme.DrawText(g, Text, Font,
                        Theme.Blend(Theme.Accent, Theme.TextSecondary, _hover), ClientRectangle, center);
                    break;
                }

            case Kind.Danger:
                {
                    using var path = Theme.RoundRect(rect, corner);
                    using (var b = new SolidBrush(Theme.Blend(Color.FromArgb(255, 236, 236), Color.White, _hover)))
                        g.FillPath(b, path);
                    using var pen = new Pen(Theme.Blend(Color.FromArgb(240, 180, 180), Theme.FieldBorder, 1 - _hover), 1f);
                    g.DrawPath(pen, path);
                    Theme.DrawText(g, Text, Font,
                        Theme.Blend(Theme.Danger, Color.FromArgb(120, 40, 40), 1 - _hover), ClientRectangle, center);
                    break;
                }

            default:   // Secondary
                {
                    var fill = Theme.Blend(Theme.AccentSoft, Color.White, _hover);
                    var edge = Theme.Blend(Theme.AccentLine, Theme.FieldBorder, _hover);
                    var fg = Theme.Blend(Theme.Accent, Color.FromArgb(52, 62, 78), _hover);

                    using (var path = Theme.RoundRect(rect, corner))
                    {
                        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                        using (var pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
                    }

                    Theme.DrawText(g, Text, Font, fg, ClientRectangle, center);
                    break;
                }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>输入框：白底 + 细边框，聚焦时蓝色描边 + 淡蓝色外环</summary>
public sealed class GlassInput : Control
{
    private readonly TextBox _box = new();
    private bool _focused;

    public event EventHandler? ValueChanged;

    public string Placeholder
    {
        get => _box.PlaceholderText;
        set => _box.PlaceholderText = value;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public TextBox Inner => _box;

    public GlassInput()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Height = 34;

        _box.BorderStyle = BorderStyle.None;
        _box.BackColor = Theme.FieldFill;
        _box.ForeColor = Theme.TextPrimary;
        _box.Font = Theme.FontUI;
        _box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _box.TextChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_box);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutBox();
    }

    private void LayoutBox()
    {
        const int padX = 11;
        int h = _box.PreferredHeight;
        _box.SetBounds(padX, Math.Max(2, (Height - h) / 2), Math.Max(10, Width - padX * 2), h);
    }

    protected override void OnClick(EventArgs e)
    {
        _box.Focus();
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        if (r.Width < 4 || r.Height < 4) return;

        const float radius = 8f;

        using (var path = Theme.RoundRect(r, radius))
        using (var brush = new SolidBrush(Theme.FieldFill))
            g.FillPath(brush, path);

        using (var path = Theme.RoundRect(r, radius))
        using (var pen = new Pen(_focused ? Theme.Accent : Theme.FieldBorder, 1f))
            g.DrawPath(pen, path);
    }
}

/// <summary>iOS 风格开关：扁平胶囊，纯色轨道 + 白色滑块</summary>
public sealed class GlassToggle : Control
{
    private readonly System.Windows.Forms.Timer _anim;
    private float _pos;
    private bool _checked;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (!IsHandleCreated) { _pos = value ? 1f : 0f; Invalidate(); }
            else _anim.Start();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public GlassToggle()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Size = new Size(42, 24);
        Cursor = Cursors.Hand;

        _anim = new System.Windows.Forms.Timer { Interval = 16 };
        _anim.Tick += (_, _) =>
        {
            // 指数收敛：每帧只走剩余距离的一部分，单调逼近目标值
            float target = _checked ? 1f : 0f;
            float delta = target - _pos;

            if (Math.Abs(delta) <= 0.006f)
            {
                _pos = target;
                _anim.Stop();
                Invalidate();
                return;
            }

            _pos += delta * 0.30f;
            Invalidate();
        };
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float inset = 1.5f;
        var track = new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2);
        if (track.Width < 8 || track.Height < 8) return;

        float radius = track.Height / 2f;
        var off = Color.FromArgb(214, 220, 230);
        var trackColor = Theme.Blend(Theme.Accent, off, _pos);

        using (var path = Theme.RoundRect(track, radius))
        using (var b = new SolidBrush(trackColor))
            g.FillPath(b, path);

        // 滑块
        float knobH = track.Height - 4;
        float knobW = knobH;
        float travel = Math.Max(0, track.Width - knobW - 4);
        float knobX = track.X + 2 + travel * _pos;
        var knob = new RectangleF(knobX, track.Y + 2, knobW, knobH);

        using (var kp = Theme.RoundRect(knob, knobH / 2f))
        using (var kb = new SolidBrush(Color.White))
            g.FillPath(kb, kp);

        using (var kp = Theme.RoundRect(knob, knobH / 2f))
        using (var pen = new Pen(Color.FromArgb(28, 20, 32, 48), 1f))
            g.DrawPath(pen, kp);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>实例列表：自绘行、状态圆点、悬停高亮、滚轮滚动</summary>
public sealed class InstanceList : Control
{
    private const int HeaderH = 32;
    private const int RowH = 58;

    private IReadOnlyList<Instance> _items = Array.Empty<Instance>();
    private int _scroll;
    private int _hover = -1;
    private int _selected = -1;

    public event EventHandler<Instance>? RowActivated;

    public InstanceList()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Font = Theme.FontUI;
        Cursor = Cursors.Default;
    }

    public void SetItems(IReadOnlyList<Instance> items)
    {
        _items = items;
        int visible = Math.Max(1, (Height - HeaderH) / RowH);
        int maxScroll = Math.Max(0, _items.Count - visible);
        if (_scroll > maxScroll) _scroll = maxScroll;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = RowAt(e.Y);
        if (idx != _hover) { _hover = idx; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int idx = RowAt(e.Y);
        _selected = idx;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        int idx = RowAt(e.Y);
        if (idx >= 0 && idx < _items.Count) RowActivated?.Invoke(this, _items[idx]);
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int visible = Math.Max(1, (Height - HeaderH) / RowH);
        int maxScroll = Math.Max(0, _items.Count - visible);
        int next = Math.Clamp(_scroll - Math.Sign(e.Delta), 0, maxScroll);
        if (next != _scroll) { _scroll = next; Invalidate(); }
        base.OnMouseWheel(e);
    }

    private int RowAt(int y)
    {
        if (y < HeaderH) return -1;
        int idx = _scroll + (y - HeaderH) / RowH;
        return idx >= 0 && idx < _items.Count ? idx : -1;
    }

    /// <summary>列位置：基准宽度下固定，窄卡片时按比例压缩，保证最后一列始终有空间</summary>
    private readonly struct Cols
    {
        public readonly int Pid, Group, Time, State, File;

        public Cols(int w)
        {
            float k = Math.Clamp((w - 60f) / 430f, 0.45f, 1f);
            int X(int v) => 36 + (int)Math.Round((v - 36) * k);
            Pid = X(36); Group = X(118); Time = X(206); State = X(302); File = X(430);
        }

        public int FileWidth(int w) => Math.Max(40, w - File - 12);
        public int GroupWidth => Math.Max(30, Group - Pid - 8);
        public int TimeWidth => Math.Max(30, State - Time - 8);
        public int StateWidth => Math.Max(40, File - State - 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int w = Width;
        var c = new Cols(w);

        // 表头
        var headColor = Theme.TextMuted;
        Theme.DrawText(g, "PID", Theme.FontSmall, headColor, new Rectangle(c.Pid, 6, 74, HeaderH - 6));
        Theme.DrawText(g, "实例组", Theme.FontSmall, headColor, new Rectangle(c.Group, 6, c.GroupWidth, HeaderH - 6));
        Theme.DrawText(g, "启动时间", Theme.FontSmall, headColor, new Rectangle(c.Time, 6, c.TimeWidth, HeaderH - 6));
        Theme.DrawText(g, "状态", Theme.FontSmall, headColor, new Rectangle(c.State, 6, c.StateWidth, HeaderH - 6));
        Theme.DrawText(g, "可执行文件", Theme.FontSmall, headColor, new Rectangle(c.File, 6, c.FileWidth(w), HeaderH - 6));

        using (var pen = new Pen(Theme.DividerLine, 1f))
            g.DrawLine(pen, 4, HeaderH - 1, w - 8, HeaderH - 1);

        if (_items.Count == 0)
        {
            Theme.DrawText(g, "还没有实例 · 点「启动全部」开始", Theme.FontUI, Theme.TextMuted,
                new Rectangle(0, HeaderH, w, Height - HeaderH),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int visible = Math.Max(1, (Height - HeaderH) / RowH);
        for (int i = 0; i < visible; i++)
        {
            int dataIdx = _scroll + i;
            if (dataIdx >= _items.Count) break;

            var inst = _items[dataIdx];
            var row = new Rectangle(4, HeaderH + i * RowH, w - 8, RowH - 4);
            DrawRow(g, row, inst, dataIdx, c);
        }

        DrawScrollBar(g, visible);
    }

    private void DrawRow(Graphics g, Rectangle row, Instance inst, int index, Cols c)
    {
        bool hover = index == _hover;
        bool selected = index == _selected;

        if (hover || selected)
        {
            using var path = Theme.RoundRect(row, 8);
            using var b = new SolidBrush(selected ? Theme.AccentSoft : Theme.ControlHover);
            g.FillPath(b, path);
        }

        int cy = row.Y + row.Height / 2;

        // 状态圆点
        var dotColor = inst.IsChasing ? Theme.Warn
                     : inst.IsRunning ? Theme.Success
                     : Color.FromArgb(190, 196, 206);
        using (var b = new SolidBrush(Color.FromArgb(inst.IsRunning ? 46 : 22, dotColor)))
            g.FillEllipse(b, 14, cy - 7, 14, 14);
        using (var b = new SolidBrush(dotColor))
            g.FillEllipse(b, 18, cy - 3, 6, 6);

        Theme.DrawText(g, inst.Pid.ToString(), Theme.FontMono, Theme.TextPrimary,
                       new Rectangle(c.Pid, row.Y, 74, row.Height));

        // 组号胶囊
        var pill = new Rectangle(c.Group, cy - 11, 66, 22);
        using (var pp = Theme.RoundRect(pill, 6))
        using (var pb = new SolidBrush(Theme.AccentSoft))
            g.FillPath(pb, pp);
        Theme.DrawText(g, inst.GroupId.ToString(), Theme.FontSmall, Theme.Accent,
                       pill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        Theme.DrawText(g, inst.StartTime.ToString("HH:mm:ss"), Theme.FontUI, Theme.TextSecondary,
                       new Rectangle(c.Time, row.Y, c.TimeWidth, row.Height));

        if (inst.IsChasing)
        {
            Theme.DrawText(g, "接替中", Theme.FontUI, Theme.Warn,
                           new Rectangle(c.State, row.Y, c.StateWidth, row.Height));
        }
        else if (inst.IsRunning)
        {
            Theme.DrawText(g, "运行中", Theme.FontUI, Theme.Success,
                           new Rectangle(c.State, row.Y, c.StateWidth, row.Height));
        }
        else
        {
            string code = inst.ExitCode is uint ec && ec != 0 ? $"0x{ec:X8}" : "正常";
            Theme.DrawText(g, "已退出", Theme.FontUI, Theme.TextMuted,
                           new Rectangle(c.State, row.Y, Math.Min(56, c.StateWidth), row.Height));
            Theme.DrawText(g, code, Theme.FontSmall, Theme.TextMuted,
                           new Rectangle(c.State + Math.Min(56, c.StateWidth), row.Y,
                                         Math.Max(30, c.StateWidth - Math.Min(56, c.StateWidth)), row.Height));
        }

        string name = string.IsNullOrEmpty(inst.ExeName) ? inst.ExePath : inst.ExeName;
        int fw = c.FileWidth(row.Right);
        var nameRect = new Rectangle(c.File, row.Y + 10, fw, 20);
        Theme.DrawTextEllipsis(g, name, Theme.FontBold,
            inst.IsRunning ? Theme.TextPrimary : Theme.TextSecondary, nameRect);

        var pathRect = new Rectangle(c.File, row.Y + 31, fw, 18);
        Theme.DrawTextEllipsis(g, inst.ExePath, Theme.FontSmall, Theme.TextMuted, pathRect);
    }

    private void DrawScrollBar(Graphics g, int visible)
    {
        if (_items.Count <= visible) return;

        int trackTop = HeaderH + 4;
        int trackH = Height - HeaderH - 8;
        float ratio = visible / (float)_items.Count;
        int thumbH = Math.Max(28, (int)(trackH * ratio));
        int maxScroll = Math.Max(1, _items.Count - visible);
        int thumbY = trackTop + (int)((trackH - thumbH) * (_scroll / (float)maxScroll));

        using var path = Theme.RoundRect(new RectangleF(Width - 8, thumbY, 4, thumbH), 2);
        using var b = new SolidBrush(Color.FromArgb(64, 152, 161, 173));
        g.FillPath(b, path);
    }
}

/// <summary>
/// 小贴士：浅色圆角卡片 + 标题圆点 + 逐条要点。
/// 高度完全由内容测量得出（MeasureHeight），调用方按测量结果给控件定高，
/// 因此任何宽度 / DPI 下文字都不会被截断。
/// </summary>
public sealed class TipsBlock : Control
{
    private readonly List<string> _items = new();

    public string Title { get; set; } = "小贴士";
    public Color Accent { get; set; } = Theme.Accent;
    public Font TitleFont { get; set; } = Theme.FontBold;
    public Font ItemFont { get; set; } = Theme.FontSmall;
    public int PaddingX { get; set; } = 12;
    public int PaddingY { get; set; } = 9;
    public int BulletIndent { get; set; } = 13;
    public int ItemGap { get; set; } = 5;

    public IReadOnlyList<string> Items => _items;

    public TipsBlock()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Font = Theme.FontSmall;
    }

    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Invalidate();
    }

    /// <summary>按给定宽度计算完整显示所需的高度</summary>
    public int MeasureHeight(int width)
    {
        int inner = Math.Max(24, width - PaddingX * 2);
        int h = PaddingY * 2;

        h += Theme.MeasureHeight(Title, TitleFont) + 4;

        int textW = Math.Max(16, inner - BulletIndent);
        foreach (var it in _items)
            h += ItemGap + Theme.MeasureWrappedHeight(it, ItemFont, textW);

        return h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintCard(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10, Theme.TintFill, Theme.TintBorder);

        int x = PaddingX;
        int y = PaddingY;
        int inner = Width - PaddingX * 2;

        // 标题 + 强调圆点
        int titleH = Theme.MeasureHeight(Title, TitleFont);
        using (var b = new SolidBrush(Accent))
            g.FillEllipse(b, x, y + titleH / 2 - 3, 6, 6);
        TextRenderer.DrawText(g, Title, TitleFont, new Point(x + BulletIndent - 3, y),
                              Theme.TextSecondary, TextFormatFlags.NoPrefix);
        y += titleH + 4;

        // 逐条要点
        int textW = Math.Max(16, inner - BulletIndent);
        foreach (var it in _items)
        {
            y += ItemGap;
            int ih = Theme.MeasureWrappedHeight(it, ItemFont, textW);
            using (var b = new SolidBrush(Color.FromArgb(150, Accent)))
                g.FillEllipse(b, x + 2, y + ih / 2 - 1, 4, 4);
            TextRenderer.DrawText(g, it, ItemFont, new Rectangle(x + BulletIndent, y, textW, ih),
                                  Color.FromArgb(68, 78, 92), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            y += ih;
        }
    }
}
