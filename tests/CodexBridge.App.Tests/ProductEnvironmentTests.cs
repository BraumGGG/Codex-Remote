namespace CodexBridge.App.Tests;

using System.Text.Json;

public sealed class ProductEnvironmentTests
{
    [Fact]
    public void Defaults_UseOnlyFixedPublicSecureEndpoints()
    {
        var defaults = ProductEnvironment.ProductDefaults;

        Assert.Equal("1", defaults["CODEX_BRIDGE_REMOTE_ENABLED"]);
        Assert.Equal("wss", new Uri(defaults["CODEX_BRIDGE_SIGNAL_URL"]).Scheme);
        Assert.Equal("https", new Uri(defaults["CODEX_BRIDGE_REMOTE_APP_URL"]).Scheme);
        Assert.Equal("https", new Uri(defaults["CODEX_BRIDGE_ENTITLEMENT_URL"]).Scheme);
        Assert.All(defaults.Where(item => item.Key.EndsWith("URL", StringComparison.Ordinal)), item =>
        {
            var uri = new Uri(item.Value);
            Assert.Equal("remote.example.invalid", uri.Host);
            Assert.Equal(8443, uri.Port);
        });
    }

    [Fact]
    public void Defaults_ContainOnlyTheExpectedPublicEntitlementKey()
    {
        var json = ProductEnvironment.ProductDefaults["CODEX_BRIDGE_ENTITLEMENT_PUBLIC_KEYS"];
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        Assert.NotNull(keys);
        var key = Assert.Single(keys);
        Assert.Equal("key-20260817-85b8a4e8", key.Key);
        Assert.Equal(122, key.Value.Length);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }
}
