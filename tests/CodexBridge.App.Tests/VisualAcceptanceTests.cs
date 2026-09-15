using CodexBridge.Core;
using CodexBridge.Host;
using CodexBridge.Host.Diagnostics;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable xUnit1031 // WinForms visual acceptance runs on one dedicated STA thread.

namespace CodexBridge.App.Tests;

public sealed class VisualAcceptanceTests
{
    [Fact]
    public void SixScenes_RenderWithoutClippingAtSupportedScales()
    {
        RunSta(() =>
        {
            foreach (var scale in new[] { 1.0f, 1.25f, 1.5f })
                RenderScale(scale);
        });
    }

    [Fact]
    public void Overview_RendersDesktopOfflineState()
    {
        RunSta(() =>
        {
            var directory = TestPaths.CreateDirectory("visual-desktop-offline");
            var options = new HostOptions(
                "http://127.0.0.1:0",
                Path.Combine(directory, "missing-state.sqlite"),
                Path.Combine(directory, "audit.jsonl"),
                Path.Combine(directory, "bridge-config.json"));
            var controller = new HostController(options, new OfflineDesktopRuntimeFactory());
            using var form = new MainForm(
                controller,
                new AutostartManager(Path.Combine(directory, "app.exe"), new FakeRegistry()));
            form.MinimumSize = new Size(600, 400);
            try
            {
                controller.StartAsync().GetAwaiter().GetResult();
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.Show();
            form.Size = new Size(900, 520);
            form.RefreshForTestingAsync().GetAwaiter().GetResult();
                Application.DoEvents();
                Assert.Contains(Descendants(form).OfType<Label>(), label => label.Text == "Codex Desktop 未启动");
                Assert.Contains(Descendants(form).OfType<Button>(), button => button.Text.Contains("启动 Desktop", StringComparison.Ordinal));
                AssertLayout(form);
                var output = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "docs", "acceptance", "screenshots", "m1-task6"));
                Directory.CreateDirectory(output);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(output, "1.00x-总览-Desktop未启动.png"));
            }
            finally
            {
                form.Dispose();
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static void RenderScale(float scale)
    {
        var directory = TestPaths.CreateDirectory($"visual-{scale:0.00}");
        var output = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "acceptance", "screenshots", "m1-task6"));
        Directory.CreateDirectory(output);
        var project = Path.Combine(directory, "示例项目");
        Directory.CreateDirectory(project);
        var options = new HostOptions(
            "http://127.0.0.1:0",
            Path.Combine(directory, "state.sqlite"),
            Path.Combine(directory, "audit.jsonl"),
            Path.Combine(directory, "bridge-config.json"));
        new BridgeConfigurationStore(options.ConfigurationPath).Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            [new AuthorizedProject(project)]));
        var controller = new HostController(options);
        controller.StartAsync().GetAwaiter().GetResult();
        using var form = new MainForm(
            controller,
            new AutostartManager(Path.Combine(directory, "CodexBridge.App.exe"), new FakeRegistry()));
        form.MinimumSize = new Size(600, 400);
        try
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.Show();
            form.Size = new Size((int)(900 * scale), (int)(520 * scale));
            Application.DoEvents();
            form.PerformLayout();

            foreach (var navigation in Descendants(form).OfType<Button>()
                         .Where(button => button.AccessibleName?.StartsWith("导航：", StringComparison.Ordinal) == true))
            {
                var page = navigation.AccessibleName![3..];
                navigation.PerformClick();
                form.PerformLayout();
                Application.DoEvents();
                var expectedTitle = page == "公网配对" ? "公网安全配对" : page;
                Assert.Contains(
                    Descendants(form).OfType<Label>(),
                    label => string.Equals(label.Text, expectedTitle, StringComparison.Ordinal));
                AssertLayout(form);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(output, $"{scale:0.00}x-{page}.png"));
            }
        }
        finally
        {
            form.Dispose();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertLayout(Control root)
    {
        foreach (var control in Descendants(root).Where(control => control.Visible))
        {
            Assert.True(control.Width > 0 && control.Height > 0, $"控件尺寸无效：{control.AccessibleName ?? control.Text}");
            if (control is Label { AutoSize: false } label && !string.IsNullOrWhiteSpace(label.Text))
            {
                var preferred = label.GetPreferredSize(Size.Empty);
                Assert.True(preferred.Height <= label.Height + 2, $"标签被垂直截断：{label.Text}");
            }
            if (control is Button button && !string.IsNullOrWhiteSpace(button.Text))
            {
                var preferred = button.GetPreferredSize(Size.Empty);
                Assert.True(preferred.Width <= button.Width + 2, $"按钮被水平截断：{button.Text}");
                Assert.True(preferred.Height <= button.Height + 2, $"按钮被垂直截断：{button.Text}");
            }
        }

        var text = string.Join('\n', Descendants(root).Select(control => control.Text));
        foreach (var forbidden in new[] { "token", "secret", "credential", "publickey", "sdp" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class FakeRegistry : IAutostartRegistry
    {
        public string? Read() => null;
        public void Write(string command) { }
        public void Delete() { }
    }

    private sealed class OfflineDesktopRuntimeFactory : IBridgeHostRuntimeFactory
    {
        public IBridgeHostRuntime Create(HostOptions options) => new OfflineDesktopRuntime();
    }

    private sealed class OfflineDesktopRuntime : IBridgeHostRuntime
    {
        private readonly ServiceProvider _services;

        public OfflineDesktopRuntime()
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<IDesktopStatusProbe, OfflineDesktopProbe>();
            services.AddSingleton(new RemoteAccessOptions(
                false, null, null, "identity", "devices", "receipts", "transport", "manifest",
                false, "entitlement", new Dictionary<string, string>(), null, "credentials"));
            services.AddSingleton<BridgeDiagnosticsService>();
            _services = services.BuildServiceProvider();
        }

        public IServiceProvider Services => _services;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    private sealed class OfflineDesktopProbe : IDesktopStatusProbe
    {
        public bool IsOnline() => false;
    }
}

#pragma warning restore xUnit1031
