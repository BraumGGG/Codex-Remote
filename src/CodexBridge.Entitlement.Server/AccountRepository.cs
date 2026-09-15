using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace CodexBridge.Entitlement.Server;

public sealed record AccountSession(string SessionToken, string AccountId, string Email, DateTimeOffset ExpiresAt);
public sealed record AccountProfile(string AccountId, string Email, bool EmailVerified, DateTimeOffset CreatedAt, IReadOnlyList<AccountLicense> Licenses);
public sealed record AccountLicense(string LicenseId, string HostId, string DeviceId, string Plan, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Revoked, DateTimeOffset? LastSeenAt);
public sealed record AccountActivation(string AccountId, string Email, string LicenseId, string EntitlementToken, DateTimeOffset ExpiresAt);
public sealed record UsageStats(int RegisteredUsers, int ActivatedComputers, int OnlineComputers, int OnlinePhones, int ActiveToday, int ActiveLast7Days);

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
    {
        var host = configuration["CODEX_BRIDGE_SMTP_HOST"];
        var user = configuration["CODEX_BRIDGE_SMTP_USER"];
        var password = configuration["CODEX_BRIDGE_SMTP_PASSWORD"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Email delivery is not configured; message for {Recipient} was not sent.", recipient);
            return;
        }
        var port = int.TryParse(configuration["CODEX_BRIDGE_SMTP_PORT"], out var configuredPort) ? configuredPort : 465;
        cancellationToken.ThrowIfCancellationRequested();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken);
        using var reader = new StreamReader(ssl, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(ssl, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await ExpectAsync(reader, "220", cancellationToken);
        await CommandAsync(writer, reader, "EHLO codex-remote", "250", cancellationToken);
        await CommandAsync(writer, reader, "AUTH LOGIN", "334", cancellationToken);
        await CommandAsync(writer, reader, Convert.ToBase64String(Encoding.UTF8.GetBytes(user)), "334", cancellationToken);
        await CommandAsync(writer, reader, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), "235", cancellationToken);
        await CommandAsync(writer, reader, $"MAIL FROM:<{user}>", "250", cancellationToken);
        await CommandAsync(writer, reader, $"RCPT TO:<{recipient}>", "250", cancellationToken);
        await CommandAsync(writer, reader, "DATA", "354", cancellationToken);
        await writer.WriteLineAsync($"From: {EncodeHeader("Codex Remote")} <{user}>");
        await writer.WriteLineAsync($"Sender: {user}");
        await writer.WriteLineAsync($"Reply-To: {user}");
        await writer.WriteLineAsync($"To: {recipient}");
        await writer.WriteLineAsync($"Subject: {EncodeHeader(subject)}");
        await writer.WriteLineAsync("Content-Type: text/plain; charset=utf-8");
        await writer.WriteLineAsync("Content-Transfer-Encoding: 8bit");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync(body.Replace("\n", "\r\n"));
        await writer.WriteLineAsync(".");
        await ExpectAsync(reader, "250", cancellationToken);
        await CommandAsync(writer, reader, "QUIT", "221", cancellationToken);
    }

    private static async Task CommandAsync(StreamWriter writer, StreamReader reader, string command, string expected, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(command);
        await ExpectAsync(reader, expected, cancellationToken);
    }

    private static async Task ExpectAsync(StreamReader reader, string expected, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("SMTP 服务提前断开连接。");
        if (!line.StartsWith(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"SMTP 服务返回异常：{expected} expected.");
        while (line.Length >= 4 && line[3] == '-')
        {
            line = await reader.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("SMTP 服务提前断开连接。");
        }
    }

    private static string EncodeHeader(string value) => $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
}

public sealed class AccountRepository : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private StoredState _state;

    public AccountRepository(EntitlementServerOptions options, TimeProvider clock)
    {
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DataPath))!, "accounts.json");
        _clock = clock;
        _state = Load();
        if (!File.Exists(_path)) Persist();
    }

    public async Task RegisterAsync(string email, string password, IEmailSender sender, CancellationToken cancellationToken = default)
    {
        email = NormalizeEmail(email);
        ValidatePassword(password);
        string code;
        lock (_gate)
        {
            var existing = _state.Accounts.FirstOrDefault(a => a.Email == email);
            if (existing is not null && existing.EmailVerified) throw new EntitlementException("account_exists");
            code = NumericCode();
            if (existing is null)
            {
                _state.Accounts.Add(new StoredAccount("acct_" + Guid.NewGuid().ToString("N"), email, HashPassword(password), false, _clock.GetUtcNow()));
            }
            else
            {
                var index = _state.Accounts.IndexOf(existing);
                _state.Accounts[index] = existing with { PasswordHash = HashPassword(password) };
            }
            _state.Verifications.Add(new Verification(email, Hash(code), _clock.GetUtcNow().AddMinutes(15)));
            Persist();
        }
        await sender.SendAsync(email, "Codex Remote 邮箱验证", $"您的验证码是：{code}，15 分钟内有效。", cancellationToken);
    }

    public void VerifyEmail(string email, string code)
    {
        email = NormalizeEmail(email);
        lock (_gate)
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Email == email) ?? throw new EntitlementException("account_not_found");
            var item = _state.Verifications.LastOrDefault(v => v.Email == email && v.ExpiresAt > _clock.GetUtcNow());
            if (item is null || !FixedHashEquals(item.CodeHash, code)) throw new EntitlementException("verification_invalid");
            var index = _state.Accounts.IndexOf(account);
            _state.Accounts[index] = account with { EmailVerified = true };
            Persist();
        }
    }

    public async Task RequestPasswordResetAsync(string email, IEmailSender sender, CancellationToken cancellationToken = default)
    {
        email = NormalizeEmail(email);
        string? code = null;
        lock (_gate)
        {
            if (_state.Accounts.Any(a => a.Email == email))
            {
                code = NumericCode();
                _state.PasswordResets.Add(new Verification(email, Hash(code), _clock.GetUtcNow().AddMinutes(15)));
                Persist();
            }
        }
        if (code is not null) await sender.SendAsync(email, "Codex Remote 密码重置", $"您的密码重置验证码是：{code}，15 分钟内有效。", cancellationToken);
    }

    public void ResetPassword(string email, string code, string newPassword)
    {
        email = NormalizeEmail(email);
        ValidatePassword(newPassword);
        lock (_gate)
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Email == email) ?? throw new EntitlementException("account_not_found");
            var item = _state.PasswordResets.LastOrDefault(v => v.Email == email && v.ExpiresAt > _clock.GetUtcNow());
            if (item is null || !FixedHashEquals(item.CodeHash, code)) throw new EntitlementException("reset_invalid");
            var index = _state.Accounts.IndexOf(account);
            _state.Accounts[index] = account with { PasswordHash = HashPassword(newPassword) };
            Persist();
        }
    }

    public AccountSession Login(string email, string password)
    {
        email = NormalizeEmail(email);
        lock (_gate)
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Email == email);
            if (account is null || !VerifyPassword(password, account.PasswordHash)) throw new EntitlementException("login_invalid");
            if (!account.EmailVerified) throw new EntitlementException("email_unverified");
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _state.Sessions.Add(new Session(token, account.AccountId, _clock.GetUtcNow().AddDays(30)));
            Persist();
            return new AccountSession(token, account.AccountId, account.Email, _clock.GetUtcNow().AddDays(30));
        }
    }

    public string RequireAccount(string token)
    {
        lock (_gate)
        {
            var session = _state.Sessions.FirstOrDefault(s => s.Token == token && s.ExpiresAt > _clock.GetUtcNow())
                ?? throw new EntitlementException("session_invalid");
            return session.AccountId;
        }
    }

    public AccountProfile Profile(string sessionToken, IReadOnlyList<LicenseSummary> licenses)
    {
        lock (_gate)
        {
            var session = _state.Sessions.FirstOrDefault(s => s.Token == sessionToken && s.ExpiresAt > _clock.GetUtcNow())
                ?? throw new EntitlementException("session_invalid");
            var account = _state.Accounts.FirstOrDefault(a => a.AccountId == session.AccountId)
                ?? throw new EntitlementException("account_not_found");
            var result = licenses.Where(l => l.UserId == account.AccountId).Select(l => new AccountLicense(
                l.LicenseId, l.HostId, l.DeviceId, l.Plan, l.CreatedAt, l.ExpiresAt, l.Revoked,
                _state.Heartbeats.Where(h => h.HostId == l.HostId && h.DeviceId == l.DeviceId).Select(h => (DateTimeOffset?)h.LastSeenAt).Max())).ToArray();
            return new AccountProfile(account.AccountId, account.Email, account.EmailVerified, account.CreatedAt, result);
        }
    }

    public void Logout(string sessionToken)
    {
        lock (_gate)
        {
            var removed = _state.Sessions.RemoveAll(s => s.Token == sessionToken);
            if (removed > 0) Persist();
        }
    }

    public void RecordActivation(string accountId, string hostId, string deviceId, string licenseId)
    {
        lock (_gate)
        {
            if (_state.Activations.Any(a => a.HostId == hostId && a.DeviceId == deviceId)) return;
            _state.Activations.Add(new Activation(accountId, hostId, deviceId, licenseId, _clock.GetUtcNow()));
            Persist();
        }
    }

    public void Heartbeat(string accountId, string hostId, string deviceId, string client, string version)
    {
        lock (_gate)
        {
            var index = _state.Heartbeats.FindIndex(h => h.AccountId == accountId && h.HostId == hostId && h.DeviceId == deviceId && h.Client == client);
            var item = new HeartbeatData(accountId, hostId, deviceId, client, version, _clock.GetUtcNow());
            if (index < 0) _state.Heartbeats.Add(item); else _state.Heartbeats[index] = item;
            Persist();
        }
    }

    public UsageStats Stats()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var active = _state.Heartbeats.Where(h => h.LastSeenAt >= now.AddMinutes(-5)).ToArray();
            var sinceToday = now.Date;
            return new UsageStats(_state.Accounts.Count, _state.Activations.Select(a => a.HostId + "|" + a.DeviceId).Distinct().Count(), active.Count(h => h.Client == "desktop"), active.Count(h => h.Client == "phone"), _state.Heartbeats.Count(h => h.LastSeenAt.Date == sinceToday), _state.Heartbeats.Count(h => h.LastSeenAt >= now.AddDays(-7)));
        }
    }

    private StoredState Load() => File.Exists(_path) ? JsonSerializer.Deserialize<StoredState>(File.ReadAllText(_path), JsonOptions) ?? new() : new();
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions), Encoding.UTF8);
        File.Move(temp, _path, true);
    }
    private static string NormalizeEmail(string value) { var email = (value ?? string.Empty).Trim().ToLowerInvariant(); if (!email.Contains('@') || email.Length > 320) throw new EntitlementException("invalid_email"); return email; }
    private static void ValidatePassword(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length < 8 || value.Length > 128) throw new EntitlementException("invalid_password"); }
    private static string NumericCode() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashPassword(string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32); return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash); }
    private static bool VerifyPassword(string password, string stored) { try { var p = stored.Split('.'); var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[0]), 120_000, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(p[1])); } catch { return false; } }
    private static bool FixedHashEquals(string expected, string value) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    public void Dispose() { }
    private sealed class StoredState { public List<StoredAccount> Accounts { get; set; } = []; public List<Verification> Verifications { get; set; } = []; public List<Verification> PasswordResets { get; set; } = []; public List<Session> Sessions { get; set; } = []; public List<Activation> Activations { get; set; } = []; public List<HeartbeatData> Heartbeats { get; set; } = []; }
    private sealed record StoredAccount(string AccountId, string Email, string PasswordHash, bool EmailVerified, DateTimeOffset CreatedAt);
    private sealed record Verification(string Email, string CodeHash, DateTimeOffset ExpiresAt);
    private sealed record Session(string Token, string AccountId, DateTimeOffset ExpiresAt);
    private sealed record Activation(string AccountId, string HostId, string DeviceId, string LicenseId, DateTimeOffset ActivatedAt);
    private sealed record HeartbeatData(string AccountId, string HostId, string DeviceId, string Client, string Version, DateTimeOffset LastSeenAt);
}
