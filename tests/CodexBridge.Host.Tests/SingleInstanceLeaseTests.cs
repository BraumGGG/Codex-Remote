using CodexBridge.Host;

namespace CodexBridge.Host.Tests;

public sealed class SingleInstanceLeaseTests
{
    [Fact]
    public async Task Acquire_RejectsAnotherThreadUntilLeaseIsDisposed()
    {
        var name = $@"Local\CodexBridge.Host.Tests.{Guid.NewGuid():N}";
        using (SingleInstanceLease.Acquire(name))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Task.Run(() => SingleInstanceLease.Acquire(name)));
        }

        var acquiredAgain = await Task.Run(() =>
        {
            using var lease = SingleInstanceLease.Acquire(name);
            return true;
        });

        Assert.True(acquiredAgain);
    }
}
