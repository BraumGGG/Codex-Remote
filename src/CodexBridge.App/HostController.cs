using CodexBridge.Core;
using CodexBridge.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.ExceptionServices;
using CodexBridge.Windows;

namespace CodexBridge.App;

public enum HostRuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
}

public interface IBridgeHostRuntime : IAsyncDisposable
{
    IServiceProvider Services { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IBridgeHostRuntimeFactory
{
    IBridgeHostRuntime Create(HostOptions options);
}

public sealed class HostController : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HostOptions _options;
    private readonly IBridgeHostRuntimeFactory _factory;
    private IBridgeHostRuntime? _runtime;

    public HostController(
        HostOptions options,
        IBridgeHostRuntimeFactory? factory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _factory = factory ?? new AspNetBridgeHostRuntimeFactory();
    }

    public event EventHandler? StateChanged;

    public HostRuntimeState State { get; private set; } = HostRuntimeState.Stopped;

    public Exception? LastError { get; private set; }

    public IServiceProvider? Services => _runtime?.Services;

    public HostOptions Options => _options;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == HostRuntimeState.Running) return;
            SetState(HostRuntimeState.Starting);
            var runtime = _factory.Create(_options);
            try
            {
                await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
                _runtime = runtime;
                LastError = null;
                SetState(HostRuntimeState.Running);
            }
            catch (Exception exception)
            {
                LastError = exception;
                await runtime.DisposeAsync().ConfigureAwait(false);
                SetState(HostRuntimeState.Faulted);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is null)
            {
                SetState(HostRuntimeState.Stopped);
                return;
            }

            SetState(HostRuntimeState.Stopping);
            var runtime = _runtime;
            _runtime = null;
            Exception? failure = null;
            try
            {
                await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            if (failure is not null)
            {
                LastError = failure;
                SetState(HostRuntimeState.Faulted);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            LastError = null;
            SetState(HostRuntimeState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateProjectsAsync(
        IReadOnlyList<string> projectPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        var wasRunning = State == HostRuntimeState.Running;
        if (wasRunning) await StopAsync(cancellationToken).ConfigureAwait(false);
        new BridgeConfigurationStore(_options.ConfigurationPath).Save(new BridgeConfiguration(
            BridgeConfiguration.CurrentVersion,
            projectPaths.Select(path => new AuthorizedProject(path)).ToArray()));
        if (wasRunning) await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public BridgeConfiguration LoadConfiguration() =>
        new BridgeConfigurationStore(_options.ConfigurationPath).LoadOrCreate();

    public Task<IReadOnlyList<DiscoveredProject>> DiscoverProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var discovery = Services?.GetService<IProjectDiscovery>()
            ?? new SqliteProjectDiscovery(_options.StateDatabasePath);
        return discovery.ListAsync(cancellationToken);
    }

    private void SetState(HostRuntimeState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { _gate.Dispose(); }
    }
}

public sealed class AspNetBridgeHostRuntimeFactory : IBridgeHostRuntimeFactory
{
    public IBridgeHostRuntime Create(HostOptions options) =>
        new AspNetBridgeHostRuntime(HostApplication.Build([], options));
}

internal sealed class AspNetBridgeHostRuntime(WebApplication application) : IBridgeHostRuntime
{
    public IServiceProvider Services => application.Services;
    public Task StartAsync(CancellationToken cancellationToken) => application.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => application.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => application.DisposeAsync();
}
