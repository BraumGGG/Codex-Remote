using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexBridge.Entitlements;

namespace CodexBridge.Entitlement.Server;

public sealed record EntitlementServerOptions(
    string DataPath,
    string AuditPath,
    string KeyId,
    string PrivateKeyPkcs8,
    string AdminToken,
    int MaximumHostsPerUser = 1,
    int MaximumDevicesPerUser = 3);

public sealed record InviteCodeResult(string Code, string Plan, DateTimeOffset ExpiresAt, int MaximumRedemptions);
public sealed record InviteSummary(string Code, string? RecipientEmail, string Plan, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int Redemptions, int MaximumRedemptions, bool Revoked, string? RedeemedByUserId, DateTimeOffset? RedeemedAt);
public sealed record LicenseCredential(string LicenseId, string RefreshToken, string EntitlementToken);
public sealed record LicenseSummary(
    string LicenseId,
    string UserId,
    string HostId,
    string DeviceId,
    string Plan,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Revoked);

public sealed class EntitlementException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class EntitlementRepository : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly EntitlementServerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ECDsa _signingKey;
    private readonly EntitlementTokenSigner _signer;
    private readonly byte[] _inviteEncryptionKey;
    private StoredState _state;

    public EntitlementRepository(EntitlementServerOptions options, TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (_options.MaximumHostsPerUser is < 1 or > 20 || _options.MaximumDevicesPerUser is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options));
        _signingKey = ECDsa.Create();
        var privateKey = Decode(_options.PrivateKeyPkcs8, 512);
        try { _signingKey.ImportPkcs8PrivateKey(privateKey, out var read); if (read != privateKey.Length) throw new InvalidDataException("Signing key has trailing bytes."); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        _signer = new EntitlementTokenSigner(_signingKey, _options.KeyId);
        _inviteEncryptionKey = SHA256.HashData(privateKeyForEncryption(_options.PrivateKeyPkcs8));
        var stateExists = File.Exists(_options.DataPath);
        _state = Load(_options.DataPath);
        if (!stateExists) Persist();
    }

    public InviteCodeResult CreateInvite(
        string plan,
        int durationDays,
        int maximumRedemptions,
        DateTimeOffset expiresAt,
        string? recipientEmail = null)
    {
        ValidatePlan(plan);
        if (durationDays is < 1 or > 366 || maximumRedemptions is < 1 or > 1000 || expiresAt <= _timeProvider.GetUtcNow())
            throw new EntitlementException("invalid_invite");
        var code = "CDB-" + Encode(RandomNumberGenerator.GetBytes(18));
        var invite = new StoredInvite(
            Hash(code), plan, durationDays, maximumRedemptions, 0, expiresAt, false,
            EncryptInviteCode(code), recipientEmail, _timeProvider.GetUtcNow(), null, null);
        lock (_gate)
        {
            _state.Invites.Add(invite);
            Persist();
            Audit("invite_created", null, null);
        }
        return new InviteCodeResult(code, plan, expiresAt, maximumRedemptions);
    }

    public LicenseCredential Redeem(string code, string userId, string hostId, string deviceId)
    {
        ValidateBinding(userId, hostId, deviceId);
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var codeHash = Hash((code ?? string.Empty).Trim());
            var index = _state.Invites.FindIndex(item => string.Equals(item.CodeHash, codeHash, StringComparison.Ordinal));
            if (index < 0) throw new EntitlementException("invite_invalid");
            var invite = _state.Invites[index];
            if (invite.Revoked || now >= invite.ExpiresAt || invite.Redemptions >= invite.MaximumRedemptions)
                throw new EntitlementException("invite_unavailable");
            EnsureCapacity(userId, hostId, deviceId, now);
            var credential = CreateLicense(userId, hostId, deviceId, invite.Plan, now.AddDays(invite.DurationDays));
            _state.Invites[index] = invite with { Redemptions = invite.Redemptions + 1, RedeemedByUserId = userId, RedeemedAt = now };
            Persist();
            Audit("invite_redeemed", credential.LicenseId, userId);
            return credential;
        }
    }

    public LicenseCredential Grant(
        string userId,
        string hostId,
        string deviceId,
        string plan,
        DateTimeOffset expiresAt)
    {
        ValidateBinding(userId, hostId, deviceId);
        ValidatePlan(plan);
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (expiresAt <= now || expiresAt > now.AddDays(366)) throw new EntitlementException("invalid_expiry");
            EnsureCapacity(userId, hostId, deviceId, now);
            var credential = CreateLicense(userId, hostId, deviceId, plan, expiresAt);
            Persist();
            Audit("license_granted", credential.LicenseId, userId);
            return credential;
        }
    }

    public string Refresh(string licenseId, string refreshToken, string hostId, string deviceId)
    {
        lock (_gate)
        {
            var license = _state.Licenses.FirstOrDefault(item => item.LicenseId == licenseId)
                ?? throw new EntitlementException("license_not_found");
            if (!string.Equals(license.HostId, hostId, StringComparison.Ordinal) ||
                !string.Equals(license.DeviceId, deviceId, StringComparison.Ordinal))
                throw new EntitlementException("license_binding_mismatch");
            if (!FixedHashEquals(license.RefreshTokenHash, refreshToken))
                throw new EntitlementException("refresh_unauthorized");
            var now = _timeProvider.GetUtcNow();
            if (license.Revoked) throw new EntitlementException("license_revoked");
            if (now >= license.ExpiresAt) throw new EntitlementException("license_expired");
            Audit("token_refreshed", license.LicenseId, license.UserId);
            return IssueToken(license, now);
        }
    }

    public bool Revoke(string licenseId)
    {
        lock (_gate)
        {
            var index = _state.Licenses.FindIndex(item => item.LicenseId == licenseId);
            if (index < 0) return false;
            var license = _state.Licenses[index];
            if (!license.Revoked)
            {
                _state.Licenses[index] = license with { Revoked = true };
                Persist();
                Audit("license_revoked", license.LicenseId, license.UserId);
            }
            return true;
        }
    }

    public IReadOnlyList<LicenseSummary> List()
    {
        lock (_gate)
        {
            return _state.Licenses.Select(item => new LicenseSummary(
                item.LicenseId, item.UserId, item.HostId, item.DeviceId,
                item.Plan, item.CreatedAt, item.ExpiresAt, item.Revoked)).ToArray();
        }
    }

    public IReadOnlyList<InviteSummary> ListInvites()
    {
        lock (_gate)
        {
            return _state.Invites.Select(item => new InviteSummary(
                DecryptInviteCode(item.EncryptedCode), item.RecipientEmail, item.Plan,
                item.CreatedAt, item.ExpiresAt, item.Redemptions, item.MaximumRedemptions,
                item.Revoked, item.RedeemedByUserId, item.RedeemedAt)).ToArray();
        }
    }

    private LicenseCredential CreateLicense(
        string userId,
        string hostId,
        string deviceId,
        string plan,
        DateTimeOffset expiresAt)
    {
        var refreshToken = Encode(RandomNumberGenerator.GetBytes(32));
        var license = new StoredLicense(
            "lic_" + Encode(RandomNumberGenerator.GetBytes(16)),
            userId,
            hostId,
            deviceId,
            plan,
            Hash(refreshToken),
            expiresAt,
            false,
            _timeProvider.GetUtcNow());
        _state.Licenses.Add(license);
        return new LicenseCredential(license.LicenseId, refreshToken, IssueToken(license, _timeProvider.GetUtcNow()));
    }

    private string IssueToken(StoredLicense license, DateTimeOffset now)
    {
        var tokenExpires = Min(now.AddMinutes(30), license.ExpiresAt);
        var refreshAfter = Min(now.AddMinutes(15), tokenExpires);
        var offlineUntil = Min(tokenExpires.AddHours(72), license.ExpiresAt);
        return _signer.Sign(new EntitlementGrant(
            1, _options.KeyId, license.LicenseId, license.UserId, license.HostId,
            license.DeviceId, license.Plan, now, refreshAfter, tokenExpires, offlineUntil));
    }

    private void EnsureCapacity(string userId, string hostId, string deviceId, DateTimeOffset now)
    {
        var active = _state.Licenses.Where(item => item.UserId == userId && !item.Revoked && item.ExpiresAt > now).ToArray();
        if (!active.Any(item => item.HostId == hostId) && active.Select(item => item.HostId).Distinct().Count() >= _options.MaximumHostsPerUser)
            throw new EntitlementException("host_limit_reached");
        if (!active.Any(item => item.DeviceId == deviceId) && active.Select(item => item.DeviceId).Distinct().Count() >= _options.MaximumDevicesPerUser)
            throw new EntitlementException("device_limit_reached");
    }

    private void Persist()
    {
        var path = Path.GetFullPath(_options.DataPath);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Entitlement data path has no parent.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_state, JsonOptions));
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void Audit(string action, string? licenseId, string? userId)
    {
        var path = Path.GetFullPath(_options.AuditPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(new { at = _timeProvider.GetUtcNow(), action, licenseId, userId }, JsonOptions);
        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
    }

    private static StoredState Load(string path)
    {
        if (!File.Exists(path)) return new StoredState(1, [], []);
        var state = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Entitlement state is empty.");
        if (state.Version != 1) throw new InvalidDataException("Unsupported entitlement state version.");
        return state;
    }

    private static void ValidatePlan(string plan)
    {
        if (plan is not ("free" or "pro")) throw new EntitlementException("invalid_plan");
    }

    private static void ValidateBinding(params string[] values)
    {
        if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
                                value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            throw new EntitlementException("invalid_binding");
    }

    private string EncryptInviteCode(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[value.Length * 4 + 32];
        var tag = new byte[16];
        using var aes = new AesGcm(_inviteEncryptionKey, 16);
        var plain = Encoding.UTF8.GetBytes(value);
        cipher = new byte[plain.Length];
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    private string DecryptInviteCode(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return "（历史激活码不可显示）";
        try
        {
            var data = Convert.FromBase64String(encrypted);
            var nonce = data[..12]; var tag = data[12..28]; var cipher = data[28..]; var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_inviteEncryptionKey, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return "（历史激活码不可显示）"; }
    }

    private static byte[] privateKeyForEncryption(string value) => Decode(value, 512);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedHashEquals(string expectedHash, string value)
    {
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var expected = Convert.FromHexString(expectedHash);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value, int maximum)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        text += (text.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Invalid key encoding.") };
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length > maximum) throw new InvalidDataException("Key is too large.");
        return bytes;
    }

    public void Dispose() => _signingKey.Dispose();

    private sealed record StoredState(int Version, List<StoredInvite> Invites, List<StoredLicense> Licenses);
    private sealed record StoredInvite(
        string CodeHash, string Plan, int DurationDays, int MaximumRedemptions,
        int Redemptions, DateTimeOffset ExpiresAt, bool Revoked,
        string? EncryptedCode = null, string? RecipientEmail = null,
        DateTimeOffset CreatedAt = default, string? RedeemedByUserId = null,
        DateTimeOffset? RedeemedAt = null);
    private sealed record StoredLicense(
        string LicenseId, string UserId, string HostId, string DeviceId, string Plan,
        string RefreshTokenHash, DateTimeOffset ExpiresAt, bool Revoked, DateTimeOffset CreatedAt);
}

public interface IPaymentProvider
{
    Task<string> CreatePaymentAsync(string userId, string plan, CancellationToken cancellationToken);
}

public sealed class UnconfiguredPaymentProvider : IPaymentProvider
{
    public Task<string> CreatePaymentAsync(string userId, string plan, CancellationToken cancellationToken) =>
        throw new EntitlementException("payment_not_configured");
}
