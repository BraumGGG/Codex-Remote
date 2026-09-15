using System.Security.Cryptography;
using System.Text;
using CodexBridge.Entitlement.Server;

public static class Program
{
    public static WebApplication BuildApplication(
        string[] args,
        EntitlementServerOptions? suppliedOptions = null,
        TimeProvider? suppliedTimeProvider = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        var options = suppliedOptions ?? LoadOptions(builder.Configuration);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(suppliedTimeProvider ?? TimeProvider.System);
        builder.Services.AddSingleton<EntitlementRepository>();
        builder.Services.AddSingleton<AccountRepository>();
        builder.Services.AddSingleton<ActivationRequestRepository>();
        builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
        builder.Services.AddSingleton<IPaymentProvider, UnconfiguredPaymentProvider>();
        builder.Services.AddCors(options => options.AddPolicy("website", policy => policy
            .WithOrigins("https://remote.example.invalid", "https://www.remote.example.invalid")
            .AllowAnyHeader()
            .AllowAnyMethod()));
        var app = builder.Build();
        app.UseCors("website");
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/v1/admin")) { await next(); return; }
            var supplied = context.Request.Headers["X-CodexBridge-Admin-Token"].ToString();
            if (!FixedEquals(supplied, options.AdminToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "admin_unauthorized" });
                return;
            }
            await next();
        });
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapPost("/api/v1/redeem", (RedeemRequest request, EntitlementRepository repository) =>
            Execute(() => repository.Redeem(request.Code, request.UserId, request.HostId, request.DeviceId)));
        app.MapPost("/api/v1/token", (RefreshRequest request, EntitlementRepository repository) =>
            Execute(() => new { entitlementToken = repository.Refresh(request.LicenseId, request.RefreshToken, request.HostId, request.DeviceId) }));
        app.MapPost("/api/v1/accounts/register", async (AccountRegisterRequest request, AccountRepository repository, IEmailSender email, CancellationToken ct) =>
            await ExecuteAsync(() => repository.RegisterAsync(request.Email, request.Password, email, ct)));
        app.MapPost("/api/v1/accounts/verify", (AccountVerifyRequest request, AccountRepository repository) =>
            Execute(() => { repository.VerifyEmail(request.Email, request.Code); return new { verified = true }; }));
        app.MapPost("/api/v1/accounts/password-reset/request", async (PasswordResetRequest request, AccountRepository repository, IEmailSender email, CancellationToken ct) =>
            await ExecuteAsync(() => repository.RequestPasswordResetAsync(request.Email, email, ct)));
        app.MapPost("/api/v1/accounts/password-reset/confirm", (PasswordResetConfirmRequest request, AccountRepository repository) =>
            Execute(() => { repository.ResetPassword(request.Email, request.Code, request.NewPassword); return new { reset = true }; }));
        app.MapPost("/api/v1/accounts/login", (AccountLoginRequest request, AccountRepository repository) =>
            Execute(() => repository.Login(request.Email, request.Password)));
        app.MapPost("/api/v1/accounts/profile", (AccountProfileRequest request, AccountRepository accounts, EntitlementRepository entitlements) =>
            Execute(() => accounts.Profile(request.SessionToken, entitlements.List())));
        app.MapPost("/api/v1/accounts/logout", (AccountLogoutRequest request, AccountRepository accounts) =>
            Execute(() => { accounts.Logout(request.SessionToken); return new { loggedOut = true }; }));
        app.MapPost("/api/v1/accounts/activate", (AccountActivateRequest request, AccountRepository accounts, EntitlementRepository entitlements) =>
            Execute(() =>
            {
                var accountId = accounts.RequireAccount(request.SessionToken);
                var credential = entitlements.Redeem(request.Code, accountId, request.HostId, request.DeviceId);
                accounts.RecordActivation(accountId, request.HostId, request.DeviceId, credential.LicenseId);
                return new AccountActivation(accountId, request.Email ?? string.Empty, credential.LicenseId, credential.EntitlementToken, DecodeExpiry(credential.EntitlementToken));
            }));
        app.MapPost("/api/v1/accounts/heartbeat", (HeartbeatRequest request, AccountRepository accounts) =>
            Execute(() => { var accountId = accounts.RequireAccount(request.SessionToken); accounts.Heartbeat(accountId, request.HostId, request.DeviceId, request.Client, request.Version); return new { accepted = true }; }));
        app.MapPost("/api/v1/activation-requests", (ActivationRequestCreate request, ActivationRequestRepository repository) =>
            Execute(() => repository.Submit(request.DouyinId, request.Email)));
        app.MapGet("/api/v1/admin/activation-requests", (ActivationRequestRepository repository) => Results.Ok(repository.List()));
        app.MapPost("/api/v1/admin/activation-requests/{requestId}/approve", async (string requestId, ActivationRequestRepository requests, EntitlementRepository entitlements, IEmailSender email, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                var item = requests.Approve(requestId, (recipient, expiry) =>
                {
                    var invite = entitlements.CreateInvite("pro", 30, 1, expiry, recipient);
                    return (invite.Code, invite.ExpiresAt);
                });
                await email.SendAsync(item.Email, "Codex Remote 激活码申请审核通过", $"您好！\n\n您的 Codex Remote 激活码申请已审核通过。\n\n激活码：{item.InviteCode}\n有效期至：{item.InviteExpiresAt:yyyy-MM-dd HH:mm:ss} UTC\n使用次数：一次\n绑定限制：仅限一台 Windows 电脑\n\n请在 Windows 客户端登录后输入激活码。\n\nCodex Remote\n", ct);
                requests.MarkEmailSent(requestId);
            }));
        app.MapPost("/api/v1/admin/activation-requests/{requestId}/reject", (string requestId, ActivationRequestReject request, ActivationRequestRepository repository) =>
            Execute(() => repository.Reject(requestId, request.Reason)));
        app.MapPost("/api/v1/admin/invites", (InviteRequest request, EntitlementRepository repository) =>
            Execute(() => repository.CreateInvite(request.Plan, request.DurationDays, request.MaximumRedemptions, DateTimeOffset.UtcNow.AddDays(7), request.Email)));
        app.MapPost("/api/v1/admin/invites/email", async (EmailInviteRequest request, EntitlementRepository repository, IEmailSender email, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                var invite = repository.CreateInvite(request.Plan, request.DurationDays, request.MaximumRedemptions, DateTimeOffset.UtcNow.AddDays(7), request.Email);
                await email.SendAsync(request.Email, "Codex Remote Windows 客户端激活码", $"您好！\n\n感谢您体验 Codex Remote。\n\n您的 Windows 客户端激活码\n--------------------------------\n{invite.Code}\n--------------------------------\n\n有效期至：{invite.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC\n使用次数：一次\n绑定限制：仅限一台 Windows 电脑\n\n使用方法：\n1. 安装并打开 Codex Remote Windows 客户端。\n2. 注册并验证您的邮箱。\n3. 在激活页面输入上方激活码。\n\n请勿将此激活码转发给他人。\n\nCodex Remote\n", ct);
            }));
        app.MapPost("/api/v1/admin/grants", (GrantRequest request, EntitlementRepository repository) =>
            Execute(() => repository.Grant(request.UserId, request.HostId, request.DeviceId, request.Plan, request.ExpiresAt)));
        app.MapGet("/api/v1/admin/licenses", (EntitlementRepository repository) => Results.Ok(repository.List()));
        app.MapGet("/api/v1/admin/invites", (EntitlementRepository repository) => Results.Ok(repository.ListInvites()));
        app.MapGet("/api/v1/admin/stats", (AccountRepository repository) => Results.Ok(repository.Stats()));
        app.MapDelete("/api/v1/admin/licenses/{licenseId}", (string licenseId, EntitlementRepository repository) =>
            repository.Revoke(licenseId) ? Results.NoContent() : Results.NotFound(new { error = "license_not_found" }));
        app.MapPost("/api/v1/payments", () => Results.Json(
            new { error = "payment_not_configured" }, statusCode: StatusCodes.Status501NotImplemented));
        app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
        return app;
    }

    public static Task Main(string[] args) => BuildApplication(args).RunAsync();

    private static IResult Execute<T>(Func<T> action)
    {
        try { return Results.Ok(action()); }
        catch (EntitlementException exception)
        {
            var status = exception.ErrorCode switch
            {
                "invite_invalid" or "refresh_unauthorized" => StatusCodes.Status401Unauthorized,
                "license_not_found" => StatusCodes.Status404NotFound,
                "host_limit_reached" or "device_limit_reached" => StatusCodes.Status409Conflict,
                "license_revoked" or "license_expired" or "invite_unavailable" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Json(new { error = exception.ErrorCode }, statusCode: status);
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task> action)
    {
        try { await action(); return Results.Ok(new { accepted = true }); }
        catch (EntitlementException exception) { return Results.Json(new { error = exception.ErrorCode }, statusCode: StatusCodes.Status400BadRequest); }
    }

    private static DateTimeOffset DecodeExpiry(string token) => DateTimeOffset.UtcNow.AddMinutes(30);

    private static EntitlementServerOptions LoadOptions(IConfiguration configuration)
    {
        string Required(string name, int minimumLength = 1)
        {
            var value = configuration[name];
            if (string.IsNullOrWhiteSpace(value) || value.Length < minimumLength)
                throw new InvalidOperationException($"{name} is required.");
            return value;
        }
        var dataDirectory = Path.GetFullPath(Required("CODEX_BRIDGE_ENTITLEMENT_DATA_DIRECTORY"));
        return new EntitlementServerOptions(
            Path.Combine(dataDirectory, "entitlements.json"),
            Path.Combine(dataDirectory, "audit.jsonl"),
            Required("CODEX_BRIDGE_ENTITLEMENT_KEY_ID"),
            Required("CODEX_BRIDGE_ENTITLEMENT_PRIVATE_KEY"),
            Required("CODEX_BRIDGE_ENTITLEMENT_ADMIN_TOKEN", 32));
    }

    private static bool FixedEquals(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    public sealed record RedeemRequest(string Code, string UserId, string HostId, string DeviceId);
    public sealed record RefreshRequest(string LicenseId, string RefreshToken, string HostId, string DeviceId);
    public sealed record InviteRequest(string Plan, int DurationDays, int MaximumRedemptions, DateTimeOffset ExpiresAt, string? Email = null);
    public sealed record EmailInviteRequest(string Email, string Plan, int DurationDays, int MaximumRedemptions, DateTimeOffset ExpiresAt);
    public sealed record GrantRequest(string UserId, string HostId, string DeviceId, string Plan, DateTimeOffset ExpiresAt);
    public sealed record AccountRegisterRequest(string Email, string Password);
    public sealed record AccountVerifyRequest(string Email, string Code);
    public sealed record AccountLoginRequest(string Email, string Password);
    public sealed record AccountProfileRequest(string SessionToken);
    public sealed record AccountLogoutRequest(string SessionToken);
    public sealed record PasswordResetRequest(string Email);
    public sealed record PasswordResetConfirmRequest(string Email, string Code, string NewPassword);
    public sealed record AccountActivateRequest(string SessionToken, string Code, string HostId, string DeviceId, string? Email);
    public sealed record HeartbeatRequest(string SessionToken, string HostId, string DeviceId, string Client, string Version);
    public sealed record ActivationRequestCreate(string DouyinId, string Email);
    public sealed record ActivationRequestReject(string Reason);
}
