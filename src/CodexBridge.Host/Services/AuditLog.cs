using System.Text.Json;

namespace CodexBridge.Host.Services;

public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string DeviceId,
    string Action,
    string ThreadId,
    string Result,
    string? ErrorCode,
    long DurationMs);

public sealed class AuditLog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    public AuditLog(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    public async Task WriteAsync(AuditEvent item, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("审计日志路径没有父目录。");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(item, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_path, json + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
