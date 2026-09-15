using CodexBridge.Entitlement.Server;

namespace CodexBridge.Entitlements.Tests;

public sealed class ActivationRequestRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Directory.GetCurrentDirectory(), ".test-state", "requests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Submit_requires_fields_and_deduplicates_pending_email()
    {
        using var repo = Create();
        var item = repo.Submit("BraumG", " FAN@QQ.COM " );
        Assert.Equal("pending", item.Status);
        Assert.Equal("fan@qq.com", item.Email);
        Assert.Equal("activation_request_exists", Assert.Throws<EntitlementException>(() => repo.Submit("other", "fan@qq.com")).ErrorCode);
        Assert.Equal("invalid_douyin_id", Assert.Throws<EntitlementException>(() => repo.Submit(" ", "new@qq.com")).ErrorCode);
    }

    [Fact]
    public void Approve_creates_one_code_and_is_idempotent()
    {
        using var repo = Create();
        var request = repo.Submit("BraumG", "fan@qq.com");
        var calls = 0;
        var first = repo.Approve(request.RequestId, (email, expiry) => { calls++; return ("CDB-test", expiry); });
        var second = repo.Approve(request.RequestId, (email, expiry) => { calls++; return ("CDB-other", expiry); });
        Assert.Equal(1, calls);
        Assert.Equal("CDB-test", first.InviteCode);
        Assert.Equal(first.InviteCode, second.InviteCode);
        Assert.Equal("approved_email_failed", first.Status);
        Assert.Equal("approved_email_failed", second.Status);
    }

    [Fact]
    public void Reject_requires_reason()
    {
        using var repo = Create();
        var request = repo.Submit("BraumG", "fan@qq.com");
        Assert.Equal("reject_reason_required", Assert.Throws<EntitlementException>(() => repo.Reject(request.RequestId, " " )).ErrorCode);
        var rejected = repo.Reject(request.RequestId, "不在群内");
        Assert.Equal("rejected", rejected.Status);
        Assert.Equal("不在群内", rejected.RejectReason);
    }

    private ActivationRequestRepository Create()
    {
        Directory.CreateDirectory(_dir);
        return new ActivationRequestRepository(new EntitlementServerOptions(Path.Combine(_dir, "entitlements.json"), Path.Combine(_dir, "audit.jsonl"), "key", "", new string('A', 32)), TimeProvider.System);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
