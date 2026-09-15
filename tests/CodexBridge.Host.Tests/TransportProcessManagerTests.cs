using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class TransportProcessManagerTests
{
    [Fact]
    public async Task Start_RejectsFailedIntegrityBeforeLaunching()
    {
        var launcher = new StubLauncher();
        await using var manager = new TransportProcessManager(
            new StubIntegrity(false), launcher, new StubPipeFactory(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task Stop_ClosesJobAndProcess()
    {
        var launcher = new StubLauncher();
        await using var manager = new TransportProcessManager(
            new StubIntegrity(true), launcher, new StubPipeFactory(), TimeProvider.System);

        await manager.StartAsync();
        await manager.StopAsync();

        Assert.Equal(1, launcher.LaunchCount);
        Assert.True(launcher.LastProcess!.StopCalled);
    }

    [Fact]
    public async Task RealSidecar_AuthenticatesAndLeavesNoProcessAfterStop()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexBridge.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        var executable = Path.Combine(root, "artifacts", "transport", "dev", "CodexBridge.Transport.exe");
        var manifest = Path.Combine(root, "artifacts", "transport", "dev", "transport-manifest.json");
        Assert.True(File.Exists(executable), "Run scripts/build-transport.ps1 before this test.");
        var originalProcesses = System.Diagnostics.Process
            .GetProcessesByName("CodexBridge.Transport")
            .Select(process => process.Id)
            .ToHashSet();
        var integrity = new ConfiguredTransportIntegrityGate(
            new TransportIntegrityVerifier(new WinTrustAuthenticodeVerifier()),
            executable,
            manifest,
            requireAuthenticode: false);

        await using (var manager = new TransportProcessManager(
                         integrity,
                         new WindowsTransportProcessLauncher(executable),
                         new RemotePipeServerFactory(TimeSpan.FromSeconds(10)),
                         TimeProvider.System))
        {
            await manager.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Task.Delay(250);
        var leaked = System.Diagnostics.Process
            .GetProcessesByName("CodexBridge.Transport")
            .Where(process => !originalProcesses.Contains(process.Id))
            .Select(process => process.Id)
            .ToArray();
        Assert.Empty(leaked);
    }

    [Fact]
    public async Task ThreeCrashesWithinWindow_OpenCircuitAndStopRestarting()
    {
        var launcher = new StubLauncher();
        var states = new System.Collections.Concurrent.ConcurrentQueue<TransportLifecycleState>();
        await using var manager = new TransportProcessManager(
            new StubIntegrity(true),
            launcher,
            new StubPipeFactory(),
            TimeProvider.System,
            [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            states.Enqueue);
        await manager.StartAsync();

        launcher.Processes[0].Crash();
        await WaitUntilAsync(() => launcher.LaunchCount == 2);
        launcher.Processes[1].Crash();
        await WaitUntilAsync(() => launcher.LaunchCount == 3);
        launcher.Processes[2].Crash();
        await WaitUntilAsync(() => manager.IsCircuitOpen);

        await Task.Delay(50);
        Assert.Equal(3, launcher.LaunchCount);
        Assert.Contains(TransportLifecycleState.Restarting, states);
        Assert.Contains(TransportLifecycleState.CircuitOpen, states);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class StubIntegrity(bool valid) : ITransportIntegrityGate
    {
        public TransportVerificationResult Verify() => valid
            ? TransportVerificationResult.Valid(1)
            : TransportVerificationResult.Invalid("hash_mismatch");
    }

    private sealed class StubLauncher : ITransportProcessLauncher
    {
        public int LaunchCount { get; private set; }
        public StubProcess? LastProcess { get; private set; }
        public List<StubProcess> Processes { get; } = [];

        public Task<ITransportProcess> LaunchAsync(TransportLaunchRequest request, CancellationToken cancellationToken)
        {
            LaunchCount++;
            Assert.Equal(32, RemoteEncoding.Base64UrlDecode(request.BootstrapSecret).Length);
            Assert.StartsWith("codex-bridge-", request.PipeName, StringComparison.Ordinal);
            LastProcess = new StubProcess();
            Processes.Add(LastProcess);
            return Task.FromResult<ITransportProcess>(LastProcess);
        }
    }

    private sealed class StubProcess : ITransportProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited { get; private set; }
        public bool StopCalled { get; private set; }
        public Task<int> ExitCode => _exit.Task;
        public void Crash()
        {
            HasExited = true;
            _exit.TrySetResult(1);
        }
        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            StopCalled = true;
            HasExited = true;
            _exit.TrySetResult(0);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubPipeFactory : IRemotePipeServerFactory
    {
        public IRemotePipeAcceptor Create(string pipeName, byte[] secret) => new StubPipeAcceptor();
    }

    private sealed class StubPipeAcceptor : IRemotePipeAcceptor
    {
        public Task<AuthenticatedRemotePipe> AcceptAuthenticatedAsync(CancellationToken cancellationToken = default)
        {
            var pipe = new System.IO.Pipes.NamedPipeServerStream(
                $"unused-{Guid.NewGuid():N}",
                System.IO.Pipes.PipeDirection.InOut);
            return Task.FromResult(new AuthenticatedRemotePipe(pipe));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
