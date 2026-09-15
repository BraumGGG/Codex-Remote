using CodexBridge.Host;

#pragma warning disable xUnit1031 // UI acceptance runs on one dedicated STA thread.

namespace CodexBridge.App.Tests;

public sealed class MainFormTests
{
    [Fact]
    public void Form_ProvidesSixAccessibleStableScenesWithoutSensitiveText()
    {
        RunSta(() =>
        {
            var directory = TestPaths.CreateDirectory("main-form");
            try
            {
                var options = new HostOptions(
                    "http://127.0.0.1:0",
                    Path.Combine(directory, "state.sqlite"),
                    Path.Combine(directory, "audit.jsonl"),
                    Path.Combine(directory, "bridge-config.json"));
                var controller = new HostController(options, new NeverUsedFactory());
                var form = new MainForm(
                    controller,
                    new AutostartManager(Path.Combine(directory, "CodexBridge.App.exe"), new FakeRegistry()));
                try
                {
                    Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
                    Assert.True(form.MinimumSize.Width >= 920);
                    Assert.True(form.MinimumSize.Height >= 640);
                    form.ShowInTaskbar = false;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    Application.DoEvents();
                    var navigation = Descendants(form).OfType<Button>()
                        .Where(button => button.AccessibleName?.StartsWith("导航：", StringComparison.Ordinal) == true)
                        .ToArray();
                    Assert.Equal(5, navigation.Length);
                    Assert.DoesNotContain(navigation, button => button.AccessibleName?.Contains("托盘", StringComparison.Ordinal) == true);
                    Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);
                    Assert.True(form.ControlBox);
                    Assert.True(form.MinimizeBox);
                    Assert.True(form.MaximizeBox);
                    foreach (var button in navigation)
                    {
                        Assert.True(button.Height >= 40);
                        button.PerformClick();
                        form.PerformLayout();
                    }

                    var allText = string.Join('\n', Descendants(form).Select(control => control.Text));
                    foreach (var forbidden in new[] { "token", "secret", "credential", "publickey", "sdp" })
                        Assert.DoesNotContain(forbidden, allText, StringComparison.OrdinalIgnoreCase);
                    Assert.All(
                        Descendants(form).OfType<Button>().Where(button => button.Visible),
                        button => Assert.True(button.MinimumSize.Height >= 38 || button.Height >= 40));
                }
                finally
                {
                    form.Dispose();
                    var disposal = controller.DisposeAsync();
                    Assert.True(disposal.IsCompletedSuccessfully);
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void Refresh_ReusesTheActivePageAndItsListControls()
    {
        RunSta(() =>
        {
            var directory = TestPaths.CreateDirectory("stable-refresh");
            try
            {
                var options = new HostOptions(
                    "http://127.0.0.1:0",
                    Path.Combine(directory, "missing-state.sqlite"),
                    Path.Combine(directory, "audit.jsonl"),
                    Path.Combine(directory, "bridge-config.json"));
                var controller = new HostController(options, new NeverUsedFactory());
                using var form = new MainForm(
                    controller,
                    new AutostartManager(Path.Combine(directory, "app.exe"), new FakeRegistry()));
                form.ShowInTaskbar = false;
                form.Show();
                Descendants(form).OfType<Button>().Single(button => button.AccessibleName == "导航：已配对设备").PerformClick();
                Application.DoEvents();
                var firstList = Descendants(form).OfType<ListView>().Single(list => list.Columns[0].Text == "设备");

                for (var index = 0; index < 10; index++)
                    form.RefreshForTestingAsync().GetAwaiter().GetResult();

                var secondList = Descendants(form).OfType<ListView>().Single(list => list.Columns[0].Text == "设备");
                Assert.Same(firstList, secondList);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });
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

    private sealed class NeverUsedFactory : IBridgeHostRuntimeFactory
    {
        public IBridgeHostRuntime Create(HostOptions options) => throw new InvalidOperationException();
    }

    private sealed class FakeRegistry : IAutostartRegistry
    {
        public string? Read() => null;
        public void Write(string command) { }
        public void Delete() { }
    }
}

#pragma warning restore xUnit1031
