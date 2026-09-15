using System.Security.Cryptography;
using CodexBridge.Entitlement.Server;

namespace CodexBridge.Entitlements.Tests;

public sealed class AccountRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), ".test-state", "accounts", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegisterVerifyLoginAndHeartbeat_AreRecordedWithoutPlainSecrets()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        using var entitlements = new EntitlementRepository(Options(key), clock);
        using var accounts = new AccountRepository(Options(key), clock);
        var mail = new FakeMailSender();
        await accounts.RegisterAsync("Test@QQ.com", "correct horse battery", mail);
        Assert.Contains("test@qq.com", mail.Recipient);
        var code = mail.Body.Split('：')[1].Split('，')[0];
        accounts.VerifyEmail("test@qq.com", code);
        var session = accounts.Login("test@qq.com", "correct horse battery");
        var invite = entitlements.CreateInvite("free", 30, 1, clock.GetUtcNow().AddDays(1));
        var license = entitlements.Redeem(invite.Code, session.AccountId, "host-1", "device-1");
        accounts.RecordActivation(session.AccountId, "host-1", "device-1", license.LicenseId);
        accounts.Heartbeat(session.AccountId, "host-1", "device-1", "desktop", "0.2.0");
        Assert.Equal(1, accounts.Stats().RegisteredUsers);
        Assert.Equal(1, accounts.Stats().OnlineComputers);
        Assert.DoesNotContain("correct horse battery", File.ReadAllText(Path.Combine(_directory, "accounts.json")));
    }

    [Fact]
    public async Task Profile_ReturnsOnlyCurrentAccountLicenses_AndLogoutInvalidatesSession()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        using var entitlements = new EntitlementRepository(Options(key), clock);
        using var accounts = new AccountRepository(Options(key), clock);
        var mail = new FakeMailSender();
        await accounts.RegisterAsync("profile@qq.com", "correct horse battery", mail);
        accounts.VerifyEmail("profile@qq.com", mail.Body.Split('：')[1].Split('，')[0]);
        var session = accounts.Login("profile@qq.com", "correct horse battery");
        var invite = entitlements.CreateInvite("pro", 30, 1, clock.GetUtcNow().AddDays(1));
        var license = entitlements.Redeem(invite.Code, session.AccountId, "host-1", "device-1");
        var profile = accounts.Profile(session.SessionToken, entitlements.List());
        Assert.Equal("profile@qq.com", profile.Email);
        Assert.Single(profile.Licenses);
        Assert.Equal(license.LicenseId, profile.Licenses[0].LicenseId);
        accounts.Logout(session.SessionToken);
        Assert.Equal("session_invalid", Assert.Throws<EntitlementException>(() => accounts.Profile(session.SessionToken, entitlements.List())).ErrorCode);
    }

    private EntitlementServerOptions Options(ECDsa key) => new(Path.Combine(_directory, "state.json"), Path.Combine(_directory, "audit.jsonl"), "key", Convert.ToBase64String(key.ExportPkcs8PrivateKey()).TrimEnd('=').Replace('+', '-').Replace('/', '_'), new string('A', 32));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class FakeMailSender : IEmailSender { public string Recipient = ""; public string Body = ""; public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default) { Recipient = recipient; Body = body; return Task.CompletedTask; } }
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
