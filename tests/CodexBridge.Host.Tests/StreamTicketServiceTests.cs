using CodexBridge.Host.Services;

namespace CodexBridge.Host.Tests;

public sealed class StreamTicketServiceTests
{
    [Fact]
    public void Consume_AllowsOnlyMatchingThreadAndSingleUse()
    {
        var service = new StreamTicketService(TimeProvider.System);
        var wrongTarget = service.Issue("device-1", "thread-1");
        var correctTarget = service.Issue("device-1", "thread-1");

        Assert.False(service.Consume(wrongTarget.Token, "thread-2"));
        Assert.False(service.Consume(wrongTarget.Token, "thread-1"));
        Assert.True(service.Consume(correctTarget.Token, "thread-1"));
        Assert.False(service.Consume(correctTarget.Token, "thread-1"));
    }
}
