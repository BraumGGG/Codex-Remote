using CodexBridge.Host;
using CodexBridge.Host.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using CodexBridge.Core;

namespace CodexBridge.Host.Tests;

internal sealed class RunningHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RunningHost(WebApplication app, HttpClient client, string directory)
    {
        _app = app;
        Client = client;
        Directory = directory;
    }

    public HttpClient Client { get; }

    public string Directory { get; }

    public string AuditLogPath => Path.Combine(Directory, "audit.jsonl");

    public string ManagementToken =>
        _app.Services.GetRequiredService<ManagementAccessGuard>().Token;

    public static async Task<RunningHost> StartAsync(
        Action<string>? setupData = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var directory = TestPaths.CreateDirectory("running-host");
        setupData?.Invoke(directory);
        var configurationPath = Path.Combine(directory, "bridge-config.json");
        if (!File.Exists(configurationPath))
        {
            new BridgeConfigurationStore(configurationPath).Save(new BridgeConfiguration(
                BridgeConfiguration.CurrentVersion,
                [new AuthorizedProject(TargetPolicy.AllowedProjectPath)]));
        }
        var options = new HostOptions(
            "http://127.0.0.1:0",
            Path.Combine(directory, "state.sqlite"),
            Path.Combine(directory, "audit.jsonl"),
            configurationPath);
        var app = HostApplication.Build([], options, configureServices);
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("测试 Host 未产生监听地址。");
        return new RunningHost(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            directory);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
