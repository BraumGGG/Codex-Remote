using System.Text.RegularExpressions;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;

namespace CodexBridge.Host.Diagnostics;

public sealed partial class BridgeDiagnosticsService
{
    private static readonly TimeSpan DefaultErrorLifetime = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly IDesktopStatusProbe _desktop;
    private readonly TimeSpan _errorLifetime;
    private readonly Dictionary<BridgeComponent, BridgeComponentHealth> _states;

    public BridgeDiagnosticsService(
        TimeProvider timeProvider,
        IDesktopStatusProbe desktop,
        RemoteAccessOptions remoteOptions)
        : this(timeProvider, desktop, remoteOptions, DefaultErrorLifetime)
    {
    }

    public BridgeDiagnosticsService(
        TimeProvider timeProvider,
        IDesktopStatusProbe desktop,
        RemoteAccessOptions remoteOptions,
        TimeSpan errorLifetime)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        ArgumentNullException.ThrowIfNull(remoteOptions);
        if (errorLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(errorLifetime));
        _errorLifetime = errorLifetime;
        var now = _timeProvider.GetUtcNow();
        var remoteState = remoteOptions.Enabled
            ? BridgeComponentState.Starting
            : BridgeComponentState.Disabled;
        _states = new Dictionary<BridgeComponent, BridgeComponentHealth>
        {
            [BridgeComponent.Host] = new(BridgeComponentState.Online, now, null),
            [BridgeComponent.Signal] = new(remoteState, now, null),
            [BridgeComponent.Turn] = new(remoteState, now, null),
            [BridgeComponent.Sidecar] = new(remoteState, now, null),
            [BridgeComponent.Pairing] = new(remoteState, now, null),
        };
    }

    public void Report(
        BridgeComponent component,
        BridgeComponentState state,
        string? errorCode = null)
    {
        if (component == BridgeComponent.Host && state == BridgeComponentState.Disabled)
            throw new ArgumentException("Host cannot be disabled while diagnostics are active.", nameof(state));
        if (errorCode is not null && !ErrorCodePattern().IsMatch(errorCode))
            throw new ArgumentException("Diagnostic error code is invalid.", nameof(errorCode));
        if (state is BridgeComponentState.Online or BridgeComponentState.Starting or BridgeComponentState.Disabled)
            errorCode = null;

        lock (_gate)
        {
            var current = _states[component];
            if (current.State == state && string.Equals(current.ErrorCode, errorCode, StringComparison.Ordinal))
                return;
            _states[component] = new BridgeComponentHealth(
                state,
                _timeProvider.GetUtcNow(),
                errorCode);
        }
    }

    public BridgeHealthSnapshot Capture()
    {
        var now = _timeProvider.GetUtcNow();
        Dictionary<BridgeComponent, BridgeComponentHealth> snapshot;
        lock (_gate)
        {
            snapshot = _states.ToDictionary(
                pair => pair.Key,
                pair => ExpireError(pair.Value, now));
        }

        var desktopOnline = _desktop.IsOnline();
        var desktop = new BridgeComponentHealth(
            desktopOnline ? BridgeComponentState.Online : BridgeComponentState.Offline,
            now,
            desktopOnline ? null : "desktop_unavailable");
        return new BridgeHealthSnapshot(
            now,
            snapshot[BridgeComponent.Host],
            snapshot[BridgeComponent.Signal],
            snapshot[BridgeComponent.Turn],
            snapshot[BridgeComponent.Sidecar],
            snapshot[BridgeComponent.Pairing],
            desktop);
    }

    private BridgeComponentHealth ExpireError(BridgeComponentHealth health, DateTimeOffset now) =>
        health.ErrorCode is not null && now - health.ChangedAt >= _errorLifetime
            ? health with { ErrorCode = null }
            : health;

    [GeneratedRegex("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();
}
