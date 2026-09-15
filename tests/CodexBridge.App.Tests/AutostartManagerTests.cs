namespace CodexBridge.App.Tests;

public sealed class AutostartManagerTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("autostart");

    [Fact]
    public void SetEnabled_WritesExactQuotedCurrentUserCommandAndDeletesIdempotently()
    {
        var registry = new FakeRegistry();
        var executable = Path.Combine(_directory, "CodexBridge.App.exe");
        var manager = new AutostartManager(executable, registry);

        Assert.False(manager.IsEnabled);
        manager.SetEnabled(true);
        Assert.True(manager.IsEnabled);
        Assert.Equal($"\"{Path.GetFullPath(executable)}\" --background", registry.Value);

        registry.Value += " --unexpected";
        Assert.False(manager.IsEnabled);
        manager.SetEnabled(false);
        manager.SetEnabled(false);
        Assert.Null(registry.Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeRegistry : IAutostartRegistry
    {
        public string? Value { get; set; }
        public string? Read() => Value;
        public void Write(string command) => Value = command;
        public void Delete() => Value = null;
    }
}
