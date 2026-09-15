using System.Text.Json;
using CodexBridge.Host.Diagnostics;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;

namespace CodexBridge.Host.Tests;

public sealed class BridgeDiagnosticsTests
{
    [Fact]
    public void Capture_TracksTransitionsExpiresErrorsAndReflectsDesktopState()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));
        var desktop = new FakeDesktopProbe(false);
        var service = new BridgeDiagnosticsService(
            time,
            desktop,
            Options(enabled: true),
            TimeSpan.FromMinutes(15));

        service.Report(
            BridgeComponent.Signal,
            BridgeComponentState.Offline,
            "signal_connection_failed");
        var failed = service.Capture();
        Assert.Equal(BridgeComponentState.Offline, failed.Signal.State);
        Assert.Equal("signal_connection_failed", failed.Signal.ErrorCode);
        Assert.Equal(BridgeComponentState.Offline, failed.Desktop.State);
        Assert.Equal("desktop_unavailable", failed.Desktop.ErrorCode);

        time.Advance(TimeSpan.FromMinutes(16));
        desktop.Online = true;
        var expired = service.Capture();
        Assert.Null(expired.Signal.ErrorCode);
        Assert.Equal(BridgeComponentState.Online, expired.Desktop.State);
        Assert.Null(expired.Desktop.ErrorCode);

        service.Report(BridgeComponent.Signal, BridgeComponentState.Online);
        Assert.Null(service.Capture().Signal.ErrorCode);

        service.Report(BridgeComponent.Pairing, BridgeComponentState.Starting);
        Assert.Equal(BridgeComponentState.Starting, service.Capture().Pairing.State);
        service.Report(BridgeComponent.Pairing, BridgeComponentState.Offline, "pairing_client_timeout");
        Assert.Equal("pairing_client_timeout", service.Capture().Pairing.ErrorCode);
    }

    [Theory]
    [InlineData("secret=https://signal.example:8443")]
    [InlineData("thread-id-01a00749")]
    [InlineData("C:\\private\\project")]
    [InlineData("UPPERCASE")]
    public void Report_RejectsFreeTextAndSensitiveValues(string value)
    {
        var service = CreateService();

        Assert.Throws<ArgumentException>(() => service.Report(
            BridgeComponent.Signal,
            BridgeComponentState.Offline,
            value));
    }

    [Fact]
    public async Task ConcurrentReportsAndCaptures_ReturnCompleteFixedShapeSnapshots()
    {
        var service = CreateService();
        var writers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                service.Report(
                    BridgeComponent.Sidecar,
                    index % 2 == 0 ? BridgeComponentState.Online : BridgeComponentState.Degraded,
                    index % 2 == 0 ? null : "sidecar_restarting");
            }
        }));
        var readers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                var snapshot = service.Capture();
                Assert.Equal(BridgeComponentState.Online, snapshot.Host.State);
                Assert.NotNull(snapshot.Signal);
                Assert.NotNull(snapshot.Turn);
                Assert.NotNull(snapshot.Sidecar);
                Assert.NotNull(snapshot.Pairing);
                Assert.NotNull(snapshot.Desktop);
            }
        }));

        await Task.WhenAll(writers.Concat(readers));
    }

    [Fact]
    public void SerializedSnapshot_ContainsNoSensitiveFieldOrValue()
    {
        var json = JsonSerializer.Serialize(CreateService().Capture());

        foreach (var forbidden in new[]
                 {
                     "token", "secret", "credential", "sdp", "address", "port",
                     "project", "thread", "message", "path", "deviceid",
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReconnectBackoff_IsBoundedAndResettable()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        Assert.Equal(
            [1, 2, 4, 8, 16, 30, 30],
            Enumerable.Range(0, 7).Select(_ => (int)backoff.Next().TotalSeconds));
        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
    }

    [Fact]
    public void SidecarStopped_DoesNotOverwriteSpecificSetupFailure()
    {
        var service = CreateService();
        service.Report(
            BridgeComponent.Sidecar,
            BridgeComponentState.Offline,
            "sidecar_transport_integrity_signature_invalid");

        RemoteSessionFactory.ObserveSidecarLifecycle(service, TransportLifecycleState.Stopped);

        var sidecar = service.Capture().Sidecar;
        Assert.Equal(BridgeComponentState.Offline, sidecar.State);
        Assert.Equal("sidecar_transport_integrity_signature_invalid", sidecar.ErrorCode);
    }

    [Fact]
    public void SidecarStopped_AfterOnlineSessionReportsIdleWithoutAnError()
    {
        var service = CreateService();
        service.Report(BridgeComponent.Sidecar, BridgeComponentState.Online);

        RemoteSessionFactory.ObserveSidecarLifecycle(service, TransportLifecycleState.Stopped);

        var sidecar = service.Capture().Sidecar;
        Assert.Equal(BridgeComponentState.Offline, sidecar.State);
        Assert.Null(sidecar.ErrorCode);
    }

    private static BridgeDiagnosticsService CreateService() => new(
        TimeProvider.System,
        new FakeDesktopProbe(true),
        Options(enabled: false));

    private static RemoteAccessOptions Options(bool enabled) => new(
        enabled,
        null,
        null,
        "identity.json",
        "devices.json",
        "receipts.json",
        "transport.exe",
        "manifest.json",
        false,
        "entitlements.json",
        new Dictionary<string, string>(),
        null,
        "entitlement-credentials.json");

    private sealed class FakeDesktopProbe(bool online) : IDesktopStatusProbe
    {
        public bool Online { get; set; } = online;
        public bool IsOnline() => Online;
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
