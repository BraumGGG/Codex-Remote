using System.Text.Json;
using CodexBridge.Core;

namespace CodexBridge.Core.Tests;

public sealed class BridgeConfigurationStoreTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("bridge-config");

    [Fact]
    public void LoadOrCreate_CreatesEmptyVersionedConfiguration()
    {
        var path = Path.Combine(_directory, "bridge-config.json");
        var store = new BridgeConfigurationStore(path);

        var configuration = store.LoadOrCreate();

        Assert.Equal(BridgeConfiguration.CurrentVersion, configuration.Version);
        Assert.Empty(configuration.Projects);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_NormalizesPathsAndRoundTrips()
    {
        var project = Path.Combine(_directory, "project");
        var path = Path.Combine(_directory, "bridge-config.json");
        var store = new BridgeConfigurationStore(path);

        store.Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            [new AuthorizedProject(project + Path.DirectorySeparatorChar)]));
        var loaded = store.LoadOrCreate();

        var saved = Assert.Single(loaded.Projects);
        Assert.Equal(TargetPolicy.NormalizePath(project), saved.Path);
        Assert.DoesNotContain(".tmp", Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_RejectsDuplicateAndOverlappingProjects()
    {
        var project = Path.Combine(_directory, "project");
        var child = Path.Combine(project, "child");
        var store = new BridgeConfigurationStore(Path.Combine(_directory, "bridge-config.json"));

        Assert.Throws<InvalidDataException>(() => store.Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            [new AuthorizedProject(project), new AuthorizedProject(project.ToUpperInvariant())])));
        Assert.Throws<InvalidDataException>(() => store.Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            [new AuthorizedProject(project), new AuthorizedProject(child)])));
    }

    [Fact]
    public void Save_RejectsRelativeRootAndTooManyProjects()
    {
        var store = new BridgeConfigurationStore(Path.Combine(_directory, "bridge-config.json"));
        Assert.Throws<InvalidDataException>(() => store.Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            [new AuthorizedProject("relative-project")])));
        Assert.Throws<InvalidDataException>(() => store.Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            [new AuthorizedProject(Path.GetPathRoot(_directory)!)])));
        Assert.Throws<InvalidDataException>(() => store.Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            Enumerable.Range(0, 33)
                .Select(index => new AuthorizedProject(Path.Combine(_directory, $"p-{index}")))
                .ToArray())));
    }

    [Fact]
    public void LoadOrCreate_RejectsUnknownVersionAndInvalidJson()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "bridge-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = BridgeConfiguration.CurrentVersion + 1,
            projects = Array.Empty<object>(),
        }));
        var store = new BridgeConfigurationStore(path);

        Assert.Throws<InvalidDataException>(() => store.LoadOrCreate());

        File.WriteAllText(path, "{not-json");
        Assert.Throws<InvalidDataException>(() => store.LoadOrCreate());
    }

    [Fact]
    public void Save_DoesNotRequireProjectDirectoryToBeOnline()
    {
        var unavailable = Path.Combine(_directory, "offline-drive-project");
        var store = new BridgeConfigurationStore(Path.Combine(_directory, "bridge-config.json"));

        store.Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            [new AuthorizedProject(unavailable)]));

        Assert.Equal(TargetPolicy.NormalizePath(unavailable), store.LoadOrCreate().Projects.Single().Path);
        Assert.False(Directory.Exists(unavailable));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
