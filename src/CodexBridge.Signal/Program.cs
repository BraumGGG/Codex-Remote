using System.Security.Cryptography;
using System.Text;
using CodexBridge.Signal;
using System.Net;
using Microsoft.AspNetCore.StaticFiles;

public static class Program
{
    public static WebApplication BuildApplication(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var secretText = builder.Configuration["CODEX_BRIDGE_TURN_SECRET"];
        if (string.IsNullOrWhiteSpace(secretText) || Encoding.UTF8.GetByteCount(secretText) < 32)
            throw new InvalidOperationException("CODEX_BRIDGE_TURN_SECRET must contain at least 32 UTF-8 bytes.");
        var secret = Encoding.UTF8.GetBytes(secretText);
        var options = new SignalOptions
        {
            TurnSharedSecret = secret,
            TurnUrls = builder.Configuration.GetSection("TurnUrls").Get<string[]>() ?? [],
            MaximumConnectionsPerIp = builder.Configuration.GetValue("MaximumConnectionsPerIp", 10),
        };
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(serviceProvider => new SignalConnectionRegistry(
            serviceProvider.GetRequiredService<TimeProvider>(), options.MaximumConnectionsPerIp));
        builder.Services.AddSingleton<RoutingTicketVerifier>();
        builder.Services.AddSingleton(serviceProvider => new TurnCredentialService(
            options.TurnSharedSecret, serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<SignalWebSocketHub>();
        builder.Services.AddSingleton<SignalMetrics>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var remoteHtml = context.Request.Path.StartsWithSegments("/remote") &&
                (!Path.HasExtension(path) || path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase));
            if (remoteHtml)
            {
                context.Response.OnStarting(() =>
                {
                    SetNoCacheHeaders(context.Response);
                    return Task.CompletedTask;
                });
            }
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles(CreateStaticFileOptions());
        app.UseStaticFiles(CreateStaticFileOptions("/remote"));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapGet("/metrics", (HttpContext context, SignalMetrics metrics, SignalConnectionRegistry registry) =>
            context.Connection.RemoteIpAddress is not null && IPAddress.IsLoopback(context.Connection.RemoteIpAddress)
                ? Results.Text(metrics.Render(registry.Snapshot()), "text/plain; version=0.0.4")
                : Results.NotFound());
        app.Map("/signal", context => context.RequestServices.GetRequiredService<SignalWebSocketHub>().AcceptAsync(context));
        app.MapFallbackToFile("/remote/{*path:nonfile}", "index.html");
        return app;
    }

    private static StaticFileOptions CreateStaticFileOptions(string? requestPath = null)
    {
        var options = new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                if (!string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase)) return;
                SetNoCacheHeaders(context.Context.Response);
            },
        };
        if (requestPath is not null) options.RequestPath = requestPath;
        return options;
    }

    private static void SetNoCacheHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    public static Task Main(string[] args) => BuildApplication(args).RunAsync();
}
