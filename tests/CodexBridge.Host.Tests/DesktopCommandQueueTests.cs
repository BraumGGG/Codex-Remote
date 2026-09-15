using CodexBridge.Host.Services;

namespace CodexBridge.Host.Tests;

public sealed class DesktopCommandQueueTests
{
    [Fact]
    public async Task EnqueueAsync_AllowsOnlyOneUiActionAtATime()
    {
        var queue = new DesktopCommandQueue();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var first = queue.EnqueueAsync(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, CancellationToken.None);
        await firstEntered.Task;

        var second = queue.EnqueueAsync(_ =>
        {
            secondEntered = true;
            return Task.FromResult(2);
        }, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(secondEntered);

        releaseFirst.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondEntered);
    }
}
