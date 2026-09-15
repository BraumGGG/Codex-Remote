using CodexBridge.Host;
using Microsoft.Extensions.DependencyInjection;

namespace CodexBridge.App.Tests;

public sealed class HostControllerTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("host-controller");

    [Fact]
    public async Task StartAndStop_AreIdempotent()
    {
        var factory = new FakeFactory();
        await using var controller = new HostController(Options(), factory);

        await Task.WhenAll(controller.StartAsync(), controller.StartAsync());

        Assert.Equal(HostRuntimeState.Running, controller.State);
        Assert.Equal(1, factory.CreateCount);
        await controller.StopAsync();
        await controller.StopAsync();
        Assert.Equal(HostRuntimeState.Stopped, controller.State);
        Assert.Equal(1, factory.Runtimes.Single().StopCount);
    }

    [Fact]
    public async Task StartFailure_EntersFaultedAndCanRetryWithFreshRuntime()
    {
        var factory = new FakeFactory(failFirstStart: true);
        await using var controller = new HostController(Options(), factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
        Assert.Equal(HostRuntimeState.Faulted, controller.State);
        Assert.NotNull(controller.LastError);

        await controller.StartAsync();
        Assert.Equal(HostRuntimeState.Running, controller.State);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task UpdateProjects_AtomicallySavesAndRestartsOnlyWhenRunning()
    {
        var factory = new FakeFactory();
        await using var controller = new HostController(Options(), factory);
        var projectOne = Path.Combine(_directory, "project-one");
        var projectTwo = Path.Combine(_directory, "project-two");

        await controller.UpdateProjectsAsync([projectOne, projectTwo]);
        Assert.Equal(HostRuntimeState.Stopped, controller.State);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(2, controller.LoadConfiguration().Projects.Count);

        await controller.StartAsync();
        await controller.UpdateProjectsAsync([projectTwo]);
        Assert.Equal(HostRuntimeState.Running, controller.State);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(projectTwo, controller.LoadConfiguration().Projects.Single().Path);
    }

    [Fact]
    public async Task StopFailure_StillDisposesRuntimeAndKeepsFaultedState()
    {
        var factory = new FakeFactory(failStop: true);
        await using var controller = new HostController(Options(), factory);
        await controller.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StopAsync());

        Assert.Equal(HostRuntimeState.Faulted, controller.State);
        Assert.Equal(1, factory.Runtimes.Single().DisposeCount);
    }

    [Fact]
    public async Task DisposeFailure_EntersFaultedInsteadOfReportingStopped()
    {
        var factory = new FakeFactory(failDispose: true);
        var controller = new HostController(Options(), factory);
        await controller.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StopAsync());

        Assert.Equal(HostRuntimeState.Faulted, controller.State);
        Assert.Equal("fake dispose failure", controller.LastError?.Message);
    }

    private HostOptions Options() => new(
        "http://127.0.0.1:0",
        Path.Combine(_directory, "state.sqlite"),
        Path.Combine(_directory, "audit.jsonl"),
        Path.Combine(_directory, "bridge-config.json"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeFactory(
        bool failFirstStart = false,
        bool failStop = false,
        bool failDispose = false) : IBridgeHostRuntimeFactory
    {
        public int CreateCount { get; private set; }
        public List<FakeRuntime> Runtimes { get; } = [];

        public IBridgeHostRuntime Create(HostOptions options)
        {
            CreateCount++;
            var runtime = new FakeRuntime(failFirstStart && CreateCount == 1, failStop, failDispose);
            Runtimes.Add(runtime);
            return runtime;
        }
    }

    private sealed class FakeRuntime : IBridgeHostRuntime
    {
        private readonly bool _failStart;
        private readonly bool _failStop;
        private readonly bool _failDispose;
        public FakeRuntime(bool failStart, bool failStop, bool failDispose)
        {
            _failStart = failStart;
            _failStop = failStop;
            _failDispose = failDispose;
            Services = new ServiceCollection().BuildServiceProvider();
        }

        public IServiceProvider Services { get; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_failStart) throw new InvalidOperationException("fake start failure");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            if (_failStop) throw new InvalidOperationException("fake stop failure");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            (Services as IDisposable)?.Dispose();
            if (_failDispose) throw new InvalidOperationException("fake dispose failure");
            return ValueTask.CompletedTask;
        }
    }
}
