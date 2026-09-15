using CodexBridge.Host.Auth;
using CodexBridge.Host.Contracts;
using CodexBridge.Host.Services;
using CodexBridge.Core;
using CodexBridge.Windows;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using CodexBridge.Entitlements;

namespace CodexBridge.Host;

public static class HostApplication
{
    public static WebApplication Build(
        string[] args,
        HostOptions? hostOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        var options = hostOptions ?? HostOptions.CreateDefault();
        options.EnsureLoopbackOnly();
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ManagementAccessGuard>();
        var configurationStore = new BridgeConfigurationStore(options.ConfigurationPath);
        var configuration = configurationStore.LoadOrCreate();
        var targetPolicy = new TargetPolicy(configuration.Projects);
        builder.Services.AddSingleton(configurationStore);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(targetPolicy);
        builder.Services.AddSingleton<IProjectDiscovery>(_ =>
            new SqliteProjectDiscovery(options.StateDatabasePath));
        builder.Services.AddSingleton<IThreadCatalog>(serviceProvider =>
            new SqliteThreadCatalog(
                options.StateDatabasePath,
                serviceProvider.GetRequiredService<TargetPolicy>()));
        builder.Services.AddSingleton<IConversationReader, RolloutConversationReader>();
        builder.Services.AddSingleton<IRolloutIndexStore>(_ =>
            new RolloutIndexStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexBridge", "indexes")));
        builder.Services.AddSingleton<PagingCursorCodec>();
        builder.Services.AddSingleton<TextFileAttachmentService>();
        builder.Services.AddSingleton<WorkspaceQueryService>();
        builder.Services.AddSingleton<ImageAttachmentService>();
        builder.Services.AddSingleton<DesktopStatusService>();
        builder.Services.AddSingleton<IDesktopStatusProbe>(serviceProvider =>
            serviceProvider.GetRequiredService<DesktopStatusService>());
        builder.Services.AddSingleton<ConversationStreamService>();
        builder.Services.AddSingleton<StreamTicketService>();
        builder.Services.AddSingleton<ICodexDesktopSender>(serviceProvider =>
            new UiAutomationCodexDesktopSender(
                serviceProvider.GetRequiredService<IThreadCatalog>(),
                serviceProvider.GetRequiredService<TargetPolicy>()));
        builder.Services.AddSingleton<DesktopCommandQueue>();
        builder.Services.AddSingleton(serviceProvider =>
            new AuditLog(options.AuditLogPath, serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<MessageSubmissionService>();
        builder.Services.AddSingleton<DeviceManagementService>();
        var remoteOptions = RemoteAccessOptions.FromEnvironment();
        builder.Services.AddSingleton(remoteOptions);
        builder.Services.AddSingleton<BridgeDiagnosticsService>();
        if (remoteOptions.Enabled)
        {
            builder.Services.AddSingleton(serviceProvider => new RemoteIdentityStore(remoteOptions.IdentityPath));
            builder.Services.AddSingleton(serviceProvider => new EntitlementTokenVerifier(
                remoteOptions.EntitlementPublicKeys,
                serviceProvider.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton(serviceProvider => new LocalEntitlementStore(
                remoteOptions.EntitlementTokenPath,
                serviceProvider.GetRequiredService<EntitlementTokenVerifier>()));
            builder.Services.AddSingleton(serviceProvider => new EntitlementRefreshCredentialStore(
                remoteOptions.EntitlementCredentialPath));
            builder.Services.AddSingleton<IEntitlementCloudClient>(_ =>
            {
                if (remoteOptions.EntitlementServiceUri is null) return new DisabledEntitlementCloudClient();
                return new EntitlementCloudClient(new HttpClient
                {
                    BaseAddress = remoteOptions.EntitlementServiceUri,
                    Timeout = TimeSpan.FromSeconds(10),
                });
            });
            builder.Services.AddSingleton<RemoteEntitlementService>();
            builder.Services.AddSingleton<IRemoteCapabilityResolver>(serviceProvider =>
                serviceProvider.GetRequiredService<RemoteEntitlementService>());
            builder.Services.AddSingleton<RemotePairingService>();
            builder.Services.AddSingleton(serviceProvider => new RemoteDeviceStore(
                remoteOptions.DevicePath,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<IRemoteCapabilityResolver>()));
            builder.Services.AddSingleton<RemoteRoutingTicketIssuer>();
            builder.Services.AddSingleton(serviceProvider => new RemoteCommandReceiptStore(
                remoteOptions.ReceiptPath, serviceProvider.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<RemoteSessionFactory>();
            builder.Services.AddSingleton<RemoteSessionConcurrencyGate>();
            builder.Services.AddSingleton<RemoteTelemetryReporter>();
            builder.Services.AddSingleton<RemoteAccessHostedService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RemoteAccessHostedService>());
            builder.Services.AddHostedService<EntitlementRefreshHostedService>();
        }
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        UseManagementAuthentication(app);
        MapApi(app);
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath!, "index.html"));
        });
        return app;
    }

    private static void MapApi(WebApplication app)
    {
        app.MapGet("/api/status", (DesktopStatusService desktop) =>
            Results.Ok(new StatusResponse(
                typeof(HostApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                desktop.IsOnline())));
        app.MapGet("/api/management/devices", (DeviceManagementService devices) =>
            Results.Ok(devices.List()));
        app.MapGet("/api/management/diagnostics", (BridgeDiagnosticsService diagnostics) =>
            Results.Ok(diagnostics.Capture()));
        app.MapGet("/api/management/projects", async (WorkspaceQueryService workspace, CancellationToken cancellationToken) =>
            Results.Ok(await workspace.GetProjectsAsync(cancellationToken)));
        app.MapGet("/api/management/project-discovery", async (
            IProjectDiscovery discovery,
            TargetPolicy policy,
            CancellationToken cancellationToken) =>
        {
            var authorized = policy.GetAllowedProjectPaths()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projects = (await discovery.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            foreach (var path in authorized.Where(path => projects.All(project =>
                         !string.Equals(TargetPolicy.NormalizePath(project.Path), path, StringComparison.OrdinalIgnoreCase))))
            {
                projects.Add(new DiscoveredProject(
                    Path.GetFileName(path),
                    path,
                    0,
                    0));
            }
            return Results.Ok(projects
                .OrderByDescending(project => project.UpdatedAtMs)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .Select(project => new
            {
                project.Name,
                project.Path,
                project.ThreadCount,
                project.UpdatedAtMs,
                authorized = authorized.Contains(TargetPolicy.NormalizePath(project.Path)),
                canSend = policy.CanSend(project.Path),
            }));
        });
        app.MapPut("/api/management/projects", async (
            UpdateAuthorizedProjectsRequest request,
            BridgeConfigurationStore configurationStore,
            TargetPolicy policy,
            IProjectDiscovery discovery,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var paths = request.Paths ?? [];
                var existing = policy.GetAllowedProjectPaths()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var discovered = (await discovery.ListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(project => TargetPolicy.NormalizePath(project.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths.Select(TargetPolicy.NormalizePath))
                {
                    if (!existing.Contains(path) && !discovered.Contains(path))
                        return Results.BadRequest(new { error = "project_not_discovered" });
                }
                var projectSettings = paths.Select(path => new AuthorizedProject(
                    TargetPolicy.NormalizePath(path),
                    request.Permissions?.FirstOrDefault(item => string.Equals(TargetPolicy.NormalizePath(item.Path), TargetPolicy.NormalizePath(path), StringComparison.OrdinalIgnoreCase))?.CanSend ?? true)).ToArray();
                configurationStore.Save(new BridgeConfiguration(
                    BridgeConfiguration.CurrentVersion,
                    projectSettings));
                policy.ReplaceAuthorizedProjects(projectSettings);
                return Results.Ok(new { applied = true });
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { error = "project_configuration_invalid" });
            }
        });
        app.MapGet("/api/management/pairing", (IServiceProvider services) =>
        {
            var remote = services.GetService<RemoteAccessHostedService>();
            var current = remote?.CurrentPairing;
            return Results.Ok(new
            {
                enabled = current is not null,
                url = current?.Url,
                expiresAt = current?.ExpiresAt,
            });
        });
        app.MapDelete("/api/management/devices/{transport}/{deviceId}", (
            string transport,
            string deviceId,
            DeviceManagementService devices) =>
            devices.Revoke(transport, deviceId)
                ? Results.NoContent()
                : Results.NotFound(new { error = "device_not_found" }));
        app.MapPut("/api/management/devices/{transport}/{deviceId}/permissions", (
            string transport,
            string deviceId,
            UpdateDevicePermissionsRequest request,
            DeviceManagementService devices) =>
            devices.SetCanSend(transport, deviceId, request.CanSend)
                ? Results.Ok(new { applied = true })
                : Results.NotFound(new { error = "device_not_found" }));
        app.MapPost("/api/management/devices/remote/{deviceId}/entitlement", InstallEntitlement);
        app.MapPost("/api/management/devices/remote/{deviceId}/redeem", RedeemEntitlement);
        app.MapDelete("/api/management/devices/remote/{deviceId}/entitlement", (
            string deviceId,
            IServiceProvider services) =>
        {
            var entitlements = services.GetService<RemoteEntitlementService>();
            return entitlements is not null && entitlements.Remove(deviceId)
                ? Results.NoContent()
                : Results.NotFound(new { error = "entitlement_not_found" });
        });
    }

    public sealed record UpdateAuthorizedProjectsRequest(IReadOnlyList<string>? Paths, IReadOnlyList<ProjectPermission>? Permissions);
    public sealed record ProjectPermission(string Path, bool CanSend);
    public sealed record UpdateDevicePermissionsRequest(bool CanSend);

    private static async Task<IResult> InstallEntitlement(
        string deviceId,
        EntitlementInstallRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var devices = services.GetService<RemoteDeviceStore>();
        var entitlements = services.GetService<RemoteEntitlementService>();
        if (devices?.Find(deviceId) is null) return Results.NotFound(new { error = "device_not_found" });
        if (entitlements is null) return Results.NotFound(new { error = "remote_disabled" });
        try
        {
            return Results.Ok(await entitlements.InstallAsync(request.Token, deviceId, cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or CryptographicException)
        {
            return Results.BadRequest(new { error = "entitlement_invalid" });
        }
    }

    public sealed record EntitlementInstallRequest(string Token);

    private static async Task<IResult> RedeemEntitlement(
        string deviceId,
        EntitlementRedeemRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var devices = services.GetService<RemoteDeviceStore>();
        var entitlements = services.GetService<RemoteEntitlementService>();
        if (devices?.Find(deviceId) is null) return Results.NotFound(new { error = "device_not_found" });
        if (entitlements is null) return Results.NotFound(new { error = "remote_disabled" });
        try
        {
            return Results.Ok(await entitlements.RedeemAsync(
                request.Code, request.UserId, deviceId, cancellationToken));
        }
        catch (EntitlementCloudException exception)
        {
            var status = exception.ErrorCode is "invite_invalid" ? StatusCodes.Status401Unauthorized :
                exception.ErrorCode is "host_limit_reached" or "device_limit_reached" ? StatusCodes.Status409Conflict :
                exception.ErrorCode is "invite_unavailable" ? StatusCodes.Status403Forbidden :
                StatusCodes.Status503ServiceUnavailable;
            return Results.Json(new { error = exception.ErrorCode }, statusCode: status);
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
        {
            return Results.BadRequest(new { error = "entitlement_invalid" });
        }
    }

    public sealed record EntitlementRedeemRequest(string Code, string UserId);

    private static void UseManagementAuthentication(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/management"))
            {
                await next();
                return;
            }

            var guard = context.RequestServices.GetRequiredService<ManagementAccessGuard>();
            if (!guard.Authorize(
                    context.Connection.RemoteIpAddress,
                    context.Connection.LocalIpAddress,
                    context.Request.Headers[ManagementAccessGuard.TokenHeader],
                    context.Request.Headers[ManagementAccessGuard.NonceHeader]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "management_unauthorized" });
                return;
            }

            await next();
        });
    }

    private static IResult NotImplemented() =>
        Results.Json(
            new { error = "not_implemented" },
            statusCode: StatusCodes.Status501NotImplemented);
}
