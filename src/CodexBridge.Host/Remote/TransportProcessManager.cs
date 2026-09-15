using System.Diagnostics;
using System.Security.Cryptography;

namespace CodexBridge.Host.Remote;

public sealed record TransportLaunchRequest(
    string PipeName,
    string BootstrapSecret,
    int ProtocolVersion);

public interface ITransportProcess : IAsyncDisposable
{
    bool HasExited { get; }
    Task<int> ExitCode { get; }
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface ITransportProcessLauncher
{
    Task<ITransportProcess> LaunchAsync(TransportLaunchRequest request, CancellationToken cancellationToken);
}

public enum TransportLifecycleState
{
    Starting,
    Online,
    Restarting,
    CircuitOpen,
    Stopped,
}

public sealed class WindowsTransportProcessLauncher(string executablePath) : ITransportProcessLauncher
{
    public async Task<ITransportProcess> LaunchAsync(
        TransportLaunchRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(request.PipeName);
        startInfo.ArgumentList.Add("--protocol-version");
        startInfo.ArgumentList.Add(request.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        WindowsJobObject? job = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Transport process could not be started.");
            job = new WindowsJobObject();
            job.AddProcess(process);
            await process.StandardInput.WriteLineAsync(request.BootstrapSecret.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            return new WindowsTransportProcess(process, job);
        }
        catch
        {
            job?.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private sealed class WindowsTransportProcess : ITransportProcess
    {
        private readonly Process _process;
        private readonly WindowsJobObject _job;
        private readonly Task<int> _exitCode;

        public WindowsTransportProcess(Process process, WindowsJobObject job)
        {
            _process = process;
            _job = job;
            _exitCode = WaitForExitCodeAsync();
        }

        public bool HasExited => _process.HasExited;
        public Task<int> ExitCode => _exitCode;

        private async Task<int> WaitForExitCodeAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }

        public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_process.HasExited) return;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(timeout);
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _job.Dispose();
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _job.Dispose();
            try
            {
                await _exitCode.ConfigureAwait(false);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}

public sealed class TransportProcessManager : IAsyncDisposable
{
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITransportIntegrityGate _integrity;
    private readonly ITransportProcessLauncher _launcher;
    private readonly IRemotePipeServerFactory _pipeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan[] _restartDelays;
    private readonly Action<TransportLifecycleState>? _lifecycleObserver;
    private readonly Queue<DateTimeOffset> _crashes = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ITransportProcess? _process;
    private IRemotePipeAcceptor? _pipeServer;
    private AuthenticatedRemotePipe? _authenticatedPipe;
    private Task? _monitorTask;
    private bool _stopping;

    public bool IsCircuitOpen { get; private set; }

    public Stream GetAuthenticatedStream() =>
        _authenticatedPipe?.Stream
        ?? throw new InvalidOperationException("Transport sidecar is not authenticated.");

    public TransportProcessManager(
        ITransportIntegrityGate integrity,
        ITransportProcessLauncher launcher,
        IRemotePipeServerFactory pipeFactory,
        TimeProvider timeProvider,
        TimeSpan[]? restartDelays = null,
        Action<TransportLifecycleState>? lifecycleObserver = null)
    {
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _pipeFactory = pipeFactory ?? throw new ArgumentNullException(nameof(pipeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _restartDelays = restartDelays ??
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
        _lifecycleObserver = lifecycleObserver;
        if (_restartDelays.Length < 3 || _restartDelays.Any(delay => delay < TimeSpan.Zero))
            throw new ArgumentException("At least three non-negative restart delays are required.", nameof(restartDelays));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false }) return;
            if (IsCircuitOpen) throw new InvalidOperationException("Transport restart circuit is open.");
            _stopping = false;
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            _monitorTask ??= MonitorLoopAsync(_lifetime.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopping = true;
        _lifetime.Cancel();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CleanupCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_monitorTask is not null)
        {
            try { await _monitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        _lifecycleObserver?.Invoke(TransportLifecycleState.Starting);
        var verification = _integrity.Verify();
        if (!verification.IsValid)
            throw new InvalidOperationException($"Transport integrity check failed: {verification.ErrorCode}");

        var secretBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var pipeName = $"codex-bridge-{Guid.NewGuid():N}";
            _pipeServer = _pipeFactory.Create(pipeName, secretBytes);
            var acceptTask = _pipeServer.AcceptAuthenticatedAsync(cancellationToken);
            _process = await _launcher.LaunchAsync(
                new TransportLaunchRequest(
                    pipeName,
                    RemoteEncoding.Base64UrlEncode(secretBytes),
                    verification.ProtocolVersion),
                cancellationToken).ConfigureAwait(false);
            _authenticatedPipe = await acceptTask.ConfigureAwait(false);
            _lifecycleObserver?.Invoke(TransportLifecycleState.Online);
        }
        catch
        {
            await CleanupCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ITransportProcess? observed;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { observed = _process; }
            finally { _gate.Release(); }
            if (observed is null) return;

            await observed.ExitCode.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_stopping || cancellationToken.IsCancellationRequested) return;

            var now = _timeProvider.GetUtcNow();
            while (_crashes.TryPeek(out var crash) && now - crash > CrashWindow) _crashes.Dequeue();
            _crashes.Enqueue(now);
            if (_crashes.Count >= 3)
            {
                IsCircuitOpen = true;
                _lifecycleObserver?.Invoke(TransportLifecycleState.CircuitOpen);
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await CleanupCurrentAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { _gate.Release(); }
                return;
            }

            var delay = _restartDelays[_crashes.Count - 1];
            _lifecycleObserver?.Invoke(TransportLifecycleState.Restarting);
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_stopping || !ReferenceEquals(_process, observed)) return;
                await CleanupCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task CleanupCurrentAsync(CancellationToken cancellationToken)
    {
        if (_authenticatedPipe is not null)
        {
            await _authenticatedPipe.DisposeAsync().ConfigureAwait(false);
            _authenticatedPipe = null;
        }
        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync().ConfigureAwait(false);
            _pipeServer = null;
        }
        if (_process is not null)
        {
            await _process.StopAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);
            _process = null;
        }
        if (_stopping) _lifecycleObserver?.Invoke(TransportLifecycleState.Stopped);
    }
}
