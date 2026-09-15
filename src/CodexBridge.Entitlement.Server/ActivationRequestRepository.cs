using System.Text;
using System.Text.Json;

namespace CodexBridge.Entitlement.Server;

public sealed record ActivationRequestView(
    string RequestId, string DouyinId, string Email, string Status,
    string? InviteCode, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt,
    DateTimeOffset? InviteExpiresAt, string? RejectReason, DateTimeOffset? EmailSentAt,
    DateTimeOffset? ActivatedAt, string? LicenseId);

public sealed class ActivationRequestRepository : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _auditPath;
    private readonly TimeProvider _clock;
    private State _state;

    public ActivationRequestRepository(EntitlementServerOptions options, TimeProvider clock)
    {
        _path = Path.Combine(Path.GetDirectoryName(options.DataPath)!, "activation-requests.json");
        _auditPath = options.AuditPath;
        _clock = clock;
        _state = File.Exists(_path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOptions) ?? new() : new();
        Persist();
    }

    public ActivationRequestView Submit(string douyinId, string email)
    {
        douyinId = (douyinId ?? string.Empty).Trim();
        email = NormalizeEmail(email);
        if (douyinId.Length is < 1 or > 128) throw new EntitlementException("invalid_douyin_id");
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_state.Items.Any(x => x.Email == email && (x.Status == "pending" || (x.Status == "approved" && x.InviteExpiresAt > now))))
                throw new EntitlementException("activation_request_exists");
            if (_state.Items.Any(x => x.DouyinId.Equals(douyinId, StringComparison.OrdinalIgnoreCase) && x.CreatedAt >= now.AddDays(-7)))
                throw new EntitlementException("activation_request_exists");
            var item = new Stored("req_" + Guid.NewGuid().ToString("N"), douyinId, email, "pending", null, now, null, null, null, null, null, null);
            _state.Items.Add(item); Persist(); Audit("activation_request_created", item.RequestId, email);
            return View(item);
        }
    }

    public IReadOnlyList<ActivationRequestView> List() { lock (_gate) return _state.Items.OrderByDescending(x => x.CreatedAt).Select(View).ToArray(); }

    public ActivationRequestView Approve(string id, Func<string, DateTimeOffset, (string Code, DateTimeOffset ExpiresAt)> createInvite)
    {
        lock (_gate)
        {
            var index = Find(id); var item = _state.Items[index];
            if (item.Status == "approved" || item.Status == "approved_email_failed") return View(item);
            if (item.Status != "pending") throw new EntitlementException("activation_request_not_pending");
            var now = _clock.GetUtcNow(); var invite = createInvite(item.Email, now.AddDays(7));
            item = item with { Status = "approved_email_failed", InviteCode = invite.Code, InviteExpiresAt = invite.ExpiresAt, ReviewedAt = now };
            _state.Items[index] = item; Persist(); Audit("activation_request_approved", id, item.Email); return View(item);
        }
    }

    public ActivationRequestView MarkEmailSent(string id) { lock (_gate) { var i = Find(id); var x = _state.Items[i] with { Status = "approved", EmailSentAt = _clock.GetUtcNow() }; _state.Items[i] = x; Persist(); return View(x); } }
    public ActivationRequestView Reject(string id, string reason) { lock (_gate) { if (string.IsNullOrWhiteSpace(reason)) throw new EntitlementException("reject_reason_required"); var i = Find(id); var x = _state.Items[i]; if (x.Status != "pending") throw new EntitlementException("activation_request_not_pending"); x = x with { Status = "rejected", RejectReason = reason.Trim(), ReviewedAt = _clock.GetUtcNow() }; _state.Items[i] = x; Persist(); Audit("activation_request_rejected", id, x.Email); return View(x); } }

    private int Find(string id) { var i = _state.Items.FindIndex(x => x.RequestId == id); if (i < 0) throw new EntitlementException("activation_request_not_found"); return i; }
    private void Persist() { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var tmp = _path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions), Encoding.UTF8); File.Move(tmp, _path, true); }
    private void Audit(string action, string id, string email) { Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!); File.AppendAllText(_auditPath, JsonSerializer.Serialize(new { at = _clock.GetUtcNow(), action, requestId = id, email }, JsonOptions) + Environment.NewLine, Encoding.UTF8); }
    private static ActivationRequestView View(Stored x) => new(x.RequestId, x.DouyinId, x.Email, x.Status, x.InviteCode, x.CreatedAt, x.ReviewedAt, x.InviteExpiresAt, x.RejectReason, x.EmailSentAt, x.ActivatedAt, x.LicenseId);
    private static string NormalizeEmail(string value) { var e = (value ?? string.Empty).Trim().ToLowerInvariant(); if (!e.Contains('@') || e.Length > 320) throw new EntitlementException("invalid_email"); return e; }
    public void Dispose() { }
    private sealed class State { public List<Stored> Items { get; set; } = []; }
    private sealed record Stored(string RequestId, string DouyinId, string Email, string Status, string? InviteCode, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, DateTimeOffset? InviteExpiresAt, string? RejectReason, DateTimeOffset? EmailSentAt, DateTimeOffset? ActivatedAt, string? LicenseId);
}
