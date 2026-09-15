using System.Threading.Channels;

namespace CodexBridge.Host.Remote;

public sealed record RemoteTelemetryEvent(string Outcome, string CandidateType, string Version);

public sealed class RemoteTelemetryReporter
{
    private readonly Channel<RemoteTelemetryEvent> _channel = Channel.CreateBounded<RemoteTelemetryEvent>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Report(string outcome, string candidateType)
    {
        if (outcome is not ("connected" or "failed") ||
            candidateType is not ("host" or "srflx" or "relay" or "unknown")) return;
        var version = typeof(RemoteTelemetryReporter).Assembly.GetName().Version;
        _channel.Writer.TryWrite(new RemoteTelemetryEvent(
            outcome,
            candidateType,
            version is null ? "unknown" : $"{version.Major}.{version.Minor}"));
    }

    public IAsyncEnumerable<RemoteTelemetryEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
