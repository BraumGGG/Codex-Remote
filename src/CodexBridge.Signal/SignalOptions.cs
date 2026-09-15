namespace CodexBridge.Signal;

public sealed class SignalOptions
{
    public const int MaximumFrameBytes = 64 * 1024;
    public int MaximumConnectionsPerIp { get; init; } = 10;
    public required byte[] TurnSharedSecret { get; init; }
    public string[] TurnUrls { get; init; } = [];
}
