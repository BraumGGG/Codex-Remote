using CodexBridge.Core;
using CodexBridge.Host;
using Microsoft.Data.Sqlite;

#pragma warning disable xUnit1031 // WinForms acceptance stays on one dedicated STA thread with bounded waits.

namespace CodexBridge.App.Tests;

public sealed class MainFormInteractionTests
{
    [Fact]
    public void ProjectAccess_AutoDiscoversProjectsAndAuthorizesByCheckBox()
    {
        RunSta(() =>
        {
            var directory = TestPaths.CreateDirectory("main-form-interaction");
            var first = Path.Combine(directory, "first");
            var second = Path.Combine(directory, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var options = new HostOptions(
                "http://127.0.0.1:0",
                Path.Combine(directory, "state.sqlite"),
                Path.Combine(directory, "audit.jsonl"),
                Path.Combine(directory, "bridge-config.json"));
            CreateStateDatabase(options.StateDatabasePath, first, second);
            new BridgeConfigurationStore(options.ConfigurationPath).Save(new BridgeConfiguration(
                BridgeConfiguration.CurrentVersion,
                [new AuthorizedProject(first)]));
            var controller = new HostController(options);
            controller.StartAsync().GetAwaiter().GetResult();
            var dialogs = new FakeDialogs();
            using var form = new MainForm(
                controller,
                new AutostartManager(Path.Combine(directory, "app.exe"), new FakeRegistry()),
                dialogs);
            try
            {
                form.ShowInTaskbar = false;
                form.Show();
                Click(form, "导航：授权项目");
                WaitUntil(() => Descendants(form).OfType<Label>().Any(label => label.Text == "first") &&
                    Descendants(form).OfType<Label>().Any(label => label.Text == "second"));
                Assert.Contains(Descendants(form).OfType<Label>(), label => label.Text == "已授权 · Codex 项目");
                var authorize = Descendants(form).OfType<Button>().Single(button => button.Text == "授权");
                authorize.PerformClick();
                WaitUntil(() => controller.LoadConfiguration().Projects.Count == 2);

                var projectPageText = string.Join('\n', Descendants(form).Select(control => control.Text));
                Assert.Contains("添加项目", projectPageText, StringComparison.Ordinal);
                Assert.DoesNotContain("选择文件夹", projectPageText, StringComparison.Ordinal);

                Click(form, "导航：公网配对");
                var pairingText = string.Join('\n', Descendants(form).Where(control => control.Visible).Select(control => control.Text));
                Assert.DoesNotContain("复制链接", pairingText, StringComparison.Ordinal);
                Assert.DoesNotContain("https://", pairingText, StringComparison.OrdinalIgnoreCase);

                Click(form, "导航：已配对设备");
                WaitUntil(() => Descendants(form).OfType<ListView>()
                    .Any(candidate => candidate.Columns.Count > 0 && candidate.Columns[0].Text == "设备"));
                var list = Descendants(form).OfType<ListView>()
                    .Single(candidate => candidate.Columns.Count > 0 && candidate.Columns[0].Text == "设备");
                Assert.Empty(list.Items);
                var pageText = string.Join('\n', Descendants(form).Select(control => control.Text));
                Assert.DoesNotContain("局域网", pageText, StringComparison.Ordinal);
                Assert.DoesNotContain("旧版本机", pageText, StringComparison.Ordinal);
            }
            finally
            {
                form.Dispose();
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static void Click(Control root, string accessibleName)
    {
        var button = Descendants(root).OfType<Button>()
            .Single(control => string.Equals(control.AccessibleName, accessibleName, StringComparison.Ordinal));
        button.PerformClick();
        Application.DoEvents();
    }

    private static void WaitUntil(Func<bool> condition, Func<string>? diagnostic = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert.True(condition(), $"UI 操作未在 5 秒内完成。{diagnostic?.Invoke()}");
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

    private static void CreateStateDatabase(string path, string first, string second)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE threads (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                cwd TEXT NOT NULL,
                preview TEXT NOT NULL,
                updated_at_ms INTEGER NOT NULL,
                archived INTEGER NOT NULL,
                rollout_path TEXT NOT NULL,
                model_provider TEXT NOT NULL,
                recency_at_ms INTEGER NOT NULL
            );
            INSERT INTO threads VALUES
              ('one', 'one', $first, '', 100, 0, 'one.jsonl', 'custom', 100),
              ('two', 'two', $second, '', 200, 0, 'two.jsonl', 'custom', 200);
            """;
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$second", second);
        command.ExecuteNonQuery();
    }

    private sealed class FakeDialogs : IBridgeUiDialogs
    {
        public int ConfirmationCount { get; private set; }
        public string? SelectProject(IWin32Window owner) => throw new Xunit.Sdk.XunitException("不应打开文件夹选择器");
        public bool Confirm(IWin32Window owner, string message, string title)
        {
            ConfirmationCount++;
            return true;
        }
        public void ShowError(IWin32Window owner, string message) => throw new Xunit.Sdk.XunitException(message);
    }

    private sealed class FakeRegistry : IAutostartRegistry
    {
        public string? Read() => null;
        public void Write(string command) { }
        public void Delete() { }
    }
}

#pragma warning restore xUnit1031
