using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CodexBridge.Entitlement.Server;

namespace CodexBridge.Entitlements.Tests;

public sealed class EntitlementServerApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Directory.GetCurrentDirectory(), ".test-state", "entitlement-api", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Api_ProtectsAdminRedeemsRevokesAndLeavesPaymentClosed()
    {
        Directory.CreateDirectory(_directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var adminToken = new string('Z', 32);
        var options = new EntitlementServerOptions(
            Path.Combine(_directory, "state.json"),
            Path.Combine(_directory, "audit.jsonl"),
            "key-1",
            Encode(key.ExportPkcs8PrivateKey()),
            adminToken);
        await using var app = global::Program.BuildApplication([], options);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var unauthorized = await client.PostAsJsonAsync("/api/v1/admin/invites", new
        {
            plan = "pro", durationDays = 30, maximumRedemptions = 1,
            expiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Add("X-CodexBridge-Admin-Token", adminToken);
        var inviteResponse = await client.PostAsJsonAsync("/api/v1/admin/invites", new
        {
            plan = "pro", durationDays = 30, maximumRedemptions = 1,
            expiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        inviteResponse.EnsureSuccessStatusCode();
        var invite = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("X-CodexBridge-Admin-Token");

        var redeemResponse = await client.PostAsJsonAsync("/api/v1/redeem", new
        {
            code = invite.GetProperty("code").GetString(),
            userId = "user-1", hostId = "host-1", deviceId = "device-1",
        });
        redeemResponse.EnsureSuccessStatusCode();
        var credential = await redeemResponse.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Add("X-CodexBridge-Admin-Token", adminToken);
        var revoke = await client.DeleteAsync(
            "/api/v1/admin/licenses/" + credential.GetProperty("licenseId").GetString());
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        client.DefaultRequestHeaders.Remove("X-CodexBridge-Admin-Token");
        var refresh = await client.PostAsJsonAsync("/api/v1/token", new
        {
            licenseId = credential.GetProperty("licenseId").GetString(),
            refreshToken = credential.GetProperty("refreshToken").GetString(),
            hostId = "host-1", deviceId = "device-1",
        });
        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.PostAsJsonAsync(
            "/api/v1/payments", new { userId = "user-1", plan = "pro" })).StatusCode);

        await app.StopAsync();
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
