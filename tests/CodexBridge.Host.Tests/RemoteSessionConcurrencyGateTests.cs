using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class RemoteSessionConcurrencyGateTests
{
    [Fact]
    public void NewSessionForSameDeviceCancelsOldWithoutConsumingAnotherSlot()
    {
        var gate = new RemoteSessionConcurrencyGate(1);
        using var first = gate.Acquire("device-1", CancellationToken.None);
        using var replacement = gate.Acquire("device-1", CancellationToken.None);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(replacement.Token.IsCancellationRequested);
        first.Dispose();
        Assert.Throws<RemoteSessionLimitException>(() => gate.Acquire("device-2", CancellationToken.None));
        replacement.Dispose();
        using var other = gate.Acquire("device-2", CancellationToken.None);
        Assert.False(other.Token.IsCancellationRequested);
    }

    [Fact]
    public void DistinctDevicesAreLimitedAndParentCancellationPropagates()
    {
        var gate = new RemoteSessionConcurrencyGate(2);
        using var cancellation = new CancellationTokenSource();
        using var first = gate.Acquire("device-1", cancellation.Token);
        using var second = gate.Acquire("device-2", CancellationToken.None);
        Assert.Throws<RemoteSessionLimitException>(() => gate.Acquire("device-3", CancellationToken.None));
        cancellation.Cancel();
        Assert.True(first.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ReplacementWaitsForPreviousLeaseToRelease()
    {
        var gate = new RemoteSessionConcurrencyGate(1);
        using var first = gate.Acquire("device-1", CancellationToken.None);

        var replacementTask = gate.AcquireAfterPreviousAsync("device-1", CancellationToken.None);
        await Task.Delay(25);
        Assert.False(replacementTask.IsCompleted);
        Assert.True(first.Token.IsCancellationRequested);

        first.Dispose();
        using var replacement = await replacementTask;
        Assert.False(replacement.Token.IsCancellationRequested);
    }

    [Fact]
    public void FailedCandidateDoesNotCancelHealthySession()
    {
        var gate = new RemoteSessionConcurrencyGate(1);
        using var healthy = gate.Acquire("device-1", CancellationToken.None);
        using (var candidate = gate.AcquireCandidate("device-1", CancellationToken.None))
        {
            Assert.False(healthy.Token.IsCancellationRequested);
            candidate.Dispose();
        }

        Assert.False(healthy.Token.IsCancellationRequested);
        using var other = gate.AcquireCandidate("device-1", CancellationToken.None);
        gate.CommitCandidate(other);
        Assert.True(healthy.Token.IsCancellationRequested);
    }
}
