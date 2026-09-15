namespace CodexBridge.Host.Services;

public sealed class DesktopCommandQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> EnqueueAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
