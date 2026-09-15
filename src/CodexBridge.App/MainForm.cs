using CodexBridge.Core;
using CodexBridge.Host.Contracts;
using CodexBridge.Host.Diagnostics;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodexRemote.UI;

namespace CodexBridge.App;

public sealed class MainForm : Form
{
    private readonly HostController _host;
    private readonly AutostartManager _autostart;
    private readonly IBridgeUiDialogs _dialogs;
    private readonly BufferedPanel _content = new();
    private readonly Dictionary<string, Button> _navigation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Label _overviewHost = ValueLabel();
    private readonly Label _overviewDesktop = ValueLabel();
    private readonly Label _overviewSignal = ValueLabel();
    private readonly Label _metricProjects = new();
    private readonly Label _metricProjectsNote = new();
    private readonly Label _metricDevices = new();
    private readonly Label _metricDevicesNote = new();
    private readonly Label _metricMessages = new();
    private readonly Label _metricMessagesNote = new();
    private readonly Label _metricHost = new();
    private readonly Label _metricHostNote = new();
    private readonly Label _overviewHeroIcon = new();
    private readonly Label _overviewHeroTitle = new();
    private readonly Label _overviewHeroCopy = new();
    private readonly Label _overviewHeroMeta = new();
    private Button? _hostButton;
    private Button? _desktopDiagnosticsButton;
    private ListView? _projectsList;
    private TableLayoutPanel? _projectsTable;
    private ModernCard? _projectsTableCard;
    private ModernCard? _projectsEmptyCard;
    private ModernCard? _projectsDetailsCard;
    private string? _projectsSelectedPath;
    private Label? _projectsState;
    private bool _suppressProjectChecks;
    private string _projectsSignature = "";
    private ListView? _devicesList;
    private TableLayoutPanel? _deviceCards;
    private TableLayoutPanel? _deviceDetails;
    private ManagedDeviceDto? _selectedDevice;
    private string _devicesSignature = "";
    private PictureBox? _pairingQr;
    private Label? _pairingState;
    private Label? _pairingExpiry;
    private Label? _pairingDisplayCode;
    private Label? _pairingLinkPreview;
    private Label? _pairingServiceBadge;
    private Button? _pairingCopyButton;
    private string? _pairingUrl;
    private ListView? _diagnosticsList;
    private TableLayoutPanel? _diagnosticsCards;
    private ModernTabControl? _diagnosticTabs;
    private string _diagnosticsSignature = "";
    private string _activePage = "overview";
    private int _timerTicks;
    private bool _refreshPending;

    public MainForm(HostController host, AutostartManager autostart, IBridgeUiDialogs? dialogs = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _dialogs = dialogs ?? new WinFormsBridgeUiDialogs();
        Text = "Codex Remote";
        AccessibleName = "Codex Remote 管理窗口";
        BackColor = UiTheme.Canvas;
        Font = UiTheme.BodyFont;
        MinimumSize = new Size(960, 640);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowIcon = true;
        Padding = Padding.Empty;
        KeyPreview = true;

        BuildLayout();
        _host.StateChanged += HostStateChanged;
        Shown += async (_, _) =>
        {
            FitToWorkingArea();
            await RefreshActivePageAsync(force: true);
            BeginInvoke(new Action(WarmPageCache));
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            _timerTicks++;
            if (_activePage == "pairing" || _timerTicks % 5 == 0)
                await RefreshActivePageAsync();
        };
        _refreshTimer.Start();
        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason != CloseReason.UserClosing) return;
            eventArgs.Cancel = true;
            Hide();
        };
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _ = RefreshActivePageAsync(force: true);
    }

    internal void ShowPairingFromTray()
    {
        ShowFromTray();
        Navigate("pairing");
    }

    internal void ShowDiagnosticsFromTray()
    {
        ShowFromTray();
        Navigate("diagnostics");
    }

    internal async Task ToggleHostFromTrayAsync()
    {
        if (_host.State == HostRuntimeState.Running) await _host.StopAsync();
        else await _host.StartAsync();
        await RefreshActivePageAsync(force: true);
    }

    internal Task RefreshForTestingAsync() => RefreshActivePageAsync(force: true);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _pairingQr?.Image?.Dispose();
            _host.StateChanged -= HostStateChanged;
        }
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        BackColor = UiTheme.Canvas;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Canvas,
            Padding = Padding.Empty,
            Name = "codex-shell",
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var toolbar = new ToolStrip { Dock = DockStyle.Fill, AutoSize = false, Height = 40, GripStyle = ToolStripGripStyle.Hidden, BackColor = UiTheme.Surface, Padding = new Padding(8, 6, 8, 6), Name = "codex-toolbar", RenderMode = ToolStripRenderMode.Professional };
        toolbar.Items.Add(ToolButton(IconGlyph.Refresh, "刷新", (_, _) => _ = RefreshActivePageAsync(force: true), primary: true));
        toolbar.Items.Add(ToolButton(IconGlyph.Pause, "暂停 Host", async (_, _) => await ToggleHostFromTrayAsync()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ToolButton(IconGlyph.Pair, "显示配对二维码", (_, _) => Navigate("pairing")));
        toolbar.Items.Add(ToolButton(IconGlyph.Diagnostic, "诊断", (_, _) => Navigate("diagnostics")));
        toolbar.Items.Add(new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right });
        toolbar.Items.Add(ToolButton(IconGlyph.Settings, "", (_, _) => { }));
        root.Controls.Add(toolbar, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.Canvas,
            Name = "codex-body",
        };
        // 交接稿的侧栏是 200px 的紧凑导航，不放置第二套品牌区。
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = BuildSidebar();
        body.Controls.Add(sidebar, 0, 0);
        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(24);
        _content.BackColor = UiTheme.Canvas;
        _content.Name = "codex-content";
        _content.ClientSizeChanged += (_, _) => ResizePageStacks();
        body.Controls.Add(_content, 1, 0);
        root.Controls.Add(body, 0, 1);

        var status = new StatusStrip { Dock = DockStyle.Fill, AutoSize = false, Height = 28, BackColor = UiTheme.Surface, ForeColor = UiTheme.TextSecondary, SizingGrip = false, Name = "codex-statusbar", RenderMode = ToolStripRenderMode.Professional };
        status.Items.Add(new ToolStripStatusLabel("● 运行中") { ForeColor = UiTheme.Success });
        status.Items.Add(new ToolStripStatusLabel("v0.9.1"));
        status.Items.Add(new ToolStripStatusLabel("0 设备"));
        status.Items.Add(new ToolStripStatusLabel("0 项目"));
        status.Items.Add(new ToolStripStatusLabel { Spring = true });
        status.Items.Add(new ToolStripStatusLabel(DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        root.Controls.Add(status, 0, 2);
        Controls.Add(root);
        Navigate("overview");
    }

    private static ToolStripButton ToolButton(string glyph, string text, EventHandler click, bool primary = false)
    {
        var button = new ToolStripButton(text)
        {
            Image = IconGlyph.Render(glyph, 16, primary ? Color.White : UiTheme.TextSecondary),
            DisplayStyle = string.IsNullOrWhiteSpace(text) ? ToolStripItemDisplayStyle.Image : ToolStripItemDisplayStyle.ImageAndText,
            AutoSize = true,
            Height = 28,
            Margin = new Padding(2, 5, 2, 5),
            Padding = new Padding(8, 2, 8, 2),
            Font = new Font("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = primary ? Color.White : UiTheme.TextSecondary,
            BackColor = primary ? UiTheme.Accent : Color.Transparent,
            ToolTipText = string.IsNullOrWhiteSpace(text) ? "设置" : text,
        };
        button.Click += click;
        return button;
    }

    private void FitToWorkingArea()
    {
        if (WindowState != FormWindowState.Normal) return;
        var area = Screen.FromControl(this).WorkingArea;
        var targetWidth = Math.Min(1440, Math.Max(MinimumSize.Width, (int)(area.Width * 0.88)));
        var targetHeight = Math.Min(900, Math.Max(MinimumSize.Height, (int)(area.Height * 0.88)));
        Size = new Size(targetWidth, targetHeight);
        CenterToScreen();
    }

    private Control BuildSidebar()
    {
        var sidebar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Brand.Surface,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(8, 12, 8, 12),
        };
        // 设计稿视觉行高为 30px；保留 40px 点击目标以满足 Windows 客户端可用性。
        for (var index = 0; index < 5; index++) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        sidebar.Name = "codex-sidebar";
        AddNavigation(sidebar, 0, "overview", "总览");
        AddNavigation(sidebar, 1, "projects", "授权项目");
        AddNavigation(sidebar, 2, "devices", "已配对设备");
        AddNavigation(sidebar, 3, "pairing", "公网配对");
        AddNavigation(sidebar, 4, "diagnostics", "诊断");
        var settings = new NavigationButton { Text = "设置", Glyph = IconGlyph.Settings, Dock = DockStyle.Fill, BackColor = Brand.Surface, ForeColor = Brand.Text2, Margin = new Padding(0, 4, 0, 4), AccessibleName = "设置" };
        settings.Click += (_, _) => MessageBox.Show(this, "设置将在后续版本提供。", "Codex Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
        sidebar.Controls.Add(settings, 0, 6);
        return sidebar;
    }

    private void AddNavigation(TableLayoutPanel sidebar, int row, string key, string text)
    {
        var button = new NavigationButton
        {
            Text = text,
            Glyph = key switch
            {
                "overview" => "\uE80F",
                "projects" => "\uE8B7",
                "devices" => "\uE8EA",
                "pairing" => "\uECA5",
                "diagnostics" => "\uE9D9",
                _ => "·",
            },
            Dock = DockStyle.Fill,
            ForeColor = Brand.Text2,
            BackColor = Brand.Surface,
            Margin = new Padding(0, 4, 0, 4),
            AccessibleName = $"导航：{text}",
            TabStop = true,
        };
        button.Click += (_, _) => Navigate(key);
        _navigation[key] = button;
        sidebar.Controls.Add(button, 0, row);
    }

    private void Navigate(string page)
    {
        if (string.Equals(_activePage, page, StringComparison.Ordinal) && _pages.ContainsKey(page))
        {
            _ = RefreshActivePageAsync(force: true);
            return;
        }
        _activePage = page;
        foreach (var (key, button) in _navigation)
        {
            var selected = key == page;
            if (button is NavigationButton navigation) navigation.Selected = selected;
            button.BackColor = UiTheme.Sidebar;
            button.ForeColor = selected ? UiTheme.Text : UiTheme.TextSecondary;
            button.Invalidate();
        }
        SuspendRedraw(_content, true);
        _content.SuspendLayout();
        Control control;
        try
        {
            foreach (Control existing in _content.Controls) existing.Visible = false;
            control = GetOrCreatePage(page);
            control.Visible = false;
            ResizePageStacks();
            if (control is ScrollableControl scrollable)
                scrollable.AutoScrollPosition = Point.Empty;
            control.PerformLayout();
            control.Visible = true;
            control.BringToFront();
        }
        finally
        {
            _content.ResumeLayout(true);
            SuspendRedraw(_content, false);
            _content.Invalidate(true);
            _content.Update();
        }
        _ = RefreshActivePageAsync(force: true);
    }

    private static void SuspendRedraw(Control control, bool suspend)
    {
        const int WmSetRedraw = 0x000B;
        _ = SendMessage(control.Handle, WmSetRedraw, suspend ? IntPtr.Zero : new IntPtr(1), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private Control GetOrCreatePage(string page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        var created = page switch
        {
            "projects" => BuildProjectsPage(),
            "devices" => BuildDevicesPage(),
            "pairing" => BuildPairingPage(),
            "diagnostics" => BuildDiagnosticsPage(),
            _ => BuildOverviewPage(),
        };
        created.Dock = DockStyle.Fill;
        _pages[page] = created;
        _content.Controls.Add(created);
        return created;
    }

    private void WarmPageCache()
    {
        if (IsDisposed || Disposing) return;
        _content.SuspendLayout();
        SuspendRedraw(_content, true);
        try
        {
            foreach (var page in new[] { "projects", "devices", "pairing", "diagnostics" })
            {
                var control = GetOrCreatePage(page);
                control.Visible = false;
            }
        }
        finally
        {
            _content.ResumeLayout(true);
            SuspendRedraw(_content, false);
            if (_pages.TryGetValue(_activePage, out var active)) active.Visible = true;
            _content.Invalidate(true);
        }
    }

    private void ResizePageStacks()
    {
        foreach (var page in _pages.Values)
        {
            if (!page.Visible && !ReferenceEquals(page, _pages.GetValueOrDefault(_activePage))) continue;
            // _content 的 Dock=Fill 已经自动扣除了自身 Padding，不能再次减边距。
            page.Dock = DockStyle.Fill;
            page.PerformLayout();
        }
    }

    private async Task RefreshActivePageAsync(bool force = false)
    {
        if (IsDisposed || Disposing || (!force && (!Visible || WindowState == FormWindowState.Minimized))) return;
        if (!await _refreshGate.WaitAsync(0))
        {
            _refreshPending = true;
            return;
        }
        try
        {
            do
            {
                _refreshPending = false;
                switch (_activePage)
                {
                    case "projects": await UpdateProjectsPageAsync(); break;
                    case "devices": UpdateDevicesPage(); break;
                    case "pairing": UpdatePairingPage(); break;
                    case "diagnostics": UpdateDiagnosticsPage(); break;
                    default: UpdateOverviewPage(); break;
                }
            }
            while (_refreshPending && !IsDisposed && !Disposing);
        }
        catch (Exception exception)
        {
            if (force) _dialogs.ShowError(this, exception.Message);
        }
        finally { _refreshGate.Release(); }
    }

    private Control BuildOverviewPage()
    {
        var panel = PageStack();
        panel.Controls.Add(OverviewWelcome());

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 168, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 12, 0, 0), Padding = Padding.Empty, BackColor = UiTheme.Canvas };
        for (var i = 0; i < 3; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        top.Controls.Add(OverviewHostCard(), 0, 0);
        top.Controls.Add(OverviewDevicesCard(), 1, 0);
        top.Controls.Add(OverviewProjectsCard(), 2, 0);
        panel.Controls.Add(top);

        var lower = new TableLayoutPanel { Dock = DockStyle.Top, Height = 184, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 14, 0, 0), Padding = Padding.Empty, BackColor = UiTheme.Canvas };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lower.Controls.Add(OverviewConnectionCard(), 0, 0);
        lower.Controls.Add(OverviewActivityCard(), 1, 0);
        panel.Controls.Add(lower);

        return panel;
    }

    private Control OverviewWelcome()
    {
        var block = new TableLayoutPanel { Dock = DockStyle.Top, Height = 54, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        block.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        block.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _overviewHeroTitle.Dock = DockStyle.Fill;
        _overviewHeroTitle.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
        _overviewHeroTitle.ForeColor = UiTheme.Text;
        _overviewHeroTitle.Text = "欢迎回来";
        _overviewHeroTitle.TextAlign = ContentAlignment.MiddleLeft;
        block.Controls.Add(_overviewHeroTitle, 0, 0);
        block.Controls.Add(new Label { Text = "Host 运行状态和远程访问概况", Dock = DockStyle.Fill, Font = UiTheme.SmallFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        return block;
    }

    private ModernCard OverviewHostCard()
    {
        var card = OverviewCard("Host 状态");
        var table = KeyValueTable();
        AddKeyValue(table, "进程", "运行中"); AddKeyValue(table, "CPU", "—"); AddKeyValue(table, "内存", "—"); AddKeyValue(table, "磁盘 I/O", "—"); AddKeyValue(table, "Uptime", "运行中");
        card.Controls.Add(table);
        var running = StatusPill("运行中", PillLabel.PillTone.Success, 76);
        running.Dock = DockStyle.Bottom;
        card.Controls.Add(running);
        return card;
    }

    private ModernCard OverviewDevicesCard()
    {
        var card = OverviewCard("已配对设备");
        var devices = _host.Services?.GetService<DeviceManagementService>()?.List() ?? [];
        var text = devices.Count == 0 ? "暂无设备\r\n前往公网配对扫描二维码" : string.Join("\r\n", devices.Take(4).Select(device => $"● {device.DisplayName}"));
        card.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, ForeColor = UiTheme.TextSecondary, Font = UiTheme.BodyFont, AutoEllipsis = true, Padding = new Padding(0, 8, 0, 0) });
        return card;
    }

    private ModernCard OverviewProjectsCard()
    {
        var card = OverviewCard("授权项目");
        var projects = _host.LoadConfiguration().Projects;
        var text = projects.Count == 0 ? "暂无授权项目" : string.Join("\r\n", projects.Take(4).Select(project => $"● {Path.GetFileName(project.Path)}"));
        card.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, ForeColor = UiTheme.TextSecondary, Font = UiTheme.BodyFont, AutoEllipsis = true, Padding = new Padding(0, 8, 0, 0) });
        return card;
    }

    private ModernCard OverviewConnectionCard()
    {
        var card = OverviewCard("当前连接");
        var table = KeyValueTable();
        AddKeyValue(table, "Signal 服务", "公网服务"); AddKeyValue(table, "RTT", "—"); AddKeyValue(table, "TURN 中继", "自动选择"); AddKeyValue(table, "Codex Desktop", "等待状态");
        card.Controls.Add(table);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        _desktopDiagnosticsButton = SecondaryButton("查看诊断"); _desktopDiagnosticsButton.Click += DesktopActionClick;
        _hostButton = SecondaryButton("停止"); _hostButton.Click += OverviewPrimaryActionClick;
        actions.Controls.Add(_desktopDiagnosticsButton); actions.Controls.Add(_hostButton); card.Controls.Add(actions);
        return card;
    }

    private static ModernCard OverviewActivityCard()
    {
        var card = OverviewCard("最近活动");
        card.Controls.Add(new Label { Text = "暂无最近活动\r\n设备连接和会话活动会显示在这里。", Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Font = UiTheme.BodyFont, Padding = new Padding(0, 8, 0, 0) });
        return card;
    }

    private static ModernCard OverviewSessionsCard()
    {
        var card = OverviewCard("最近会话");
        card.Height = 136;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, BackColor = UiTheme.Surface, Margin = new Padding(0, 8, 0, 0) };
        foreach (var width in new[] { 28f, 34f, 20f, 18f }) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        foreach (var title in new[] { "会话", "项目", "来源设备", "状态" }) table.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceMuted, ForeColor = UiTheme.Muted, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) }, Array.IndexOf(new[] { "会话", "项目", "来源设备", "状态" }, title), 0);
        var empty = new Label { Text = "暂无会话", Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
        table.Controls.Add(empty, 0, 1); table.SetColumnSpan(empty, 4);
        card.Controls.Add(table);
        return card;
    }

    private static ModernCard OverviewCard(string title)
    {
        var card = new ModernCard { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, BorderColor = UiTheme.Border, Radius = 8, Padding = new Padding(16), Margin = new Padding(0, 0, 8, 0) };
        card.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 28, ForeColor = UiTheme.Text, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.MiddleLeft });
        return card;
    }

    private static PillLabel StatusPill(string text, PillLabel.PillTone tone, int width) => new()
    {
        Text = text,
        Tone = tone,
        AutoSize = false,
        Width = width,
        Height = 26,
        Margin = new Padding(0, 8, 0, 0),
    };

    private static TableLayoutPanel KeyValueTable() => new() { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = false, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };

    private static void AddKeyValue(TableLayoutPanel table, string key, string value)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        table.ColumnStyles.Clear(); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        table.Controls.Add(new Label { Text = key, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        table.Controls.Add(new Label { Text = value, Dock = DockStyle.Fill, ForeColor = UiTheme.TextSecondary, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true }, 1, row);
    }

    private static Label TableHeader(string text) => new() { Text = text, Dock = DockStyle.Fill, Height = 34, ForeColor = UiTheme.Muted, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.MiddleLeft };

    private void UpdateOverviewPage()
    {
        var snapshot = _host.Services?.GetService<BridgeDiagnosticsService>()?.Capture();
        SetValue(_overviewHost, HostStateText(_host.State), _host.State == HostRuntimeState.Running);
        SetValue(_overviewDesktop, snapshot is null ? "等待 Host" : StateText(snapshot.Desktop.State), snapshot?.Desktop.State == BridgeComponentState.Online);
        SetValue(_overviewSignal, snapshot is null ? "等待 Host" : StateText(snapshot.Signal.State), snapshot?.Signal.State == BridgeComponentState.Online);
        var desktopOnline = snapshot?.Desktop.State == BridgeComponentState.Online;
        var projectCount = _host.LoadConfiguration().Projects.Count;
        var devices = _host.Services?.GetService<DeviceManagementService>()?.List() ?? [];
        _metricProjects.Text = projectCount.ToString();
        _metricProjectsNote.Text = desktopOnline ? "活跃中" : "Desktop 未启动";
        _metricProjectsNote.ForeColor = desktopOnline ? UiTheme.Success : UiTheme.Warning;
        _metricDevices.Text = devices.Count.ToString();
        _metricDevicesNote.Text = devices.Count == 0 ? "尚未配对" : "已连接";
        _metricDevicesNote.ForeColor = devices.Count == 0 ? UiTheme.Muted : UiTheme.Success;
        _metricMessages.Text = "0";
        _metricMessagesNote.Text = "未推送";
        _metricMessagesNote.ForeColor = UiTheme.Muted;
        _metricHost.Text = HostStateText(_host.State);
        _metricHostNote.Text = _host.State == HostRuntimeState.Running ? "公网服务运行中" : "服务已停止";
        _metricHostNote.ForeColor = _host.State == HostRuntimeState.Running ? UiTheme.Success : UiTheme.Warning;
        if (_navigation.TryGetValue("projects", out var projectsNavigation) && projectsNavigation is NavigationButton projectsButton)
        {
            projectsButton.Badge = projectCount.ToString();
            projectsButton.Invalidate();
        }
        if (_navigation.TryGetValue("devices", out var devicesNavigation) && devicesNavigation is NavigationButton devicesButton)
        {
            devicesButton.Badge = devices.Count.ToString();
            devicesButton.Invalidate();
        }
        _overviewHeroTitle.Text = desktopOnline ? "欢迎回来" : "Codex Desktop 未启动";
        if (_hostButton is null) return;
        _hostButton.Text = desktopOnline ? "停止" : "启动 Desktop";
        if (_hostButton is ModernButton modernButton)
            modernButton.ButtonVariant = desktopOnline ? ModernButton.Variant.Default : ModernButton.Variant.Primary;
        _desktopDiagnosticsButton!.Visible = !desktopOnline;
        _hostButton.Enabled = _host.State is not (HostRuntimeState.Starting or HostRuntimeState.Stopping);

    }

    private void DesktopActionClick(object? sender, EventArgs e) => Navigate("diagnostics");

    private async void OverviewPrimaryActionClick(object? sender, EventArgs e)
    {
        var desktopOnline = _host.Services?.GetService<BridgeDiagnosticsService>()?.Capture().Desktop.State == BridgeComponentState.Online;
        if (desktopOnline)
        {
            await RunUiActionAsync(() => _host.StopAsync());
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("codex://") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _dialogs.ShowError(this, $"无法启动 Codex Desktop：{exception.Message}");
        }
    }

    private Control BuildProjectsPage()
    {
        var panel = PageStack();
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 44, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8), Padding = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        heading.Controls.Add(TitleBlock("授权项目", "选择允许手机查看的 Codex 项目，新会话会自动继承项目权限。"), 0, 0);
        var add = PrimaryButton("添加项目");
        add.Image = IconGlyph.Render(IconGlyph.Add, 15, Color.White);
        add.ImageAlign = ContentAlignment.MiddleLeft;
        add.TextImageRelation = TextImageRelation.ImageBeforeText;
        add.AccessibleName = "添加项目"; add.Anchor = AnchorStyles.Right; add.Width = 112; add.MinimumSize = new Size(112, 32); add.Height = 32; add.AutoSize = false; add.Padding = new Padding(8, 0, 8, 0); add.TextAlign = ContentAlignment.MiddleCenter;
        add.Click += async (_, _) =>
        {
            var path = _dialogs.SelectProject(this);
            if (string.IsNullOrWhiteSpace(path)) return;
            var current = _host.LoadConfiguration().Projects.Select(project => project.Path)
                .Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await RunUiActionAsync(() => _host.UpdateProjectsAsync(current));
            _projectsSignature = "";
        };
        heading.Controls.Add(add, 1, 0); panel.Controls.Add(heading);

        var filters = new ModernCard { Dock = DockStyle.Top, Height = 48, Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 0, 0, 8) };
        // 过滤器严格按设计稿分成三段：固定搜索框、弹性间隔、右侧筛选操作。
        // 不把搜索框放进百分比列，避免它在宽窗口下吞掉整行并制造空白区域。
        var filterLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Padding = Padding.Empty };
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 382));
        var searchFrame = new RoundedPanel { Dock = DockStyle.Fill, CornerRadius = 6, BorderColor = UiTheme.Border, BorderWidth = 1, BackColor = UiTheme.Surface, Margin = new Padding(0, 1, 8, 1), Padding = new Padding(8, 0, 8, 0) };
        var searchGroup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        searchGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
        searchGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchGroup.Controls.Add(new Label { Text = IconGlyph.Search, Dock = DockStyle.Fill, Font = UiTheme.IconFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty }, 0, 0);
        var search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Muted, Font = UiTheme.BodyFont, Text = "搜索项目名或路径", Margin = new Padding(0, 7, 0, 0) };
        search.GotFocus += (_, _) => { if (search.Text == "搜索项目名或路径") { search.Text = ""; search.ForeColor = UiTheme.Text; } };
        search.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(search.Text)) { search.Text = "搜索项目名或路径"; search.ForeColor = UiTheme.Muted; } };
        searchGroup.Controls.Add(search, 1, 0);
        searchFrame.Controls.Add(searchGroup);
        filterLayout.Controls.Add(searchFrame, 0, 0);

        var filterActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        filterActions.Controls.Add(new Label { Text = "权限", AutoSize = false, Width = 34, Height = 32, ForeColor = UiTheme.Muted, Font = UiTheme.SmallFont, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 2, 4, 0) });
        filterActions.Controls.Add(ProjectFilterCombo("权限", ["全部", "Pro", "基础"]));
        filterActions.Controls.Add(new Label { Text = "状态", AutoSize = false, Width = 32, Height = 32, ForeColor = UiTheme.Muted, Font = UiTheme.SmallFont, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(8, 2, 4, 0) });
        filterActions.Controls.Add(ProjectFilterCombo("状态", ["全部", "活跃", "离线"]));
        var proOnly = new CheckBox { Text = "仅 Pro", AutoSize = false, Width = 66, Height = 32, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.TextSecondary, Font = UiTheme.SmallFont, Margin = new Padding(8, 2, 0, 0) };
        filterActions.Controls.Add(proOnly);
        var reset = SecondaryButton("重置"); reset.AutoSize = false; reset.Width = 58; reset.Height = 32; reset.Padding = Padding.Empty; reset.Margin = new Padding(8, 2, 0, 0); reset.Click += (_, _) => { search.Text = "搜索项目名或路径"; search.ForeColor = UiTheme.Muted; proOnly.Checked = false; };
        filterActions.Controls.Add(reset);
        filterLayout.Controls.Add(filterActions, 2, 0);
        filters.Controls.Add(filterLayout); panel.Controls.Add(filters);

        var boundary = InfoCard("授权边界", "Host 只管理项目授权。删除项目、删除会话和归档会话必须在 Codex Desktop 中完成。", UiTheme.Accent);
        panel.Controls.Add(boundary);
        _projectsState = SectionNote("正在读取 Codex Desktop 项目…");
        _projectsState.Margin = new Padding(0, 6, 0, 2);
        panel.Controls.Add(_projectsState);
        _projectsList = CreateListView(panel, ["允许访问", "项目", "未归档会话", "路径"], [14, 24, 18, 44]);
        _projectsList.CheckBoxes = true;
        _projectsList.Height = 1;
        _projectsList.Visible = false;
        _projectsList.ItemChecked += ProjectsItemChecked;
        panel.Controls.Add(_projectsList);
        _projectsTableCard = new ModernCard { Dock = DockStyle.Top, Height = 150, MinimumSize = new Size(0, 150), Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(0), Margin = new Padding(0, 6, 0, 0), Name = "projects-table-card" };
        _projectsTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, BackColor = UiTheme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        foreach (var weight in new[] { 25f, 24f, 11f, 11f, 9f, 12f, 8f })
            _projectsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, weight));
        _projectsTable.Resize += (_, _) => ResizeProjectsTableColumns();
        _projectsTable.Layout += (_, _) => ResizeProjectsTableColumns();
        _projectsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        foreach (var (label, column) in new[] { ("项目", 0), ("路径", 1), ("权限", 2), ("状态", 3), ("会话", 4), ("最近活跃", 5), ("操作", 6) })
            _projectsTable.Controls.Add(ProjectHeader(label, column == 6 ? ContentAlignment.MiddleRight : column is 2 or 3 or 4 ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft), column, 0);
        // 首次绘制时立即显示加载空状态，避免后台发现项目尚未完成时出现整块白色表格。
        _projectsTable.RowCount = 2;
        _projectsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var initialEmpty = new EmptyStatePanel
        {
            Dock = DockStyle.Fill,
            Glyph = IconGlyph.Folder,
            Title = "正在读取项目",
            Description = "正在从 Codex Desktop 读取项目，请稍候。",
            MinimumSize = new Size(0, 164),
        };
        _projectsTable.Controls.Add(initialEmpty, 0, 1);
        _projectsTable.SetColumnSpan(initialEmpty, 7);
        _projectsTableCard.Height = 236;
        _projectsTableCard.Controls.Add(_projectsTable);
        panel.Controls.Add(_projectsTableCard);
        _projectsEmptyCard = new ModernCard { Dock = DockStyle.Top, Height = 138, MinimumSize = new Size(0, 138), Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(16), Margin = new Padding(0, 6, 0, 0), Visible = false, Name = "projects-empty-card" };
        _projectsEmptyCard.Controls.Add(new EmptyStatePanel { Dock = DockStyle.Fill, Glyph = IconGlyph.Folder, Title = "正在读取项目", Description = "Codex Desktop 项目加载完成后会显示在这里。", MinimumSize = new Size(0, 100) });
        panel.Controls.Add(_projectsEmptyCard);
        _projectsDetailsCard = new ModernCard { Dock = DockStyle.Top, Height = 124, MinimumSize = new Size(0, 124), Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(16), Margin = new Padding(0, 8, 0, 0), Name = "projects-details-card" };
        _projectsDetailsCard.Controls.Add(new EmptyStatePanel { Dock = DockStyle.Fill, Glyph = IconGlyph.Folder, Title = "选择一个项目查看详情", Description = "项目路径、授权范围和最近活动会显示在这里。", MinimumSize = new Size(0, 90) });
        panel.Controls.Add(_projectsDetailsCard);
        panel.Controls.Add(InfoCard("提示", "移除项目授权不会删除本地项目文件，手机端将立即无法访问该项目的会话。", UiTheme.Muted));
        return panel;
    }

    private static ComboBox FilterCombo(string label, string[] values)
    {
        var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = UiTheme.SmallFont, ForeColor = UiTheme.TextSecondary, BackColor = UiTheme.SurfaceMuted, Margin = new Padding(4, 2, 4, 2) };
        combo.Items.AddRange(values);
        combo.SelectedIndex = 0;
        combo.AccessibleName = label;
        return combo;
    }

    private static ComboBox ProjectFilterCombo(string label, string[] values)
    {
        var combo = FilterCombo(label, values);
        combo.Dock = DockStyle.None;
        combo.Width = 82;
        combo.Height = 32;
        combo.Margin = new Padding(0, 4, 0, 0);
        return combo;
    }

    private void ResizeProjectsTableColumns()
    {
        if (_projectsTable is null || _projectsTable.ClientSize.Width <= 0) return;
        var width = _projectsTable.ClientSize.Width;
        // 最小窗口（960px）时主内容可用宽度约 688px；固定列必须为路径列保留
        // 可见空间，避免右侧“操作”列把整张表推出客户端边界。
        var fixedWidths = new[] { 190, 0, 68, 68, 56, 96, 76 };
        _projectsTable.SuspendLayout();
        try
        {
            _projectsTable.ColumnStyles.Clear();
            var pathWidth = Math.Max(120, width - fixedWidths.Sum());
            fixedWidths[1] = pathWidth;
            for (var index = 0; index < fixedWidths.Length; index++)
                _projectsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, fixedWidths[index]));
            Console.WriteLine($"projects table={width} cols={string.Join(',', fixedWidths)} client={_projectsTable.ClientSize.Width}");
        }
        finally { _projectsTable.ResumeLayout(true); }
    }

    private async Task UpdateProjectsPageAsync()
    {
        if (_projectsList is null || _projectsState is null) return;
        var authorized = _host.LoadConfiguration().Projects
            .Select(project => TargetPolicy.NormalizePath(project.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<DiscoveredProject> discovered;
        var discoveryAvailable = true;
        try
        {
            // 项目发现会读取本机 Codex 数据库，放到后台线程避免导航点击被 I/O 阻塞。
            discovered = await Task.Run(() => _host.DiscoverProjectsAsync().GetAwaiter().GetResult());
            if (IsDisposed || Disposing || _projectsList is null || _projectsState is null) return;
            _projectsState.Text = discovered.Count == 0
                ? "Codex Desktop 当前没有可显示的未归档会话。"
                : $"已自动发现 {discovered.Count} 个项目。勾选后手机即可访问。";
            _projectsState.ForeColor = UiTheme.Muted;
        }
        catch (Exception)
        {
            discovered = [];
            discoveryAvailable = false;
            _projectsState.Text = $"Codex Desktop 暂不可用，仍显示 {authorized.Count} 个已授权项目。";
            _projectsState.ForeColor = UiTheme.Warning;
        }

        var rows = discovered.ToList();
        foreach (var missing in authorized.Where(path => rows.All(project =>
                     !string.Equals(project.Path, path, StringComparison.OrdinalIgnoreCase))))
            rows.Add(new DiscoveredProject(Path.GetFileName(missing), missing, 0, 0));

        var signature = discoveryAvailable + "|" + string.Join('\n', rows.Select(project =>
            $"{project.Path}|{project.ThreadCount}|{project.UpdatedAtMs}|{authorized.Contains(project.Path)}"));
        if (signature == _projectsSignature) return;
        _projectsSignature = signature;
        var selected = _projectsList.SelectedItems.Count == 1 ? (string?)_projectsList.SelectedItems[0].Tag : null;
        _suppressProjectChecks = true;
        _projectsList.BeginUpdate();
        try
        {
            _projectsList.Items.Clear();
            foreach (var project in rows.OrderByDescending(project => project.UpdatedAtMs))
            {
                var allowed = authorized.Contains(project.Path);
                var item = new ListViewItem(allowed ? "已允许" : "未允许")
                {
                    Checked = allowed,
                    Tag = project.Path,
                };
                item.SubItems.Add(project.Name);
                item.SubItems.Add(project.ThreadCount.ToString());
                item.SubItems.Add(project.Path);
                _projectsList.Items.Add(item);
                if (string.Equals(selected, project.Path, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
            }
        }
        finally
        {
            _projectsList.EndUpdate();
            _suppressProjectChecks = false;
        }
        RenderProjectsTable(rows, authorized, discoveryAvailable);
    }

    private void RenderProjectsTable(
        IReadOnlyList<DiscoveredProject> projects,
        IReadOnlySet<string> authorized,
        bool discoveryAvailable)
    {
        if (_projectsTable is null || _projectsTableCard is null) return;
        _projectsTable.SuspendLayout();
        try
        {
            while (_projectsTable.Controls.Count > 7)
                _projectsTable.Controls.RemoveAt(_projectsTable.Controls.Count - 1);
            while (_projectsTable.RowStyles.Count > 1)
                _projectsTable.RowStyles.RemoveAt(_projectsTable.RowStyles.Count - 1);
            _projectsTable.RowCount = 1;
            // 表头始终固定为设计稿的 42px，避免空状态时 TableLayoutPanel
            // 把剩余高度错误分配给表头。
            if (_projectsTable.RowStyles.Count == 0)
                _projectsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            else
            {
                _projectsTable.RowStyles[0].SizeType = SizeType.Absolute;
                _projectsTable.RowStyles[0].Height = 32;
            }

            var ordered = projects
                .OrderByDescending(project => authorized.Contains(project.Path))
                .ThenByDescending(project => project.UpdatedAtMs)
                .ToArray();
            if (ordered.Length == 0)
            {
                // 空状态需要足够的垂直空间绘制图标、标题和说明，避免在
                // Codex Desktop 不可用时退化成一块没有内容的白色表格。
                _projectsTableCard.Height = 236;
                _projectsTable.RowCount = 2;
                _projectsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var empty = new EmptyStatePanel
                {
                    Dock = DockStyle.Fill,
                    Glyph = IconGlyph.Folder,
                    Title = discoveryAvailable ? "尚未发现可授权项目" : "Codex Desktop 暂不可用",
                    Description = discoveryAvailable ? "启动 Codex Desktop 后，可在这里选择要授权给手机的项目。" : "启动 Codex Desktop 后重新检测即可发现项目。",
                    MinimumSize = new Size(0, 100),
                };
                _projectsTable.Controls.Add(empty, 0, 1);
                _projectsTable.SetColumnSpan(empty, 7);
                RenderProjectDetails(null, false, 0);
                return;
            }
            _projectsTableCard.Height = 32 + ordered.Length * 52 + 2;

            var deviceCount = _host.Services?.GetService<DeviceManagementService>()?.List().Count ?? 0;
            foreach (var project in ordered)
            {
                var row = _projectsTable.RowCount++;
                _projectsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
                var allowed = authorized.Contains(project.Path);
                var action = allowed ? DangerButton("移除") : SecondaryButton("授权");
                action.Dock = DockStyle.None;
                action.Anchor = AnchorStyles.Right;
                action.Margin = new Padding(4, 10, 8, 10);
                action.AutoSize = false;
                action.Width = 64;
                action.Height = 30;
                action.MinimumSize = new Size(64, 30);
                action.Padding = Padding.Empty;
                action.Click += async (_, _) => { _projectsSelectedPath = project.Path; await SetProjectAuthorizationAsync(project.Path, !allowed); };
                var identity = ProjectIdentityCell(project, allowed);
                identity.Click += (_, _) => { _projectsSelectedPath = project.Path; RenderProjectDetails(project, allowed, deviceCount); };
                _projectsTable.Controls.Add(identity, 0, row);
                _projectsTable.Controls.Add(ProjectTextCell(ShortPath(project.Path), UiTheme.Muted), 1, row);
                _projectsTable.Controls.Add(ProjectStatusCell(allowed ? "Pro" : "基础", allowed ? PillLabel.PillTone.Pro : PillLabel.PillTone.Brand), 2, row);
                _projectsTable.Controls.Add(ProjectStatusCell(project.ThreadCount > 0 ? "活跃" : "空闲", project.ThreadCount > 0 ? PillLabel.PillTone.Success : PillLabel.PillTone.Warning), 3, row);
                _projectsTable.Controls.Add(ProjectTextCell(project.ThreadCount.ToString(), UiTheme.TextSecondary, ContentAlignment.MiddleCenter), 4, row);
                _projectsTable.Controls.Add(ProjectTextCell(ProjectActivity(project.UpdatedAtMs), UiTheme.Muted), 5, row);
                _projectsTable.Controls.Add(action, 6, row);
                AddProjectRowDivider(row);
            }
            _projectsTableCard.Height = 32 + ordered.Length * 52 + 2;
            var selectedProject = ordered.FirstOrDefault(project => string.Equals(project.Path, _projectsSelectedPath, StringComparison.OrdinalIgnoreCase)) ?? ordered[0];
            RenderProjectDetails(selectedProject, authorized.Contains(selectedProject.Path), deviceCount);
        }
        finally { _projectsTable.ResumeLayout(true); }
    }

    private void RenderProjectDetails(DiscoveredProject? project, bool allowed, int deviceCount)
    {
        if (_projectsDetailsCard is null) return;
        _projectsDetailsCard.Controls.Clear();
        if (project is null)
        {
            var empty = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(10, 4, 10, 4) };
            empty.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); empty.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var icon = new RoundedPanel { Dock = DockStyle.Fill, CornerRadius = 10, BorderWidth = 0, BackColor = UiTheme.AccentSoft };
            icon.Controls.Add(new Label { Text = IconGlyph.Folder, Dock = DockStyle.Fill, Font = UiTheme.IconFont, ForeColor = UiTheme.Accent, TextAlign = ContentAlignment.MiddleCenter });
            var copy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(12, 0, 0, 0) };
            copy.Controls.Add(new Label { Text = "选择一个项目查看详情", Dock = DockStyle.Fill, Font = UiTheme.LabelFont, ForeColor = UiTheme.Text, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
            copy.Controls.Add(new Label { Text = "项目路径、授权范围和最近活动会显示在这里。", Dock = DockStyle.Fill, Font = UiTheme.SmallFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.TopLeft }, 0, 1);
            empty.Controls.Add(icon, 0, 0); empty.Controls.Add(copy, 1, 0);
            _projectsDetailsCard.Controls.Add(empty);
            return;
        }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label { Text = $"详情 · {project.Name}", Dock = DockStyle.Fill, Font = UiTheme.LabelFont, ForeColor = UiTheme.Text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        layout.Controls.Add(title, 0, 0);
        var action = allowed ? DangerButton("移除授权") : PrimaryButton("授权项目");
        action.AutoSize = false; action.Width = 104; action.Height = 34; action.Anchor = AnchorStyles.Right; action.Margin = new Padding(0, 0, 0, 0);
        action.Click += async (_, _) => { _projectsSelectedPath = project.Path; await SetProjectAuthorizationAsync(project.Path, !allowed); };
        layout.Controls.Add(action, 2, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(ProjectDetailColumn("授权范围", allowed ? "手机可访问此项目" : "尚未授权", allowed ? UiTheme.Success : UiTheme.Muted), 0, 1);
        layout.Controls.Add(ProjectDetailColumn("项目统计", $"{project.ThreadCount:N0} 个未归档会话 · {deviceCount} 台设备", UiTheme.TextSecondary), 1, 1);
        layout.Controls.Add(ProjectDetailColumn("本地路径", ShortPath(project.Path), UiTheme.Muted), 2, 1);
        _projectsDetailsCard.Controls.Add(layout);
    }

    private static Control ProjectDetailColumn(string title, string value, Color valueColor)
    {
        var copy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 0, 16, 0) };
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 18)); copy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        copy.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Font = UiTheme.SmallFont, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        copy.Controls.Add(new Label { Text = value, Dock = DockStyle.Fill, ForeColor = valueColor, Font = UiTheme.BodyFont, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, 1);
        return copy;
    }

    private static Label ProjectHeader(string text, ContentAlignment alignment) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 32,
        Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        BackColor = UiTheme.SurfaceMuted,
        ForeColor = UiTheme.Muted,
        Font = UiTheme.LabelFont,
        TextAlign = alignment,
        Padding = alignment == ContentAlignment.MiddleLeft ? new Padding(16, 0, 4, 0) : Padding.Empty,
    };

    private static Control ProjectIdentityCell(DiscoveredProject project, bool allowed)
    {
        var cell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = UiTheme.Surface, Padding = new Padding(12, 7, 4, 7), Margin = Padding.Empty };
        cell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var mark = new RoundedPanel { Width = 30, Height = 30, Anchor = AnchorStyles.Left | AnchorStyles.Top, CornerRadius = 7, BorderWidth = 0, BackColor = allowed ? UiTheme.AccentSoft : UiTheme.SurfaceMuted, Margin = new Padding(0, 4, 8, 0) };
        mark.Controls.Add(new Label { Text = IconGlyph.Folder, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = allowed ? UiTheme.Accent : UiTheme.Muted, Font = UiTheme.IconFont, AutoSize = false });
        var copy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.Surface, Margin = Padding.Empty };
        copy.Controls.Add(new Label { Text = project.Name, Dock = DockStyle.Fill, ForeColor = UiTheme.Text, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, AutoSize = false }, 0, 0);
        copy.Controls.Add(new Label { Text = allowed ? "已授权 · Codex 项目" : "待授权 · Codex 项目", Dock = DockStyle.Fill, ForeColor = allowed ? UiTheme.Success : UiTheme.Muted, Font = UiTheme.SmallFont, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, AutoSize = false }, 0, 1);
        cell.Controls.Add(mark, 0, 0); cell.Controls.Add(copy, 1, 0);
        return cell;
    }

    private static Label ProjectTextCell(string text, Color color, ContentAlignment alignment = ContentAlignment.MiddleLeft) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = color,
        BackColor = UiTheme.Surface,
        Font = UiTheme.BodyFont,
        TextAlign = alignment,
        AutoEllipsis = true,
        AutoSize = false,
        MaximumSize = new Size(0, 0),
        Padding = alignment == ContentAlignment.MiddleLeft ? new Padding(12, 0, 4, 0) : Padding.Empty,
    };

    private static Control ProjectStatusCell(string text, PillLabel.PillTone tone)
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 14, 0, 0),
            Margin = Padding.Empty,
            Controls = { new PillLabel { Text = text, Tone = tone, Height = 24, Margin = Padding.Empty } },
        };
    }

    private void AddProjectRowDivider(int row)
    {
        if (_projectsTable is null) return;
        foreach (Control control in _projectsTable.Controls.Cast<Control>().Where(control => _projectsTable.GetRow(control) == row))
            control.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(238, 238, 242)); e.Graphics.DrawLine(pen, 0, control.Height - 1, control.Width, control.Height - 1); };
    }

    private static string ProjectActivity(long updatedAtMs) => updatedAtMs <= 0
        ? "已授权"
        : DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs).ToLocalTime().ToString("yyyy-MM-dd");

    private static string ShortPath(string path)
    {
        if (path.Length <= 44) return path;
        return path[..18] + "…" + path[^22..];
    }

    private async Task SetProjectAuthorizationAsync(string path, bool allowed)
    {
        var current = _host.LoadConfiguration().Projects.Select(project => TargetPolicy.NormalizePath(project.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed) current.Add(TargetPolicy.NormalizePath(path));
        else current.Remove(TargetPolicy.NormalizePath(path));
        await RunUiActionAsync(() => _host.UpdateProjectsAsync(current.ToArray()));
        _projectsSignature = "";
    }

    private void ProjectsItemChecked(object? sender, ItemCheckedEventArgs eventArgs)
    {
        if (_suppressProjectChecks || _projectsList is null) return;
        BeginInvoke(async () =>
        {
            if (_projectsList is null || IsDisposed) return;
            try
            {
                var paths = _projectsList.Items.Cast<ListViewItem>()
                    .Where(item => item.Checked)
                    .Select(item => (string)item.Tag!)
                    .ToArray();
                await _host.UpdateProjectsAsync(paths);
                _projectsSignature = "";
                _ = RefreshActivePageAsync(force: true);
            }
            catch (Exception exception)
            {
                _dialogs.ShowError(this, exception.Message);
            }
        });
    }

    private Control BuildDevicesPage()
    {
        var panel = PageStack();
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 44, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8), Padding = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        heading.Controls.Add(TitleBlock("已配对设备", "本电脑已授权的远程手机"), 0, 0);
        var refresh = SecondaryButton("刷新");
        refresh.Image = IconGlyph.Render(IconGlyph.Refresh, 14, UiTheme.TextSecondary);
        refresh.ImageAlign = ContentAlignment.MiddleLeft;
        refresh.TextImageRelation = TextImageRelation.ImageBeforeText;
        refresh.AutoSize = false; refresh.Width = 80; refresh.Height = 32; refresh.Anchor = AnchorStyles.Right; refresh.Padding = new Padding(8, 0, 8, 0);
        refresh.Click += (_, _) => _ = RefreshActivePageAsync(force: true);
        heading.Controls.Add(refresh, 1, 0);
        panel.Controls.Add(heading);

        var filters = new ModernCard { Dock = DockStyle.Top, Height = 48, Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 0, 0, 8) };
        var filterLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent };
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        filterLayout.Controls.Add(new Label { Text = IconGlyph.Search, Dock = DockStyle.Fill, Font = UiTheme.IconFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleCenter }, 0, 0);
        filterLayout.Controls.Add(new ModernTextBox { Dock = DockStyle.Fill, Height = 30, Font = UiTheme.BodyFont, PlaceholderText = "搜索设备名 / ID", Margin = new Padding(0, 2, 8, 2) }, 1, 0);
        filterLayout.Controls.Add(FilterCombo("连接状态", ["全部状态", "在线", "离线"]), 2, 0);
        filterLayout.Controls.Add(FilterCombo("权限", ["全部权限", "Pro", "只读"]), 3, 0);
        filters.Controls.Add(filterLayout); panel.Controls.Add(filters);
        _devicesList = CreateListView(panel, ["设备", "连接", "权限", "最近使用"], [38, 16, 20, 26]);
        _devicesList.Height = 1;
        _devicesList.Visible = false;
        panel.Controls.Add(_devicesList);
        _deviceCards = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(0, 176), ColumnCount = 1, BackColor = UiTheme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        _deviceCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _deviceDetails = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(0, 54), ColumnCount = 2, BackColor = UiTheme.Surface, Padding = new Padding(18, 8, 18, 8), Margin = Padding.Empty };
        _deviceDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        _deviceDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        var deviceBody = new TableLayoutPanel { Dock = DockStyle.Top, Height = Math.Max(360, ClientSize.Height - 190), MinimumSize = new Size(0, 360), ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Canvas, Margin = Padding.Empty, Padding = Padding.Empty };
        deviceBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        deviceBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        var deviceListCard = new ModernCard { Dock = DockStyle.Fill, Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(12), Margin = new Padding(0, 0, 10, 0) };
        deviceListCard.Controls.Add(_deviceCards);
        var deviceDetailCard = new ModernCard { Dock = DockStyle.Fill, Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(0), Margin = Padding.Empty };
        deviceDetailCard.Controls.Add(_deviceDetails);
        deviceBody.Controls.Add(deviceListCard, 0, 0);
        deviceBody.Controls.Add(deviceDetailCard, 1, 0);
        panel.Controls.Add(deviceBody);
        panel.Controls.Add(InfoCard("安全提醒", "撤销授权只会移除连接权限，不会删除设备上的项目文件或历史会话。", UiTheme.Warning));
        RenderDeviceCards([]);
        return panel;
    }

    private void UpdateDevicesPage()
    {
        if (_devicesList is null) return;
        var devices = _host.Services?.GetService<DeviceManagementService>()?.List() ?? [];
        var signature = string.Join('\n', devices.Select(device => $"{device.DeviceId}|{device.CanSend}|{device.LastSeenAt:O}"));
        if (signature == _devicesSignature) return;
        _devicesSignature = signature;
        var selected = _devicesList.SelectedItems.Count == 1
            ? ((ManagedDeviceDto)_devicesList.SelectedItems[0].Tag!).DeviceId
            : null;
        _devicesList.BeginUpdate();
        try
        {
            _devicesList.Items.Clear();
            foreach (var device in devices)
            {
                var item = new ListViewItem(device.DisplayName) { Tag = device };
                item.SubItems.Add("公网");
                item.SubItems.Add(device.CanSend ? "Pro 可发送" : "免费只读");
                item.SubItems.Add(device.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                _devicesList.Items.Add(item);
                if (device.DeviceId == selected) item.Selected = true;
            }
        }
        finally { _devicesList.EndUpdate(); }
        RenderDeviceCards(devices);
    }

    private void RenderDeviceCards(IReadOnlyList<ManagedDeviceDto> devices)
    {
        if (_deviceCards is null || _deviceDetails is null) return;
        _deviceCards.SuspendLayout();
        try
        {
            _deviceCards.Controls.Clear();
            _deviceCards.RowStyles.Clear();
            _deviceCards.RowCount = 0;
            if (devices.Count == 0)
            {
                var empty = new EmptyStatePanel { Dock = DockStyle.Fill, Glyph = IconGlyph.Devices, Title = "尚未配对设备", Description = "前往“公网配对”扫描二维码，配对成功后设备会显示在这里。", MinimumSize = new Size(0, 176) };
                _deviceCards.RowCount = 1; _deviceCards.RowStyles.Add(new RowStyle(SizeType.Absolute, 176)); _deviceCards.Controls.Add(empty, 0, 0);
                _selectedDevice = null;
            }
            else
            {
                _selectedDevice = devices.FirstOrDefault(device => device.DeviceId == _selectedDevice?.DeviceId) ?? devices[0];
                foreach (var device in devices)
                {
                    var card = CreateDeviceCard(device);
                    var row = _deviceCards.RowCount++;
                    _deviceCards.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
                    _deviceCards.Controls.Add(card, 0, row);
                }
            }
            RenderDeviceDetails(_selectedDevice);
        }
        finally { _deviceCards.ResumeLayout(true); }
    }

    private Control CreateDeviceCard(ManagedDeviceDto device)
    {
        var card = new ModernCard { Dock = DockStyle.Top, Height = 72, Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(10), Margin = new Padding(0, 0, 0, 6) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.Transparent };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        var icon = new RoundedPanel { Dock = DockStyle.Fill, CornerRadius = 10, BorderWidth = 0, BackColor = UiTheme.AccentSoft, Margin = new Padding(0, 0, 12, 0) };
        icon.Controls.Add(new Label { Text = device.DisplayName.Contains("电脑", StringComparison.OrdinalIgnoreCase) || device.DisplayName.Contains("Mac", StringComparison.OrdinalIgnoreCase) ? IconGlyph.Laptop : IconGlyph.Phone, Dock = DockStyle.Fill, ForeColor = UiTheme.Accent, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe MDL2 Assets", 18, FontStyle.Regular) });
        var copy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        copy.Controls.Add(new Label { Text = device.DisplayName, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = UiTheme.Text, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
        copy.Controls.Add(new Label { Text = "在线", Dock = DockStyle.Fill, Font = UiTheme.SmallFont, ForeColor = UiTheme.Success, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        copy.Controls.Add(new Label { Text = $"{(device.CanSend ? "Pro 可发送" : "基础只读")} · 最近在线 {device.LastSeenAt.ToLocalTime():MM-dd HH:mm}", Dock = DockStyle.Fill, Font = UiTheme.SmallFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.TopLeft }, 0, 2);
        var revoke = DangerButton("撤销"); revoke.Dock = DockStyle.None; revoke.Anchor = AnchorStyles.Right; revoke.Margin = new Padding(8, 20, 0, 0); revoke.MinimumSize = new Size(64, 30); revoke.Width = 64; revoke.Height = 30; revoke.Padding = Padding.Empty; revoke.Click += async (_, _) => await RevokeDeviceAsync(device);
        layout.Controls.Add(icon, 0, 0); layout.Controls.Add(copy, 1, 0); layout.Controls.Add(revoke, 2, 0); card.Controls.Add(layout);
        card.Click += (_, _) => { _selectedDevice = device; RenderDeviceDetails(device); };
        return card;
    }

    private void RenderDeviceDetails(ManagedDeviceDto? device)
    {
        if (_deviceDetails is null) return;
        _deviceDetails.Controls.Clear(); _deviceDetails.RowStyles.Clear(); _deviceDetails.RowCount = 0;
        if (device is null)
        {
            _deviceDetails.RowCount = 1;
            _deviceDetails.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var empty = new EmptyStatePanel { Dock = DockStyle.Fill, Glyph = IconGlyph.Devices, Title = "设备详情", Description = "配对设备后显示连接、权限和实时状态。", MinimumSize = new Size(0, 220) };
            _deviceDetails.Controls.Add(empty, 0, 0);
            _deviceDetails.SetColumnSpan(empty, 2);
            return;
        }

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var icon = new RoundedPanel { Dock = DockStyle.Fill, CornerRadius = 10, BorderWidth = 0, BackColor = UiTheme.AccentSoft, Margin = new Padding(0, 0, 10, 0) };
        icon.Controls.Add(new Label { Text = device.DisplayName.Contains("Mac", StringComparison.OrdinalIgnoreCase) ? IconGlyph.Laptop : IconGlyph.Phone, Dock = DockStyle.Fill, Font = new Font("Segoe MDL2 Assets", 18), ForeColor = UiTheme.Accent, TextAlign = ContentAlignment.MiddleCenter });
        var name = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        name.Controls.Add(new Label { Text = device.DisplayName, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = UiTheme.Text, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true }, 0, 0);
        name.Controls.Add(new Label { Text = $"公网连接 · {device.LastSeenAt.ToLocalTime():yyyy-MM-dd HH:mm}", Dock = DockStyle.Fill, Font = UiTheme.SmallFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true }, 0, 1);
        var revoke = DangerButton("撤销"); revoke.AutoSize = false; revoke.Width = 78; revoke.Height = 34; revoke.Anchor = AnchorStyles.Right; revoke.Margin = Padding.Empty; revoke.Padding = new Padding(8, 0, 8, 0); revoke.Image = IconGlyph.Render(IconGlyph.Trash, 13, UiTheme.Danger); revoke.ImageAlign = ContentAlignment.MiddleLeft; revoke.TextImageRelation = TextImageRelation.ImageBeforeText; revoke.Click += async (_, _) => await RevokeDeviceAsync(device);
        header.Controls.Add(icon, 0, 0); header.Controls.Add(name, 1, 0); header.Controls.Add(revoke, 2, 0);
        var badges = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 0), Padding = new Padding(62, 0, 0, 0) };
        badges.Controls.Add(new PillLabel { Text = device.CanSend ? "Pro 权限" : "基础只读", Tone = device.CanSend ? PillLabel.PillTone.Pro : PillLabel.PillTone.Brand, Height = 24, Margin = new Padding(0, 0, 6, 0) });
        badges.Controls.Add(new PillLabel { Text = "在线", Tone = PillLabel.PillTone.Success, Height = 24, Margin = Padding.Empty });
        header.Controls.Add(badges, 0, 1); header.SetColumnSpan(badges, 3);
        _deviceDetails.RowCount = 1;
        _deviceDetails.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        _deviceDetails.Controls.Add(header, 0, 0); _deviceDetails.SetColumnSpan(header, 2);

        AddDeviceSectionHeader("设备信息");
        AddDeviceDetailRow("设备 ID", device.DeviceId);
        AddDeviceDetailRow("配对时间", device.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        AddDeviceDetailRow("最近使用", device.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        AddDeviceDetailRow("当前权限", device.CanSend ? "Pro · 可发送指令" : "免费 · 只读");
        AddDeviceDetailRow("连接方式", "公网 · 端到端加密");
        AddDeviceSectionHeader("连接状态");
        AddDeviceDetailRow("实时状态", "在线 · 等待远程请求");
        AddDeviceDetailRow("安全策略", "端到端加密 · 本机确认");
    }

    private void AddDeviceSectionHeader(string title)
    {
        if (_deviceDetails is null) return;
        var row = _deviceDetails.RowCount++;
        _deviceDetails.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var header = new Label { Text = title, Dock = DockStyle.Fill, ForeColor = UiTheme.Text, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4) };
        _deviceDetails.Controls.Add(header, 0, row); _deviceDetails.SetColumnSpan(header, 2);
    }

    private void AddDeviceDetailRow(string label, string value)
    {
        if (_deviceDetails is null) return;
        var row = _deviceDetails.RowCount++;
        _deviceDetails.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _deviceDetails.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Font = UiTheme.SmallFont, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        _deviceDetails.Controls.Add(new Label { Text = value, Dock = DockStyle.Fill, ForeColor = UiTheme.TextSecondary, Font = UiTheme.SmallFont, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true }, 1, row);
    }

    private async Task RevokeDeviceAsync(ManagedDeviceDto device)
    {
        if (!_dialogs.Confirm(this, $"撤销“{device.DisplayName}”后，该设备需要重新扫码配对。", "撤销设备")) return;
        var service = _host.Services?.GetService<DeviceManagementService>();
        if (service is null) return;
        await RunUiActionAsync(() => { service.Revoke(device.Transport, device.DeviceId); return Task.CompletedTask; });
        _devicesSignature = "";
    }

    private Control BuildPairingPage()
    {
        var panel = PageStack();
        panel.Controls.Add(TitleBlock("公网安全配对", "使用手机扫描下方二维码，或在手机上打开配对链接。配对码 5 分钟内有效。"));
        var body = new TableLayoutPanel { Dock = DockStyle.Top, Height = 376, MinimumSize = new Size(0, 376), ColumnCount = 2, Margin = new Padding(0, 8, 0, 0), Padding = Padding.Empty, BackColor = UiTheme.Canvas };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Canvas, Margin = Padding.Empty };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 220)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var qrCard = new ModernCard { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, BorderColor = UiTheme.Border, Radius = 8, Padding = new Padding(20), Margin = Padding.Empty };
        _pairingQr = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = Padding.Empty,
            AccessibleName = "公网配对二维码",
        };
        qrCard.Controls.Add(_pairingQr);
        _pairingDisplayCode = new Label { Text = "PAIR-准备中", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiTheme.Muted, Font = UiTheme.MonoFont, AutoEllipsis = true };
        var pairActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Canvas, Padding = new Padding(0, 5, 0, 0), Margin = Padding.Empty };
        var regenerate = SecondaryButton("重新生成");
        regenerate.AutoSize = false;
        regenerate.MinimumSize = Size.Empty;
        regenerate.Size = new Size(112, 38);
        regenerate.Margin = new Padding(0, 0, 8, 0);
        regenerate.Click += (_, _) => _ = RefreshActivePageAsync(force: true);
        var cancel = DangerButton("取消配对");
        cancel.AutoSize = false;
        cancel.MinimumSize = Size.Empty;
        cancel.Size = new Size(112, 38);
        cancel.Margin = Padding.Empty;
        cancel.Click += (_, _) => Navigate("overview");
        pairActions.Controls.Add(regenerate); pairActions.Controls.Add(cancel);
        left.Controls.Add(qrCard, 0, 0); left.Controls.Add(_pairingDisplayCode, 0, 1); left.Controls.Add(pairActions, 0, 2);

        var rightStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Canvas, Margin = new Padding(14, 0, 0, 0) };
        rightStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        rightStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        rightStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var statusCard = new ModernCard { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, BorderColor = UiTheme.Border, Radius = 8, Padding = new Padding(16, 10, 16, 10), Margin = Padding.Empty };
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var statusHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent };
        statusHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); statusHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        _pairingState = new Label
        {
            Text = "●  正在连接公网服务",
            Dock = DockStyle.Fill,
            Font = UiTheme.LabelFont,
            ForeColor = UiTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _pairingServiceBadge = new Label { Text = "● 公网连接中", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = UiTheme.Warning, Font = UiTheme.SmallFont };
        statusHeader.Controls.Add(_pairingState, 0, 0); statusHeader.Controls.Add(_pairingServiceBadge, 1, 0);
        statusLayout.Controls.Add(new Label { Text = "配对状态    等待手机扫码", Dock = DockStyle.Fill, ForeColor = UiTheme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        statusLayout.Controls.Add(new Label { Text = "网络          公网服务正常\r\n加密          E2E · X25519", Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        _pairingExpiry = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 10, 0, 0),
            AccessibleName = "二维码有效期",
        };
        statusLayout.Controls.Add(statusHeader, 0, 0); statusCard.Controls.Add(statusLayout);

        var countdownCard = new ModernCard { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, BorderColor = UiTheme.Border, Radius = 8, Padding = new Padding(16, 10, 16, 8), Margin = new Padding(0, 10, 0, 0) };
        var countdownLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        countdownLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); countdownLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        countdownLayout.Controls.Add(new Label { Text = "倒计时", Dock = DockStyle.Fill, ForeColor = UiTheme.Text, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        countdownLayout.Controls.Add(_pairingExpiry, 0, 1);
        countdownCard.Controls.Add(countdownLayout);
        var linkCard = new ModernCard { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, BorderColor = UiTheme.Border, Radius = 8, Padding = new Padding(16, 10, 16, 8), Margin = new Padding(0, 10, 0, 0) };
        var linkCardLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        linkCardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); linkCardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        linkCardLayout.Controls.Add(new Label { Text = "配对链接", Dock = DockStyle.Fill, ForeColor = UiTheme.Text, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var linkLayout = new TableLayoutPanel { Dock = DockStyle.Top, Height = 38, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        linkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); linkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _pairingLinkPreview = new Label { Text = "codex-remote://pair/等待生成", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.TextSecondary, Font = UiTheme.SmallFont, AutoEllipsis = true, BackColor = UiTheme.SurfaceMuted, Padding = new Padding(8, 0, 4, 0) };
        _pairingCopyButton = SecondaryButton("复制"); _pairingCopyButton.Dock = DockStyle.Fill; _pairingCopyButton.MinimumSize = new Size(62, 34); _pairingCopyButton.Margin = new Padding(6, 0, 0, 0); _pairingCopyButton.Padding = Padding.Empty; _pairingCopyButton.Click += CopyPairingLinkClick;
        linkLayout.Controls.Add(_pairingLinkPreview, 0, 0); linkLayout.Controls.Add(_pairingCopyButton, 1, 0); linkCardLayout.Controls.Add(linkLayout, 0, 1); linkCard.Controls.Add(linkCardLayout);
        rightStack.Controls.Add(statusCard, 0, 0); rightStack.Controls.Add(countdownCard, 0, 1); rightStack.Controls.Add(linkCard, 0, 2);
        body.Controls.Add(left, 0, 0); body.Controls.Add(rightStack, 1, 0); panel.Controls.Add(body);
        panel.Controls.Add(InfoCard("安全说明", "配对链接通过端到端加密传输，并包含设备指纹校验。手机扫码后请在手机上确认配对请求。", UiTheme.Accent));
        return panel;
    }

    private void UpdatePairingPage()
    {
        if (_pairingQr is null || _pairingState is null || _pairingExpiry is null) return;
        var remote = _host.Services?.GetService<RemoteAccessHostedService>()?.CurrentPairing;
        if (remote is null)
        {
            ReplacePairingImage(null, null);
            _pairingState.Text = _host.State == HostRuntimeState.Running ? "正在连接公网服务" : "请先启动 Host";
            _pairingState.ForeColor = UiTheme.Warning;
            _pairingExpiry.Text = "二维码准备完成后会自动显示";
            _pairingExpiry.ForeColor = UiTheme.Muted;
            if (_pairingDisplayCode is not null) _pairingDisplayCode.Text = "PAIR-准备中";
            if (_pairingLinkPreview is not null) _pairingLinkPreview.Text = "LINK  codex-remote://pair/等待生成";
            if (_pairingServiceBadge is not null) { _pairingServiceBadge.Text = "● 公网连接中"; _pairingServiceBadge.ForeColor = UiTheme.Warning; }
            if (_pairingCopyButton is not null) { _pairingCopyButton.Enabled = true; _pairingCopyButton.Text = "复制"; }
            return;
        }

        ReplacePairingImage(remote.Url, remote.Url);
        var displayCode = PairingDisplayCode(remote.Url);
        if (_pairingDisplayCode is not null) _pairingDisplayCode.Text = displayCode;
        if (_pairingLinkPreview is not null) _pairingLinkPreview.Text = $"LINK  codex-remote://pair/{displayCode[5..]}";
        if (_pairingServiceBadge is not null) { _pairingServiceBadge.Text = "● 公网正常"; _pairingServiceBadge.ForeColor = UiTheme.Success; }
        if (_pairingCopyButton is not null) _pairingCopyButton.Enabled = true;
        var expiry = FormatPairingExpiry(remote.ExpiresAt, DateTimeOffset.UtcNow);
        if (expiry.Expired)
        {
            _pairingState.Text = "正在更新二维码";
            _pairingState.ForeColor = UiTheme.Warning;
            _pairingExpiry.Text = expiry.Text;
            _pairingExpiry.ForeColor = UiTheme.Warning;
            return;
        }
        _pairingState.Text = "●  等待手机扫码";
        _pairingState.ForeColor = UiTheme.Success;
        _pairingExpiry.Text = $"◷  {expiry.Text} · 最长 5 分钟";
        _pairingExpiry.ForeColor = expiry.Warning ? UiTheme.Warning : UiTheme.Muted;
    }

    private void CopyPairingLinkClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pairingUrl)) return;
        try
        {
            Clipboard.SetText(_pairingUrl);
            if (_pairingCopyButton is not null) _pairingCopyButton.Text = "已复制";
        }
        catch
        {
            _dialogs.ShowError(this, "无法写入剪贴板，请稍后重试。");
        }
    }

    private static string PairingDisplayCode(string value)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return $"PAIR-{digest[..4]}-{digest[4..8]}";
    }

    private void ReplacePairingImage(string? url, string? value)
    {
        if (_pairingQr is null || string.Equals(_pairingUrl, url, StringComparison.Ordinal)) return;
        var previous = _pairingQr.Image;
        _pairingQr.Image = value is null ? null : CreateQrImage(value);
        _pairingUrl = url;
        previous?.Dispose();
    }


    private Control BuildDiagnosticsPage()
    {
        var panel = PageStack();
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 44, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8), Padding = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        heading.Controls.Add(TitleBlock("诊断", "各模块运行状态和最近错误码"), 0, 0);
        var retry = SecondaryButton("重新检测"); retry.Anchor = AnchorStyles.Right; retry.MinimumSize = new Size(112, 38); retry.Height = 38;
        retry.Click += async (_, _) => { _diagnosticsSignature = ""; await RefreshActivePageAsync(force: true); };
        heading.Controls.Add(retry, 1, 0); panel.Controls.Add(heading);
        // PageStack lays out children vertically, so a Fill-docked TabControl must
        // reserve an explicit height before the stack normalizes child docking.
        _diagnosticTabs = new ModernTabControl { Dock = DockStyle.Top, Height = 430, MinimumSize = new Size(0, 430), Margin = new Padding(0, 4, 0, 0), BackColor = UiTheme.Surface };
        var modules = new TabPage("模块状态") { BackColor = UiTheme.Surface, Padding = new Padding(20, 16, 20, 16) };
        _diagnosticsList = CreateListView(modules, ["组件", "状态", "问题", "错误码", "建议操作", "更新时间"], [16, 10, 21, 18, 22, 13]);
        _diagnosticsList.Dock = DockStyle.Fill;
        // The handoff design uses a compact diagnostic table. Keep the ListView
        // as the visible surface; the legacy card renderer is retained only as a
        // data-independent fallback and must not cover the table.
        _diagnosticsList.Visible = true;
        _diagnosticsCards = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 390, MinimumSize = new Size(0, 390), ColumnCount = 1, BackColor = UiTheme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        _diagnosticsCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _diagnosticsCards.SizeChanged += (_, _) => ResizeDiagnosticCards();
        _diagnosticsCards.Visible = false;
        modules.Controls.Add(_diagnosticsCards);
        modules.Resize += (_, _) =>
        {
            if (_diagnosticsCards is null) return;
            _diagnosticsCards.Width = Math.Max(0, modules.ClientSize.Width - modules.Padding.Horizontal);
            _diagnosticsCards.Invalidate();
        };
        var errors = new TabPage("错误日志") { BackColor = UiTheme.Surface, Padding = new Padding(0, 10, 0, 0) };
        errors.Controls.Add(InfoCard("最近错误", "当前没有需要处理的错误。重新检测会刷新所有模块状态。", UiTheme.Success));
        var performance = new TabPage("性能指标") { BackColor = UiTheme.Surface, Padding = new Padding(0, 10, 0, 0) };
        performance.Controls.Add(InfoCard("运行指标", "Host、Signal 和 Sidecar 的实时指标将在连接后显示。", UiTheme.Muted));
        var network = new TabPage("网络诊断") { BackColor = UiTheme.Surface, Padding = new Padding(0, 10, 0, 0) };
        network.Controls.Add(InfoCard("网络连通性", "重新检测会检查公网配对服务、Signal 和 TURN 中继。", UiTheme.Muted));
        var settings = new TabPage("日志设置") { BackColor = UiTheme.Surface, Padding = new Padding(0, 10, 0, 0) };
        settings.Controls.Add(InfoCard("日志保留", "日志仅保存在本机，且不会包含项目路径、会话内容或密钥。", UiTheme.Muted));
        _diagnosticTabs.TabPages.AddRange([modules, errors, performance, network, settings]);
        panel.Controls.Add(_diagnosticTabs);
        panel.Controls.Add(InfoCard("隐私", "诊断不会显示项目路径、会话内容、服务器地址、密钥或配对信息。", UiTheme.Muted));
        return panel;
    }


    private void UpdateDiagnosticsPage()
    {
        if (_diagnosticsList is null) return;
        var snapshot = _host.Services?.GetService<BridgeDiagnosticsService>()?.Capture();
        var now = DateTimeOffset.UtcNow;
        var rows = snapshot is null
            ? new[]
            {
                DiagnosticRow("Host", new BridgeComponentHealth(
                    _host.State == HostRuntimeState.Faulted ? BridgeComponentState.Offline : BridgeComponentState.Disabled,
                    now,
                    _host.State == HostRuntimeState.Faulted ? "host_start_failed" : null)),
            }
            : new[]
            {
                DiagnosticRow("Host", snapshot.Host),
                DiagnosticRow("Signal", snapshot.Signal),
                DiagnosticRow("TURN", snapshot.Turn),
                DiagnosticRow("Sidecar", snapshot.Sidecar),
                DiagnosticRow("Pairing", snapshot.Pairing),
                DiagnosticRow("Codex Desktop", snapshot.Desktop),
            };
        var signature = string.Join('\n', rows.Select(row => string.Join('|', row.Cells)));
        // 即使状态签名未变化，也要确保首次创建或切换回页面时卡片已经填充。
        if (signature == _diagnosticsSignature && _diagnosticsCards is not null && _diagnosticsCards.Controls.Count > 0) return;
        _diagnosticsSignature = signature;
        _diagnosticsList.BeginUpdate();
        try
        {
            _diagnosticsList.Items.Clear();
            foreach (var row in rows)
            {
                var item = new ListViewItem(row.Cells[0]) { UseItemStyleForSubItems = false };
                for (var index = 1; index < row.Cells.Length; index++) item.SubItems.Add(row.Cells[index]);
                item.SubItems[1].ForeColor = row.StatusColor;
                _diagnosticsList.Items.Add(item);
            }
        }
        finally { _diagnosticsList.EndUpdate(); }
        RenderDiagnosticCards(rows);
    }

    private void RenderDiagnosticCards(IReadOnlyList<DiagnosticUiRow> rows)
    {
        if (_diagnosticsCards is null) return;
        _diagnosticsCards.SuspendLayout();
        try
        {
            _diagnosticsCards.Controls.Clear();
            _diagnosticsCards.RowStyles.Clear();
            _diagnosticsCards.RowCount = 0;
            foreach (var row in rows)
            {
                var card = new ModernCard { Dock = DockStyle.Top, Height = 52, Width = Math.Max(0, _diagnosticsCards.ClientSize.Width), Radius = 8, BorderColor = UiTheme.Border, BackColor = UiTheme.Surface, Padding = new Padding(12, 6, 12, 5), Margin = new Padding(0, 0, 0, 5) };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
                var healthy = row.StatusColor == UiTheme.Success;
                var icon = new Label { Text = healthy ? "✓" : "!", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = row.StatusColor, BackColor = healthy ? UiTheme.SuccessTint : UiTheme.WarningTint, Font = new Font("Segoe UI", 12, FontStyle.Bold), Margin = new Padding(0, 0, 12, 0) };
                var copy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
                copy.Controls.Add(new Label { Text = row.Cells[0], Dock = DockStyle.Fill, Font = UiTheme.LabelFont, ForeColor = UiTheme.Text, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
                copy.Controls.Add(new Label { Text = row.Cells[2], Dock = DockStyle.Fill, Font = UiTheme.SmallFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true }, 0, 1);
                var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
                status.Controls.Add(new Label { Text = "●  " + row.Cells[1], Dock = DockStyle.Fill, Font = UiTheme.LabelFont, ForeColor = row.StatusColor, TextAlign = ContentAlignment.BottomRight }, 0, 0);
                status.Controls.Add(new Label { Text = row.Cells[5], Dock = DockStyle.Fill, Font = UiTheme.SmallFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.TopRight }, 0, 1);
                layout.Controls.Add(icon, 0, 0); layout.Controls.Add(copy, 1, 0); layout.Controls.Add(status, 2, 0); card.Controls.Add(layout);
                var index = _diagnosticsCards.RowCount++;
                _diagnosticsCards.RowStyles.Add(new RowStyle(SizeType.Absolute, 57));
                _diagnosticsCards.Controls.Add(card, 0, index);
            }
        }
        finally
        {
            _diagnosticsCards.ResumeLayout(true);
            ResizeDiagnosticCards();
        }
    }

    private void ResizeDiagnosticCards()
    {
        if (_diagnosticsCards is null) return;
        var width = Math.Max(0, _diagnosticsCards.ClientSize.Width);
        foreach (Control control in _diagnosticsCards.Controls)
            control.Width = width;
    }

    private static DiagnosticUiRow DiagnosticRow(string name, BridgeComponentHealth health)
    {
        var presentation = DiagnosticPresentation.From(name, health);
        return new DiagnosticUiRow(
            [
                name,
                presentation.Status,
                presentation.Problem,
                presentation.ErrorCode,
                presentation.Action,
                health.ChangedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),
            ],
            presentation.StatusColor);
    }

    internal static PairingExpiryPresentation FormatPairingExpiry(
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        var totalSeconds = Math.Max(0, (int)Math.Ceiling((expiresAt - now).TotalSeconds));
        return new PairingExpiryPresentation(
            $"有效期 {totalSeconds / 60:00}:{totalSeconds % 60:00}",
            Warning: totalSeconds is > 0 and <= 60,
            Expired: totalSeconds == 0);
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        Enabled = false;
        try
        {
            await action();
            await RefreshActivePageAsync(force: true);
        }
        catch (Exception exception) { _dialogs.ShowError(this, exception.Message); }
        finally { Enabled = true; }
    }

    private void HostStateChanged(object? sender, EventArgs eventArgs)
    {
        if (IsHandleCreated) BeginInvoke(async () => await RefreshActivePageAsync(force: true));
    }

    private static Panel PageStack()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiTheme.Canvas,
            Padding = Padding.Empty,
        };
        panel.HorizontalScroll.Enabled = false;
        panel.HorizontalScroll.Visible = false;
        var layingOut = false;
        void ConstrainPageWidth()
        {
            if (layingOut) return;
            layingOut = true;
            // Reserve the vertical scrollbar gutter up front. Its visibility can
            // change during the same layout pass, otherwise children briefly use
            // the wider pre-scrollbar viewport and create horizontal overflow.
            var scrollbar = SystemInformation.VerticalScrollBarWidth;
            // Leave a small safety gutter so child margins/borders never create
            // a horizontal virtual extent at high DPI.
            var width = Math.Max(0, panel.ClientSize.Width - panel.Padding.Horizontal - scrollbar - 4);
            var y = panel.Padding.Top;
            // Keep the declaration order: title, content, actions, notes.
            foreach (var child in panel.Controls.Cast<Control>())
            {
                child.Dock = DockStyle.None;
                child.Left = panel.Padding.Left;
                child.Top = y + child.Margin.Top;
                child.Width = width;
                if (child.Height <= 0)
                    child.Height = child.GetPreferredSize(new Size(width, 0)).Height;
                y = child.Bottom + child.Margin.Bottom;
            }
            panel.AutoScrollMinSize = new Size(0, y + panel.Padding.Bottom);
            panel.HorizontalScroll.Enabled = false;
            panel.HorizontalScroll.Visible = false;
            panel.HorizontalScroll.Maximum = 0;
            layingOut = false;
        }
        panel.Resize += (_, _) => ConstrainPageWidth();
        panel.Layout += (_, _) => ConstrainPageWidth();
        return panel;
    }

    private static Label SectionNote(string text) => new()
    {
        Text = text,
        ForeColor = UiTheme.Muted,
        AutoSize = false,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = false,
        Margin = new Padding(0, 8, 0, 0),
        Padding = Padding.Empty,
    };

    private static Control TitleBlock(string title, string subtitle)
    {
        var titleFont = new Font("Segoe UI", 13f, FontStyle.Bold);
        var subtitleFont = UiTheme.SmallFont;
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = titleFont,
            ForeColor = UiTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };
        var subtitleLabel = new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = UiTheme.Muted,
            Font = subtitleFont,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty,
        };
        // 按当前 DPI 的实际字体高度分配行高，避免高缩放下标题被垂直裁切。
        var titleHeight = Math.Max(24, titleLabel.GetPreferredSize(Size.Empty).Height + 2);
        var subtitleHeight = Math.Max(18, subtitleLabel.GetPreferredSize(Size.Empty).Height + 1);
        var block = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = titleHeight + subtitleHeight,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 2),
            Padding = Padding.Empty,
        };
        block.RowStyles.Add(new RowStyle(SizeType.Absolute, titleHeight));
        block.RowStyles.Add(new RowStyle(SizeType.Absolute, subtitleHeight));
        block.Controls.Add(titleLabel, 0, 0);
        block.Controls.Add(subtitleLabel, 0, 1);
        return block;
    }

    private static Control InfoCard(string title, string text, Color accent)
    {
        var tint = accent == UiTheme.Warning ? UiTheme.WarningTint
            : accent == UiTheme.Success ? UiTheme.SuccessTint
            : accent == UiTheme.Accent ? UiTheme.InfoTint : UiTheme.Surface;
        var card = new ModernCard { Dock = DockStyle.Top, AutoSize = false, Height = 66, MinimumSize = new Size(0, 66), BackColor = tint, Radius = 8, Padding = new Padding(14, 7, 12, 7), Margin = new Padding(0, 6, 0, 0) };
        var copy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(14, 0, 0, 0) };
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        copy.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = UiTheme.Text, Font = UiTheme.LabelFont, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        copy.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, ForeColor = UiTheme.TextSecondary, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        card.Controls.Add(copy);
        return card;
    }

    private static Label ValueLabel() => new()
    {
        ForeColor = UiTheme.Text,
        Font = UiTheme.LabelFont,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static void AddStatusRow(TableLayoutPanel table, string name, Label value)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        table.Controls.Add(new Label
        {
            Text = name,
            ForeColor = UiTheme.Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        table.Controls.Add(value, 1, row);
    }

    private static void SetValue(Label label, string value, bool healthy)
    {
        label.Text = value;
        label.ForeColor = healthy ? UiTheme.Success : UiTheme.Text;
    }

    private static ListView CreateListView(Control container, string[] columns, int[] percentages)
    {
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            GridLines = false,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            Font = new Font("Segoe UI", 10f),
            OwnerDraw = true,
            AccessibleName = "数据列表",
            Width = Math.Max(420, container.ClientSize.Width - SystemInformation.VerticalScrollBarWidth),
        };
        foreach (var column in columns) list.Columns.Add(column);
        list.SmallImageList = new ImageList
        {
            ImageSize = new Size(1, 48),
            ColorDepth = ColorDepth.Depth32Bit,
        };
        list.DrawColumnHeader += (_, e) =>
        {
            using var background = new SolidBrush(UiTheme.SurfaceMuted);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", UiTheme.LabelFont,
                new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height),
                UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var line = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        list.DrawItem += (_, e) =>
        {
            if (e.Item is null) return;
            var color = e.Item.Selected ? UiTheme.AccentSoft : UiTheme.Surface;
            using var background = new SolidBrush(color);
            e.Graphics.FillRectangle(background, e.Bounds);
        };
        list.DrawSubItem += (_, e) =>
        {
            if (e.Item is null || e.SubItem is null) return;
            var bounds = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
            if (e.ColumnIndex == 0 && list.CheckBoxes)
            {
                var box = new Rectangle(bounds.X, bounds.Y + (bounds.Height - 16) / 2, 16, 16);
                ControlPaint.DrawCheckBox(e.Graphics, box, e.Item.Checked ? ButtonState.Checked : ButtonState.Normal);
                bounds.X += 24; bounds.Width = Math.Max(0, bounds.Width - 24);
            }
            var color = e.ColumnIndex == 1 && list.Columns.Count >= 5 && e.Item.SubItems.Count > 1
                ? e.Item.SubItems[e.ColumnIndex].ForeColor : UiTheme.TextSecondary;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, bounds, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var line = new Pen(Color.FromArgb(238, 238, 242));
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        void ResizeColumns()
        {
            list.Width = Math.Max(420, container.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
            var available = Math.Max(1, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            for (var index = 0; index < list.Columns.Count; index++)
                list.Columns[index].Width = Math.Max(72, available * percentages[index] / 100);
        }
        container.ClientSizeChanged += (_, _) => ResizeColumns();
        list.HandleCreated += (_, _) => ResizeColumns();
        return list;
    }

    private static Bitmap CreateQrImage(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        using var stream = new MemoryStream(code.GetGraphic(8, drawQuietZones: true));
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }

    private static FlowLayoutPanel ActionRow() => new()
    {
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        Margin = new Padding(0, 16, 0, 0),
    };

    private static Button PrimaryButton(string text) => StyledButton(text, UiTheme.Accent, Color.White);
    private static Button SecondaryButton(string text) => StyledButton(text, UiTheme.Surface, UiTheme.Text, UiTheme.Border);
    private static Button DangerButton(string text) => StyledButton(text, UiTheme.Surface, UiTheme.Danger, UiTheme.Danger);

    private static Button StyledButton(string text, Color background, Color foreground, Color? border = null)
    {
        var button = new ModernButton
        {
            Text = text,
            AutoSize = true,
            BackColor = background,
            ForeColor = foreground,
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(12, 4, 12, 4),
            AccessibleName = text,
        };
        button.ButtonVariant = foreground == Color.White ? ModernButton.Variant.Primary
            : foreground == UiTheme.Danger ? ModernButton.Variant.Danger
            : ModernButton.Variant.Default;
        return button;
    }

    private static string HostStateText(HostRuntimeState state) => state switch
    {
        HostRuntimeState.Stopped => "已停止",
        HostRuntimeState.Starting => "正在启动",
        HostRuntimeState.Running => "运行中",
        HostRuntimeState.Stopping => "正在停止",
        HostRuntimeState.Faulted => "需要处理",
        _ => "未知",
    };

    private static string StateText(BridgeComponentState state) => state switch
    {
        BridgeComponentState.Online => "正常",
        BridgeComponentState.Starting => "连接中",
        BridgeComponentState.Disabled => "未启用",
        BridgeComponentState.Degraded => "性能下降",
        BridgeComponentState.CircuitOpen => "已暂停",
        _ => "异常",
    };

    private static string FriendlyDiscoveryError(Exception exception) => exception switch
    {
        FileNotFoundException => "未找到 Codex 会话索引，请先启动 Codex Desktop。",
        Microsoft.Data.Sqlite.SqliteException => "Codex 会话索引暂时不可用，请稍后重新打开此页面。",
        _ => "读取失败，请确认 Codex Desktop 已正常运行。",
    };

    internal sealed record PairingExpiryPresentation(string Text, bool Warning, bool Expired);
    private sealed record DiagnosticUiRow(string[] Cells, Color StatusColor);
}
