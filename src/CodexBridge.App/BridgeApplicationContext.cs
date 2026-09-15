using CodexBridge.Host;
using CodexRemote.UI;

namespace CodexBridge.App;

public sealed class BridgeApplicationContext : ApplicationContext
{
    private static void BootLog(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "codexbridge-live.log");
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
    private readonly HostController _host;
    private readonly MainForm _mainForm;
    private readonly NotifyIcon _trayIcon;
    private readonly EventWaitHandle _installShutdownEvent;
    private readonly System.Windows.Forms.Timer _installShutdownTimer;
    private bool _exiting;
    private readonly ToolStripMenuItem _toggleHostMenuItem;
    private Label? _trayHeaderState;
    private Bitmap? _trayBitmap;
    private Icon? _trayIconResource;

    public BridgeApplicationContext(bool startInBackground)
    {
        BootLog($"context.begin background={startInBackground}");
        var options = HostOptions.CreateDefault();
        _host = new HostController(options);
        BootLog("context.host-created");
        var autostart = new AutostartManager(Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。"));
        _mainForm = new MainForm(_host, autostart);
        BootLog("context.form-created");
        MainForm = _mainForm;
        _mainForm.FormClosed += (_, _) => ExitThread();

        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.Professional,
            AutoSize = false,
            Width = 240,
            ShowImageMargin = true,
            ShowCheckMargin = false,
            ShowItemToolTips = true,
            Font = new Font("Segoe UI", 9f),
            Padding = new Padding(2),
        };
        menu.ItemAdded += (_, args) =>
        {
            if (args.Item is ToolStripMenuItem item)
            {
                item.AutoSize = false;
                item.Height = 30;
                item.Padding = new Padding(12, 0, 12, 0);
                item.Margin = new Padding(0);
                item.AutoSize = false;
                item.Width = 236;
            }
        };
        // 使用真正的两行品牌头部，避免 ToolStripMenuItem 对换行文本进行省略，
        // 也避免托盘菜单顶部出现一条空白黑色竖条。
        var headerPanel = new Panel { Width = 232, Height = 46, BackColor = UiTheme.Surface, Padding = new Padding(10, 6, 8, 6) };
        var headerIcon = new RoundedPanel { Width = 30, Height = 30, CornerRadius = 7, BorderWidth = 0, BackColor = UiTheme.Accent, Dock = DockStyle.Left };
        headerIcon.Controls.Add(new Label { Text = ">_", Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Cascadia Mono", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
        var headerCopy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.Surface, Padding = new Padding(10, 0, 0, 0) };
        headerCopy.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
        headerCopy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headerCopy.Controls.Add(new Label { Text = "Codex Remote", Dock = DockStyle.Fill, ForeColor = UiTheme.Text, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _trayHeaderState = new Label { Text = "Host 运行中", Dock = DockStyle.Fill, ForeColor = UiTheme.Success, Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleLeft };
        headerCopy.Controls.Add(_trayHeaderState, 0, 1);
        headerPanel.Controls.Add(headerCopy);
        headerPanel.Controls.Add(headerIcon);
        menu.Items.Add(new ToolStripControlHost(headerPanel) { AutoSize = false, Width = 236, Height = 48, Margin = Padding.Empty, Padding = Padding.Empty });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayItem("打开管理工具", IconGlyph.Home, Keys.Enter, (_, _) => _mainForm.ShowFromTray()));
        menu.Items.Add(TrayItem("显示配对二维码", IconGlyph.Pair, Keys.Control | Keys.P, (_, _) => _mainForm.ShowPairingFromTray()));
        menu.Items.Add(TrayItem("诊断", IconGlyph.Diagnostic, Keys.None, (_, _) => _mainForm.ShowDiagnosticsFromTray()));
        _toggleHostMenuItem = TrayItem("暂停 Host", IconGlyph.Pause, Keys.None, async (_, _) => await _mainForm.ToggleHostFromTrayAsync());
        menu.Items.Add(_toggleHostMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayItem("退出程序", IconGlyph.Close, Keys.Control | Keys.Q, async (_, _) => await ExitApplicationAsync(), UiTheme.Danger));
        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(HostRuntimeState.Running),
            Text = "Codex Bridge",
            Visible = true,
            ContextMenuStrip = menu,
        };
        BootLog("context.tray-created");
        _trayIcon.DoubleClick += (_, _) => _mainForm.ShowFromTray();
        _host.StateChanged += HostStateChanged;

        _installShutdownEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            @"Local\CodexBridge.InstallShutdown");
        _installShutdownTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _installShutdownTimer.Tick += async (_, _) =>
        {
            if (!_installShutdownEvent.WaitOne(0)) return;
            _installShutdownTimer.Stop();
            await ExitApplicationAsync();
        };
        _installShutdownTimer.Start();

        BootLog("context.before-start-task");
        _ = StartAsync(startInBackground);
        BootLog("context.end");
    }

    private async Task StartAsync(bool background)
    {
        BootLog("startup.begin");
        var startupErrorPath = Path.Combine(
            Path.GetDirectoryName(_host.Options.ConfigurationPath)
                ?? throw new InvalidOperationException("无法确定诊断目录。"),
            "startup-error.log");

        // 先显示管理窗口，再后台启动 Host。Host 初始化可能涉及网络和本地服务，
        // 不能让这些耗时操作阻塞首次启动的窗口创建。
        if (!background)
        {
            // 在 Application.Run 建立消息循环后再显示，避免构造阶段 Show() 被吞掉。
            void ShowMainForm(object? _, EventArgs __)
            {
                Application.Idle -= ShowMainForm;
                if (!_mainForm.IsDisposed) _mainForm.ShowFromTray();
            }
            Application.Idle += ShowMainForm;
            BootLog("startup.idle-hooked");
        }

        try
        {
            BootLog("startup.host-starting");
            await _host.StartAsync();
            BootLog("startup.host-started");
            UpdateTrayState();
            if (File.Exists(startupErrorPath)) File.Delete(startupErrorPath);
        }
        catch (Exception exception)
        {
            BootLog("startup.error " + exception);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(startupErrorPath)!);
                File.WriteAllText(startupErrorPath, exception.ToString());
            }
            catch
            {
                // The tray notification remains the last-resort diagnostic channel.
            }
            _trayIcon.ShowBalloonTip(
                5000,
                "Codex Bridge 启动失败",
                exception.Message,
                ToolTipIcon.Error);
        }

    }

    private async Task ExitApplicationAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _installShutdownTimer.Stop();
        _trayIcon.Visible = false;
        _host.StateChanged -= HostStateChanged;
        try { await _host.DisposeAsync(); }
        finally
        {
            _trayIcon.Dispose();
            _trayIconResource?.Dispose();
            _trayBitmap?.Dispose();
            _mainForm.Dispose();
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _installShutdownTimer.Dispose();
            _installShutdownEvent.Dispose();
            _trayIcon.Dispose();
            _trayIconResource?.Dispose();
            _trayBitmap?.Dispose();
            _mainForm.Dispose();
        }
        base.Dispose(disposing);
    }

    private ToolStripMenuItem TrayItem(string text, string glyph, Keys shortcut, EventHandler click, Color? foreground = null)
    {
        var item = new ToolStripMenuItem(text, IconGlyph.Render(glyph, 16, foreground ?? UiTheme.TextSecondary), click)
        {
            ForeColor = foreground ?? UiTheme.Text,
            ToolTipText = shortcut == Keys.None ? text : $"{text} ({shortcut})",
        };
        // 某些 WinForms 运行时会把 Keys.Enter 等单键值判定为非法快捷键枚举，
        // 导致托盘初始化阶段直接退出。菜单命令仍可正常点击，快捷键提示保留在 ToolTip 中。
        return item;
    }

    private void HostStateChanged(object? sender, EventArgs e)
    {
        if (_exiting) return;
        if (_mainForm.IsHandleCreated)
            _mainForm.BeginInvoke(new Action(UpdateTrayState));
    }

    private void UpdateTrayState()
    {
        if (_exiting) return;
        var state = _host.State;
        _toggleHostMenuItem.Text = state == HostRuntimeState.Running ? "暂停 Host" : "恢复 Host";
        if (_trayHeaderState is not null)
        {
            _trayHeaderState.Text = state == HostRuntimeState.Running ? "Host 运行中" : "Host 已暂停";
            _trayHeaderState.ForeColor = state == HostRuntimeState.Running ? UiTheme.Success : UiTheme.Warning;
        }
        _toggleHostMenuItem.Image = IconGlyph.Render(state == HostRuntimeState.Running ? IconGlyph.Pause : IconGlyph.Play, 16, UiTheme.TextSecondary);
        var old = _trayIconResource;
        _trayIconResource = CreateTrayIcon(state);
        _trayIcon.Icon = _trayIconResource;
        old?.Dispose();
        _trayIcon.Text = state == HostRuntimeState.Running ? "Codex Bridge · 运行中" : "Codex Bridge · 已暂停";
    }

    private Icon CreateTrayIcon(HostRuntimeState state)
    {
        _trayBitmap?.Dispose();
        _trayBitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(_trayBitmap))
        using (var bg = new SolidBrush(state == HostRuntimeState.Running ? UiTheme.Accent : UiTheme.Muted))
            using (var pen = new Pen(Color.White, 2.4f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillPath(bg, CodexRemote.UI.Draw.RoundedRect(new Rectangle(1, 1, 30, 30), 7));
            // 几何化品牌标记：在 16px 托盘尺寸下比文字更清晰，也不会因字体缺失而变形。
            g.DrawLines(pen, new[] { new PointF(10, 10), new PointF(16, 16), new PointF(10, 22) });
            g.DrawLine(pen, 17, 21, 23, 21);
        }
        using var source = Icon.FromHandle(_trayBitmap.GetHicon());
        return new Icon(source, new Size(16, 16));
    }
}
