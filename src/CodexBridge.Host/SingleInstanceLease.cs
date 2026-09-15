namespace CodexBridge.Host;

public sealed class SingleInstanceLease : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _ownsLease;

    private SingleInstanceLease(Semaphore semaphore)
    {
        _semaphore = semaphore;
        _ownsLease = true;
    }

    public static SingleInstanceLease Acquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var semaphore = new Semaphore(initialCount: 1, maximumCount: 1, name);

        try
        {
            if (!semaphore.WaitOne(TimeSpan.Zero))
            {
                throw new InvalidOperationException("Codex Bridge Host 已在运行。");
            }

            return new SingleInstanceLease(semaphore);
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsLease)
        {
            _ownsLease = false;
            _semaphore.Release();
        }

        _semaphore.Dispose();
    }
}
