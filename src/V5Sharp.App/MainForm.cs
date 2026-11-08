using System.Diagnostics;
using V5Sharp.Core;

namespace V5Sharp.App;

public sealed class MainForm : GlassForm
{
    // 日志区是卡片里的一块"内嵌"面板：底色比卡片灰一档，文字用近黑的中性灰
    private static readonly Color LogBack = Color.FromArgb(246, 246, 248);
    private static readonly Color LogText = Color.FromArgb(58, 58, 60);

    /// <summary>日志最多保留的行数：TextBox 越攒越慢，超过就按队列裁掉最旧的</summary>
    private const int MaxLogLines = 400;

    /// <summary>收起右侧面板时的左卡片宽度</summary>
    private const int CollapsedCardW = 430;

    /// <summary>收起态的窗口宽度：左卡片 + 两侧留白</summary>
    private int CollapsedClientW => S(CollapsedCardW) + S(18) * 2;

    private readonly InstanceManager _manager = new();
    private readonly AppConfig _config;
    private readonly string _configPath = AppConfig.DefaultPath;

    private readonly GlassPanel _cardForm = new();
    private readonly GlassPanel _cardList = new();
    private readonly GlassPanel _cardLog = new();
    private readonly GlassPanel _logShell = new();

    private readonly Label _lblFormTitle = new();
    private readonly Label _lblExe = new();
    private readonly Label _lblWork = new();
    private readonly Label _lblArgs = new();
    private readonly Label _lblHide = new();
    private readonly Label _lblCount = new();
    private readonly Label _lblLogSwitch = new();
    private readonly Label _lblListTitle = new();
    private readonly Label _lblStat = new();
    private readonly Label _lblCountValue = new();
    private readonly Label _lblStrong = new();
    private readonly Label _lblGlobalHide = new();
    private readonly Label _lblReg = new();
    private readonly Label _lblLogTitle = new();
    private readonly Hairline _divider = new();
    private readonly Hairline _barForm = new();
    private readonly Hairline _barList = new();
    private readonly Hairline _barLog = new();
    private readonly TipsBlock _tips = new();

    private readonly GlassInput _inExe = new();
    private readonly GlassInput _inWork = new();
    private readonly GlassInput _inArgs = new();
    private readonly GlassInput _inHide = new();
    private readonly GlassInput _inRegFilter = new();

    private readonly GlassButton _btnBrowseExe = new();
    private readonly GlassButton _btnBrowseWork = new();
    private readonly GlassButton _btnLaunch = new();
    private readonly GlassButton _btnAdd = new();
    private readonly GlassButton _btnKill = new();
    private readonly GlassButton _btnMinus = new();
    private readonly GlassButton _btnPlus = new();
    private readonly GlassButton _btnClearLog = new();
    private readonly GlassButton _btnAdvanced = new();
    private readonly GlassButton _btnPanels = new();

    private readonly GlassToggle _tgLog = new();
    private readonly GlassToggle _tgStrong = new();
    private readonly GlassToggle _tgGlobalHide = new();
    private readonly InstanceList _list = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    /// <summary>日志正文的行缓存，用于裁剪最旧的内容</summary>
    private readonly Queue<string> _logLines = new();
    /// <summary>自上次整体重排后追加的行数</summary>
    private int _logAppended;

    private int _count = 2;
    private bool _busy;
    /// <summary>高级选项是否展开（注册表关键词等，普通用户不需要）</summary>
    private bool _advancedOpen;
    /// <summary>右侧「运行实例 / 运行日志」面板是否展开；默认收起，只留左侧配置区</summary>
    private bool _panelsOpen;
    /// <summary>展开态使用的窗口宽度：收起前记下来，展开时恢复</summary>
    private int _expandedWidth = 1140;
    /// <summary>展开态使用的窗口高度：收起时会按内容缩到最小，展开要能还原</summary>
    private int _expandedHeight;   // 0 = 还没记录过，用内容高度
    /// <summary>正在按内容抬高窗口，避免布局回调重入</summary>
    private bool _growing;
    /// <summary>正在按内容收紧窗口高度，避免布局回调重入</summary>
    private bool _fittingHeight;

    /// <summary>左卡片内容真正需要的高度（超出时自动抬高窗口下限）</summary>
    private int _requiredCardH;
    /// <summary>待生效的窗口最小高度，避免在布局过程中反复触发缩放</summary>
    private int _pendingMinH = -1;

    public MainForm()
    {
        _config = AppConfig.Load(_configPath);
        _panelsOpen = _config.PanelsOpen;

        TitleText = "V5多开器Pro";
        TitleActionText = "官方";
        TitleActionClicked += (_, _) => OpenWebsite();
        Icon = LoadAppIcon();
        TopMost = _config.TopMost;   // 直接设，别走 Pinned 的 setter（那时 UI 还没建好）
        PinChanged += (_, _) => SaveConfig();

        ClientSize = new Size(_panelsOpen ? 1140 : CollapsedClientW, 840);
        MinimumSize = _panelsOpen ? new Size(1040, 760) : new Size(S(400), 760);

        BuildUi();
        ApplyConfig();
        LayoutAll();
        ApplyPanelState();   // 首帧按内容定尺寸，别留默认 840 那一截空白

        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            string first = files[0];
            if (!string.Equals(Path.GetExtension(first), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"只支持拖入 .exe 文件：{Path.GetFileName(first)}");
                return;
            }
            SetExePath(first);
        };

        _timer.Interval = 1000;
        _timer.Tick += (_, _) => RefreshList();
        _timer.Start();

        // 接替事件（启动器退出、客户端接管）直接写进运行日志
        _manager.Notify += AppendLog;

        FormClosing += (_, _) =>
        {
            _timer.Stop();
            SaveConfig();
            _manager.Dispose();
        };
    }

    // ============================== 构建界面 ==============================

    private void BuildUi()
    {
        _cardForm.Radius = 14;
        _cardList.Radius = 14;
        _cardLog.Radius = 14;

        StyleLabel(_lblFormTitle, "启动配置", Theme.FontHero, Theme.TextPrimary);

        StyleLabel(_lblExe, "程序路径", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblWork, "工作目录", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblArgs, "启动参数", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblHide, "受保护进程", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblCount, "实例数", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblLogSwitch, "记录改名日志", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblStrong, "加强模式", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblGlobalHide, "全局隐藏", Theme.FontSmall, Theme.TextSecondary);
        StyleLabel(_lblReg, "注册表隔离关键词", Theme.FontSmall, Theme.TextSecondary);

        // 小贴士：自绘控件，高度按内容测量，任何宽度下都不会截断
        _tips.Title = "小贴士";
        _tips.SetItems(new[]
        {
            "选好程序点「启动全部」即可多开，配置会自动推断",
            "程序由启动器拉起新进程时会自动跟随，无需刷新",
            "全局隐藏：让对方看不到其他实例",
            "加强模式：每个实例独立文件锁与临时目录"
        });

        _inExe.Placeholder = "拖入或选择要运行的程序（.exe）";
        _inWork.Placeholder = "留空 = 程序所在目录";
        _inArgs.Placeholder = "可选，例如 -windowed";
        _inHide.Placeholder = "填了会自动隐藏这些同名进程，多个用 ; 分隔";
        _inRegFilter.Placeholder = "留空即可，一般无需填写";

        _inExe.Inner.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LaunchAll(); }
        };

        _btnBrowseExe.Text = "浏览";
        _btnBrowseExe.Variant = GlassButton.Kind.Soft;
        _btnBrowseExe.Font = Theme.FontSmallMedium;
        _btnBrowseExe.Click += (_, _) => BrowseExe();

        _btnBrowseWork.Text = "选择";
        _btnBrowseWork.Variant = GlassButton.Kind.Soft;
        _btnBrowseWork.Font = Theme.FontSmallMedium;
        _btnBrowseWork.Click += (_, _) => BrowseWorkDir();

        _btnAdvanced.Text = "高级";
        _btnAdvanced.Variant = GlassButton.Kind.Ghost;
        _btnAdvanced.Font = Theme.FontSmall;
        _btnAdvanced.Click += (_, _) => ToggleAdvanced();

        // 右侧面板的展开入口：收起点在它身上，展开后变成"收起"
        _btnPanels.Variant = GlassButton.Kind.Soft;
        _btnPanels.Font = Theme.FontSmallMedium;
        _btnPanels.Click += (_, _) => TogglePanels();

        _btnMinus.Text = "−";
        _btnMinus.Variant = GlassButton.Kind.Secondary;
        _btnMinus.Radius = 10f;      // 步进器不用胶囊：macOS 的 stepper 是小圆角方块
        _btnMinus.Font = new Font(Theme.FontMedium.FontFamily, 12f, FontStyle.Regular);
        _btnMinus.Click += (_, _) => { _count = Math.Max(1, _count - 1); _lblCountValue.Text = _count.ToString(); };

        _btnPlus.Text = "+";
        _btnPlus.Variant = GlassButton.Kind.Secondary;
        _btnPlus.Radius = 10f;
        _btnPlus.Font = new Font(Theme.FontMedium.FontFamily, 12f, FontStyle.Regular);
        _btnPlus.Click += (_, _) => { _count = Math.Min(32, _count + 1); _lblCountValue.Text = _count.ToString(); };

        StyleLabel(_lblCountValue, Theme.FontHero, Theme.TextPrimary);
        _lblCountValue.AutoSize = false;
        _lblCountValue.TextAlign = ContentAlignment.MiddleCenter;

        _tgLog.Checked = true;

        _btnLaunch.Text = "启动全部";
        _btnLaunch.Variant = GlassButton.Kind.Primary;
        _btnLaunch.Click += (_, _) => LaunchAll();

        _btnAdd.Text = "追加一个";
        _btnAdd.Variant = GlassButton.Kind.Secondary;
        _btnAdd.Click += (_, _) => AddOne();

        _btnKill.Text = "结束全部";
        _btnKill.Variant = GlassButton.Kind.Secondary;
        _btnKill.Click += (_, _) => KillAll();

        // 高级选项默认收起：普通用户不需要关心注册表关键字
        _lblReg.Visible = false;
        _inRegFilter.Visible = false;

        // 标题下的强调短条：Apple 的版面不用彩色短线做标题装饰，层级靠字号与留白，
        // 这里直接停用（后面布局也不再给它留位置）
        foreach (var bar in new[] { _barForm, _barList, _barLog })
            bar.Visible = false;
        _divider.Tint = Theme.DividerLine;
        _divider.Fade = false;
        _divider.Visible = false;

        _cardForm.Controls.AddRange(new Control[]
        {
            _lblFormTitle, _btnAdvanced, _barForm,
            _lblExe, _inExe, _btnBrowseExe,
            _lblWork, _inWork, _btnBrowseWork,
            _lblArgs, _inArgs,
            _lblHide, _inHide,
            _lblCount, _btnMinus, _lblCountValue, _btnPlus, _lblLogSwitch, _tgLog,
            _lblStrong, _tgStrong, _lblGlobalHide, _tgGlobalHide,
            _lblReg, _inRegFilter,
            _btnLaunch, _btnAdd, _btnKill, _btnPanels, _divider, _tips
        });

        // ---- 实例列表 ----
        StyleLabel(_lblListTitle, "运行实例", Theme.FontHero, Theme.TextPrimary);
        StyleLabel(_lblStat, "", Theme.FontSmall, Theme.TextMuted);
        _lblStat.AutoSize = false;
        _lblStat.TextAlign = ContentAlignment.MiddleRight;

        _list.RowActivated += (_, inst) => OpenHookLog(inst);

        _cardList.Controls.AddRange(new Control[] { _lblListTitle, _lblStat, _barList, _list });

        // ---- 日志 ----
        StyleLabel(_lblLogTitle, "运行日志", Theme.FontHero, Theme.TextPrimary);
        _btnClearLog.Text = "清空";
        _btnClearLog.Variant = GlassButton.Kind.Ghost;
        _btnClearLog.Font = Theme.FontSmall;
        _btnClearLog.Click += (_, _) =>
        {
            _log.Clear();
            _logLines.Clear();
            _logAppended = 0;
        };

        _logShell.Radius = 10;
        _logShell.Fill = LogBack;
        _logShell.Border = Theme.CardBorder;

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.WordWrap = true;
        _log.BorderStyle = BorderStyle.None;
        _log.ScrollBars = ScrollBars.None;   // 自绘面板里用系统滚动条很跳，改为自动滚到底 + 滚轮可翻
        _log.BackColor = LogBack;
        _log.ForeColor = LogText;
        _log.Font = Theme.FontLog;
        _logShell.Controls.Add(_log);

        _cardLog.Controls.AddRange(new Control[] { _lblLogTitle, _btnClearLog, _barLog, _logShell });

        Controls.AddRange(new Control[] { _cardForm, _cardList, _cardLog });
    }

    private static void StyleLabel(Label label, string text, Font font, Color color)
    {
        label.Text = text;
        label.Font = font;
        label.ForeColor = color;
        label.BackColor = Color.Transparent;
        // 关闭 AutoSize：位置与尺寸全部由布局代码按实测文本尺寸给出，避免系统二次调整
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
    }

    private static void StyleLabel(Label label, Font font, Color color)
    {
        label.Font = font;
        label.ForeColor = color;
        label.BackColor = Color.Transparent;
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
    }

    // ============================== 布局 ==============================

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_cardForm is null) return;
        LayoutAll();
    }

    private void LayoutAll()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        int pad = S(18);
        int top = TitleBarHeight + S(12);
        int bottom = ClientSize.Height - pad;
        int gap = S(16);
        int rightH = bottom - top;

        int leftW;
        if (_panelsOpen)
        {
            // 左卡片宽度随窗口伸缩，并夹在合理区间内；右侧始终保留足够操作空间
            int availW = ClientSize.Width - pad * 2 - gap;
            leftW = (int)Math.Round(availW * 0.37);
            leftW = Math.Clamp(leftW, S(340), S(430));
            leftW = Math.Min(leftW, Math.Max(S(300), availW - S(340)));
            leftW = Math.Max(leftW, S(300));

            int rightX = pad + leftW + gap;
            int rightW = Math.Max(S(320), ClientSize.Width - rightX - pad);

            // 右侧：实例列表 + 运行日志，日志高度按比例分配并夹取区间
            int logH = (int)Math.Round(rightH * 0.34);
            logH = Math.Clamp(logH, S(170), S(300));
            int listH = rightH - logH - gap;
            if (listH < S(190)) { listH = S(190); logH = Math.Max(S(140), rightH - listH - gap); }

            _cardList.SetBounds(rightX, top, rightW, listH);
            _cardLog.SetBounds(rightX, top + listH + gap, rightW, rightH - listH - gap);

            LayoutListCard();
            LayoutLogCard();
        }
        else
        {
            // 收起态：右侧面板隐藏，左卡片吃满整个可用宽度（窗口此时本来就很窄）
            leftW = Math.Max(S(300), ClientSize.Width - pad * 2);
        }

        _cardForm.SetBounds(pad, top, leftW, rightH);
        LayoutFormCard();

        int needClientH = TitleBarHeight + S(12) + _requiredCardH + pad + 2;

        // 内容放不下时抬高窗口下限，保证小贴士永不被裁切
        if (_requiredCardH > rightH)
        {
            if (needClientH > MinimumSize.Height) RequestMinHeight(needClientH);
            // 展开高级选项后内容可能超出当前窗口，按需把窗口撑高
            GrowToFit(needClientH);
        }
        else if (!_panelsOpen)
        {
            // 收起态：窗口高度跟着内容走，否则按钮和小贴士之间会空出一大片
            FitCollapsedHeight(needClientH);
        }
    }

    /// <summary>收起态按内容收紧窗口高度（延后执行，避免布局里改尺寸重入）</summary>
    private void FitCollapsedHeight(int clientH)
    {
        if (_fittingHeight || !IsHandleCreated) return;
        if (WindowState != FormWindowState.Normal) return;
        if (Math.Abs(ClientSize.Height - clientH) <= S(2)) return;

        _fittingHeight = true;
        BeginInvoke(new Action(() =>
        {
            try
            {
                if (_panelsOpen || WindowState != FormWindowState.Normal) return;
                if (Math.Abs(ClientSize.Height - clientH) > S(2))
                    ClientSize = new Size(ClientSize.Width, clientH);
            }
            finally
            {
                _fittingHeight = false;
            }
        }));
    }

    /// <summary>延后抬高窗口下限，避免在布局回调里直接改尺寸造成递归</summary>
    private void RequestMinHeight(int clientH)
    {
        if (_pendingMinH == clientH) return;
        _pendingMinH = clientH;
        if (!IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            if (MinimumSize.Height < clientH)
                MinimumSize = new Size(MinimumSize.Width, clientH);
        }));
    }

    /// <summary>窗口高度不够时延后撑高（避免在布局里直接改尺寸造成重入）</summary>
    private void GrowToFit(int clientH)
    {
        if (_growing || !IsHandleCreated) return;
        if (WindowState != FormWindowState.Normal) return;
        if (ClientSize.Height >= clientH) return;

        var screen = Screen.FromControl(this);
        if (screen is not null && clientH > screen.WorkingArea.Height) return;   // 屏幕都放不下就别撑了

        _growing = true;
        BeginInvoke(new Action(() =>
        {
            try
            {
                if (WindowState == FormWindowState.Normal && ClientSize.Height < clientH)
                    ClientSize = new Size(ClientSize.Width, clientH);
            }
            finally
            {
                _growing = false;
            }
        }));
    }

    /// <summary>展开 / 收起高级选项（注册表隔离关键词）</summary>
    private void ToggleAdvanced()
    {
        _advancedOpen = !_advancedOpen;

        _lblReg.Visible = _advancedOpen;
        _inRegFilter.Visible = _advancedOpen;
        _btnAdvanced.Text = _advancedOpen ? "收起" : "高级";

        LayoutAll();
        Refresh();
    }

    /// <summary>展开 / 收起右侧的「运行实例 + 运行日志」</summary>
    private void TogglePanels()
    {
        // 收起前把展开态的尺寸记下来（此时窗口还是宽的那个）
        if (_panelsOpen)
        {
            if (ClientSize.Width >= S(900)) _expandedWidth = ClientSize.Width;
            if (ClientSize.Height >= S(600)) _expandedHeight = ClientSize.Height;
        }

        _panelsOpen = !_panelsOpen;
        ApplyPanelState();
    }

    /// <summary>
    /// 同步面板可见性、窗口尺寸。
    /// 收起时窗口一起收窄，否则右侧会留出一大片空白；展开时恢复收起前的尺寸。
    /// </summary>
    private void ApplyPanelState()
    {
        _cardList.Visible = _panelsOpen;
        _cardLog.Visible = _panelsOpen;
        SyncPanelChrome();

        MinimumSize = new Size(_panelsOpen ? S(1040) : S(400), MinimumSize.Height);

        // 高度：展开态优先用收起前的尺寸（用户可能拉高过），没有就按内容高度，
        // 否则窗口比内容高，小贴士贴底后按钮和贴士之间会空出一大块。
        int contentH = TitleBarHeight + S(12) + _requiredCardH + S(18) + 2;
        int targetW = _panelsOpen
            ? Math.Max(_expandedWidth, Math.Max(ClientSize.Width, S(1040)))
            : CollapsedClientW;
        int targetH = _panelsOpen && _expandedHeight > 0 ? _expandedHeight : contentH;

        // 内容算得比下限还矮时把下限降下来，否则窗口会被下限顶高、下面留白
        if (MinimumSize.Height > targetH) MinimumSize = new Size(MinimumSize.Width, targetH);
        if (ClientSize.Width != targetW || ClientSize.Height != targetH)
            ClientSize = new Size(targetW, targetH);

        LayoutAll();
        if (_panelsOpen) RefreshList();   // 收起期间暂停刷新，展开时立刻补一次
        Invalidate();
    }

    /// <summary>展开 / 收起按钮的文案</summary>
    private void SyncPanelChrome()
    {
        _btnPanels.Text = _panelsOpen ? "收起运行面板" : "展开运行面板";
    }

    /// <summary>
    /// 窗口图标。任务栏和 Alt-Tab 读的是窗口图标（WM_SETICON），不会自动沿用 exe 里那张，
    /// 所以必须自己设一次。这里把内嵌的 app.ico 整个交出去，系统会按当前 DPI 挑合适尺寸。
    /// </summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            using Stream? s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
            if (s is null) return null;
            return new Icon(s);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开官网</summary>
    private void OpenWebsite()
    {
        const string url = "https://www.yysdjbd.cn";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppendLog($"已打开官网：{url}");
        }
        catch
        {
            try { Clipboard.SetText(url); } catch { /* 忽略 */ }
            AppendLog($"无法打开浏览器，官网地址已复制：{url}");
        }
    }

    // ---------------- 左卡片（启动配置）----------------

    private void LayoutFormCard()
    {
        int p = S(22);
        int w = _cardForm.Width - p * 2;
        if (w < S(180)) return;

        int y = S(18);

        // 标题 + 右侧「高级」折叠入口
        int titleH = SizeOf(_lblFormTitle).Height;
        int advW = FitButtonWidth(_btnAdvanced);
        int advH = S(24);
        _btnAdvanced.SetBounds(p + w - advW, y + (titleH - advH) / 2, advW, advH);
        _lblFormTitle.SetBounds(p, y, Math.Max(S(60), w - advW - S(10)), titleH);
        y += titleH + S(14);

        // 输入行：标签在上、输入框在下、右侧可选操作按钮
        y = LayoutFieldRow(_lblExe, _inExe, _btnBrowseExe, p, y, w);
        y = LayoutFieldRow(_lblWork, _inWork, _btnBrowseWork, p, y, w);
        y = LayoutFieldRow(_lblArgs, _inArgs, null, p, y, w);
        y = LayoutFieldRow(_lblHide, _inHide, null, p, y, w);

        y += S(4);

        // 实例数 + 记录 hook 日志（同一行；放不下时自动拆成两行）
        y = LayoutCountRow(p, y, w);

        // 加强模式（左起） | 全局隐藏（右对齐）
        int pairH = S(26);
        int strongW = SizeOf(_lblStrong).Width + S(10) + S(46);
        int hideW = SizeOf(_lblGlobalHide).Width + S(10) + S(46);

        if (strongW + hideW + S(16) <= w)
        {
            LayoutSwitchPairStart(_lblStrong, _tgStrong, p, y);
            LayoutSwitchPairEnd(_lblGlobalHide, _tgGlobalHide, p + w, y);
            y += pairH + S(6);
        }
        else
        {
            // 极窄时上下排列
            LayoutSwitchPairStart(_lblStrong, _tgStrong, p, y);
            y += pairH + S(4);
            LayoutSwitchPairEnd(_lblGlobalHide, _tgGlobalHide, p + w, y);
            y += pairH + S(6);
        }

        // 高级选项（默认收起）：注册表隔离关键词
        if (_advancedOpen)
        {
            y += S(2);
            y = LayoutRegRow(p, y, w);
        }

        y += S(14);

        // 主按钮
        _btnLaunch.SetBounds(p, y, w, S(40));
        y += S(40) + S(10);

        // 次按钮
        int half = (w - S(10)) / 2;
        _btnAdd.SetBounds(p, y, half, S(36));
        _btnKill.SetBounds(p + half + S(10), y, half, S(36));
        y += S(36) + S(12);

        // 右侧面板的展开 / 收起：收起时它是唯一入口，做得像"显示更多"的居中胶囊
        int panelBtnW = Math.Min(w, Math.Max(S(150), FitButtonWidth(_btnPanels)));
        _btnPanels.SetBounds(p + (w - panelBtnW) / 2, y, panelBtnW, S(34));
        y += S(34);

        // 分隔线 + 小贴士贴底排布：窗口变高时空白留在按钮下方，不会把贴士顶飞
        int tipsH = _tips.MeasureHeight(w);
        int tipsTop = Math.Max(y + S(18), _cardForm.Height - p - tipsH);
        _divider.SetBounds(p, tipsTop - S(10), w, 1);
        _tips.SetBounds(p, tipsTop, w, tipsH);

        // 内容自然排布需要的高度（不贴底）：用于抬高窗口下限，收起态也靠它收紧窗口高度
        _requiredCardH = y + S(18) + tipsH + p;
    }

    /// <summary>标签在上、输入框在下的一行；右侧按钮宽度按文字自适应</summary>
    private int LayoutFieldRow(Label lbl, GlassInput input, GlassButton? btn, int p, int y, int w)
    {
        int lblH = SizeOf(lbl).Height;
        lbl.SetBounds(p, y, w, lblH);
        y += lblH + S(3);

        int inputH = S(34);
        int btnW = 0;
        if (btn is not null)
        {
            btnW = FitButtonWidth(btn);
            input.SetBounds(p, y, Math.Max(S(60), w - btnW - S(8)), inputH);
            btn.SetBounds(p + w - btnW, y, btnW, inputH);
        }
        else
        {
            input.SetBounds(p, y, w, inputH);
        }
        return y + inputH + S(10);
    }

    /// <summary>实例数步进器 + 右侧「记录 hook 日志」开关</summary>
    private int LayoutCountRow(int p, int y, int w)
    {
        int lblW = SizeOf(_lblCount).Width;
        int lblH = SizeOf(_lblCount).Height;
        int btnSz = S(30);
        int valW = S(30);

        _lblCount.SetBounds(p, y + (btnSz - lblH) / 2, lblW, lblH);
        int x = p + lblW + S(10);
        _btnMinus.SetBounds(x, y, btnSz, btnSz);
        x += btnSz + S(4);
        _lblCountValue.SetBounds(x, y, valW, btnSz);
        x += valW + S(4);
        _btnPlus.SetBounds(x, y, btnSz, btnSz);
        int leftEnd = x + btnSz;

        // 右侧「记录 hook 日志」开关：按文字实测宽度左移，绝不压住文字
        int tgW = S(46), tgH = S(26);
        int logW = SizeOf(_lblLogSwitch).Width;
        int logH = SizeOf(_lblLogSwitch).Height;
        int right = p + w;

        if (leftEnd + S(16) + logW + S(10) + tgW <= right)
        {
            // 同一行
            _tgLog.SetBounds(right - tgW, y + (btnSz - tgH) / 2, tgW, tgH);
            _lblLogSwitch.SetBounds(right - tgW - S(10) - logW, y + (btnSz - logH) / 2, logW, logH);
            return y + btnSz + S(10);
        }

        // 空间不足 → 换行独占一行
        y += btnSz + S(6);
        _tgLog.SetBounds(right - tgW, y, tgW, tgH);
        _lblLogSwitch.SetBounds(right - tgW - S(10) - logW, y + (tgH - logH) / 2, logW, logH);
        return y + tgH + S(10);
    }

    /// <summary>「文字 + 开关」成对排布（左起）：文字在左、开关紧跟其后</summary>
    private void LayoutSwitchPairStart(Label lbl, GlassToggle tg, int left, int y)
    {
        int tgW = S(46), tgH = S(26);
        int lw = SizeOf(lbl).Width;
        int lh = SizeOf(lbl).Height;
        lbl.SetBounds(left, y + (tgH - lh) / 2, lw, lh);
        tg.SetBounds(left + lw + S(10), y, tgW, tgH);
    }

    /// <summary>「文字 + 开关」成对排布（右对齐）：开关贴右边界、文字在开关左侧</summary>
    private void LayoutSwitchPairEnd(Label lbl, GlassToggle tg, int right, int y)
    {
        int tgW = S(46), tgH = S(26);
        int lw = SizeOf(lbl).Width;
        int lh = SizeOf(lbl).Height;
        tg.SetBounds(right - tgW, y, tgW, tgH);
        lbl.SetBounds(right - tgW - S(10) - lw, y + (tgH - lh) / 2, lw, lh);
    }

    /// <summary>注册表隔离关键词（高级选项）：标签在上、输入框在下，始终可编辑</summary>
    private int LayoutRegRow(int p, int y, int w)
    {
        int lh = SizeOf(_lblReg).Height;
        _lblReg.SetBounds(p, y, w, lh);
        y += lh + S(3);

        int inputH = S(32);
        _inRegFilter.SetBounds(p, y, w, inputH);
        return y + inputH + S(6);
    }

    /// <summary>按钮宽度按文字自适应（至少 S(56)）</summary>
    private int FitButtonWidth(GlassButton btn)
    {
        int tw = Theme.MeasureWidth(btn.Text, btn.Font);
        if (btn.Variant == GlassButton.Kind.Ghost) return Math.Max(S(40), tw + S(16));
        // 胶囊形左右各要留出圆角的空间，否则文字会顶到弧线上
        return Math.Max(S(64), tw + S(32));
    }

    /// <summary>标签的实际排版尺寸：直接取 Label 自身的结果，保证与绘制完全一致、绝不裁字</summary>
    private static Size SizeOf(Label l) => l.GetPreferredSize(Size.Empty);

    // ---------------- 右侧卡片 ----------------

    private void LayoutListCard()
    {
        int p = S(20);
        int titleH = SizeOf(_lblListTitle).Height;

        _lblListTitle.SetBounds(p, S(14), S(200), titleH);
        _lblStat.SetBounds(_cardList.Width - p - S(180), S(14), S(180), titleH);

        int listTop = S(14) + titleH + S(10);
        _list.SetBounds(S(10), listTop, _cardList.Width - S(20), Math.Max(S(60), _cardList.Height - listTop - S(10)));
    }

    private void LayoutLogCard()
    {
        int p = S(20);
        int titleH = SizeOf(_lblLogTitle).Height;

        _lblLogTitle.SetBounds(p, S(12), S(200), titleH);
        _btnClearLog.SetBounds(_cardLog.Width - p - S(52), S(9), S(52), S(24));

        int shellTop = S(12) + titleH + S(8);
        _logShell.SetBounds(S(12), shellTop, Math.Max(S(60), _cardLog.Width - S(24)),
                            Math.Max(S(60), _cardLog.Height - shellTop - S(12)));

        int innerW = _logShell.Width - S(24);
        int innerH = _logShell.Height - S(16);
        _log.SetBounds(S(12), S(8), Math.Max(S(40), innerW), Math.Max(S(20), innerH));
    }

    // ============================== 配置 ==============================

    private void ApplyConfig()
    {
        SetExePath(_config.ExePath, keepWorkDir: true);
        if (!string.IsNullOrWhiteSpace(_config.WorkDir))
            _inWork.Text = _config.WorkDir;
        _inArgs.Text = _config.Arguments;
        _inHide.Text = _config.HideNames;
        _count = Math.Clamp(_config.Count, 1, 32);
        _lblCountValue.Text = _count.ToString();
        _tgLog.Checked = _config.WriteHookLog;
        _tgStrong.Checked = _config.StrongMode;
        _tgGlobalHide.Checked = _config.GlobalHide;

        // 关键词有值才展开高级区，普通用户完全看不到这一项
        _inRegFilter.Text = _config.RegFilter;
        if (!string.IsNullOrWhiteSpace(_config.RegFilter))
        {
            _advancedOpen = true;
            _lblReg.Visible = true;
            _inRegFilter.Visible = true;
            _btnAdvanced.Text = "收起";
        }

        // 右侧面板的展开状态：默认收起，只留左侧配置区
        _cardList.Visible = _panelsOpen;
        _cardLog.Visible = _panelsOpen;
        SyncPanelChrome();

        TopMost = _config.TopMost;   // 句柄创建后再落一次，确保置顶真正生效
    }

    private void SaveConfig()
    {
        _config.ExePath = _inExe.Text.Trim();
        _config.WorkDir = _inWork.Text.Trim();
        _config.Arguments = _inArgs.Text.Trim();
        _config.HideNames = _inHide.Text.Trim();
        _config.Count = _count;
        _config.WriteHookLog = _tgLog.Checked;
        _config.StrongMode = _tgStrong.Checked;
        _config.GlobalHide = _tgGlobalHide.Checked;
        _config.RegFilter = _inRegFilter.Text.Trim();
        _config.PanelsOpen = _panelsOpen;
        _config.TopMost = TopMost;
        try { _config.Save(_configPath); } catch { /* 忽略 */ }
    }

    private void SetExePath(string path, bool keepWorkDir = false)
    {
        _inExe.Text = path;
        if (keepWorkDir && _inWork.Text.Length > 0) return;
        _inWork.Text = GuessWorkDir(path);
        _inHide.Text = GuessHideNames(path);
    }

    /// <summary>
    /// 程序的可执行文件常放在 bin\ 子目录里，但资源按上一级根目录找，
    /// 所以遇到 bin\ 时往上取一层（上一级确有资源目录或启动器才取，避免误判）。
    /// </summary>
    private static string GuessWorkDir(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath)) return "";
            string dir = Path.GetDirectoryName(exePath) ?? "";
            if (dir.Length == 0) return "";

            if (string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(dir) ?? "";
                if (parent.Length > 0 && LooksLikeRoot(parent)) return parent;
            }
            return dir;
        }
        catch { return ""; }
    }

    /// <summary>上一级是否像"程序根目录"：有资源目录，或有一个像启动器/客户端的 exe</summary>
    private static bool LooksLikeRoot(string dir)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir, "res")) ||
                Directory.Exists(Path.Combine(dir, "resources")) ||
                Directory.Exists(Path.Combine(dir, "data"))) return true;

            foreach (string exe in Directory.GetFiles(dir, "*.exe"))
            {
                string n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (n.Contains("launch") || n.Contains("start") || n.Contains("client")) return true;
            }
        }
        catch { /* 忽略 */ }
        return false;
    }

    /// <summary>
    /// 推断"需要保护的进程名"：启动器二进制里通常写死它要管理的同名进程，
    /// 与同目录 bin\*.exe 求交集即可。
    /// </summary>
    private static string GuessHideNames(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath)) return "";
            string dir = Path.GetDirectoryName(exePath) ?? "";
            string bin = Path.Combine(dir, "bin");
            if (!Directory.Exists(bin)) return "";

            var candidates = Directory.GetFiles(bin, "*.exe")
                                      .Select(Path.GetFileName)
                                      .Where(n => !string.IsNullOrEmpty(n))
                                      .Select(n => n!)
                                      .ToList();
            if (candidates.Count == 0) return "";

            string haystack = ReadBinaryText(exePath);
            if (haystack.Length == 0) return string.Join(";", candidates.Take(2));

            var hits = candidates.Where(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                                 .Take(6).ToList();
            if (hits.Count == 0) hits = candidates.Take(2).ToList();
            return string.Join(";", hits);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 把 exe 当文本扫描（ASCII + UTF-16LE 两种视图），用于找它内部写死的进程名。
    /// 不做整体 ToLowerInvariant：16MB 转小写会再复制两份，调用方用 OrdinalIgnoreCase 匹配即可。
    /// </summary>
    private static string ReadBinaryText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int len = (int)Math.Min(fs.Length, 16 * 1024 * 1024);
            if (len <= 0) return "";

            var buf = new byte[len];
            int total = 0;
            while (total < len)   // FileStream.Read 不保证一次读完
            {
                int n = fs.Read(buf, total, len - total);
                if (n <= 0) break;
                total += n;
            }

            string ascii = System.Text.Encoding.Latin1.GetString(buf, 0, total);
            // 奇数长度会让 Unicode 解码抛异常，向下取偶
            string wide = System.Text.Encoding.Unicode.GetString(buf, 0, total & ~1);
            return ascii + "\n" + wide;
        }
        catch { return ""; }
    }

    // ============================== 交互 ==============================

    private void BrowseExe()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择要运行的程序"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _inWork.Text = "";
        _inHide.Text = "";
        SetExePath(dlg.FileName);
    }

    private void BrowseWorkDir()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择目标程序的工作目录" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _inWork.Text = dlg.SelectedPath;
    }

    private LaunchOptions BuildOptions() => new()
    {
        ExePath = _inExe.Text.Trim(),
        Arguments = _inArgs.Text.Trim(),
        WorkingDirectory = string.IsNullOrWhiteSpace(_inWork.Text) ? null : _inWork.Text.Trim(),
        HideProcessNames = _inHide.Text.Trim(),
        WriteHookLog = _tgLog.Checked,
        StrongMode = _tgStrong.Checked,
        StrongSuffixes = _config.StrongSuffixes,
        GlobalHide = _tgGlobalHide.Checked,
        RegFilter = _inRegFilter.Text.Trim()
    };

    private async void LaunchAll()
    {
        if (_busy) return;
        var opt = BuildOptions();
        if (!File.Exists(opt.ExePath)) { Warn("请先选择一个有效的 exe 文件"); return; }

        int count = _count;   // 启动期间用户可能还在点 +/-
        SetBusy(true);
        AppendLog($"开始启动 {count} 个实例：{Path.GetFileName(opt.ExePath)}");
        AppendLog($"工作目录：{opt.WorkingDirectory ?? Path.GetDirectoryName(opt.ExePath)}");

        List<Instance> started = new();
        try
        {
            await Task.Run(() => started = _manager.LaunchMany(opt, count, AppendLog));
        }
        catch (Exception ex)
        {
            AppendLog("启动异常: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            RefreshList();
        }

        // 等过"接替"窗口再判断：启动器型目标此时应该已经跟到客户端进程中
        await Task.Delay(8000);
        if (IsDisposed || Disposing) return;

        foreach (var inst in started.Where(i => !i.IsRunning && i.ChaseCount == 0))
        {
            AppendLog($"⚠ 实例 PID={inst.Pid} 已退出（退出码 {inst.ExitCode}）。常见原因：工作目录不对、" +
                      "缺少启动参数，或目标自身在检测多开。");
        }
    }

    private void AddOne()
    {
        if (_busy) return;
        var opt = BuildOptions();
        if (!File.Exists(opt.ExePath)) { Warn("请先选择一个有效的 exe 文件"); return; }
        try
        {
            var inst = _manager.LaunchOne(opt);
            AppendLog($"已追加实例 PID={inst.Pid} 组号={inst.GroupId}");
        }
        catch (Exception ex)
        {
            AppendLog("追加失败: " + ex.Message);
        }
        RefreshList();
    }

    private void KillAll()
    {
        int n = _manager.KillAll();
        AppendLog(n > 0 ? $"已结束 {n} 个实例" : "当前没有运行中的实例");
        RefreshList();
    }

    private void OpenHookLog(Instance inst)
    {
        if (string.IsNullOrEmpty(inst.HookLogPath)) return;
        try
        {
            string? dir = Path.GetDirectoryName(inst.HookLogPath);
            if (dir is not null && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (IsDisposed || Disposing) return;   // 关闭窗口时启动线程可能还在收尾
        _btnLaunch.Enabled = !busy;
        _btnAdd.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
    private void RefreshList()
    {
        if (IsDisposed || Disposing) return;
        if (!_panelsOpen) return;   // 面板收起时列表不可见，省掉每秒一次的枚举
        _manager.Refresh();
        var list = _manager.Instances;
        _list.SetItems(list);

        int running = list.Count(i => i.IsRunning);
        _lblStat.Text = list.Count == 0 ? "" : $"运行中 {running} / 共 {list.Count}";
        _lblStat.ForeColor = running > 0 ? Theme.SuccessText : Theme.TextMuted;
    }

    private void AppendLog(string text)
    {
        // 窗体已关闭（后台启动线程还在回调）时直接丢弃，否则 BeginInvoke 会抛 ObjectDisposed
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendLog), text); }
            catch (InvalidOperationException) { /* 句柄已销毁，忽略 */ }
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _logLines.Enqueue(line);
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();

        _log.AppendText(line + Environment.NewLine);
        _logAppended++;

        // 攒够一批再整体重排一次：TextBox 的内容越短，AppendText 与滚动越省
        if (_logAppended > MaxLogLines)
        {
            _log.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
            _logAppended = _logLines.Count;
        }

        // 追加后把插入点收回到末尾：否则只读文本框会留一段蓝色反选，看着像出错
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.ScrollToCaret();
    }

    private void Warn(string text)
    {
        MessageBox.Show(this, text, "V5多开器Pro", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
