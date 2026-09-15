using System.Net;
using CodexBridge.Host.Auth;

namespace CodexBridge.Host.Tests;

public sealed class ManagementAccessGuardTests
{
    [Fact]
    public void Authorize_RequiresBothLoopbackAddressesTokenAndFreshNonce()
    {
        var guard = new ManagementAccessGuard();
        const string nonce = "unique-nonce-0001";

        Assert.False(guard.Authorize(
            IPAddress.Parse("192.0.2.10"), IPAddress.Loopback, guard.Token, nonce));
        Assert.False(guard.Authorize(
            IPAddress.Loopback, IPAddress.Parse("192.0.2.20"), guard.Token, nonce));
        Assert.False(guard.Authorize(
            IPAddress.Loopback, IPAddress.Loopback, "wrong", nonce));
        Assert.True(guard.Authorize(
            IPAddress.Loopback, IPAddress.Loopback, guard.Token, nonce));
        Assert.False(guard.Authorize(
            IPAddress.Loopback, IPAddress.Loopback, guard.Token, nonce));
    }
}
