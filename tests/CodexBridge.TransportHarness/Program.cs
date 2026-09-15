using CodexBridge.Host.Remote;
using CodexBridge.Remote.Protocol;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var root = args.Length >= 1
    ? Path.GetFullPath(args[0])
    : throw new ArgumentException("Repository root argument is required.");
var executable = args.Length >= 2
    ? Path.GetFullPath(args[1])
    : Path.Combine(root, "artifacts", "transport", "dev", "CodexBridge.Transport.exe");
var manifest = Path.Combine(Path.GetDirectoryName(executable)!, "transport-manifest.json");
var integrity = new ConfiguredTransportIntegrityGate(
    new TransportIntegrityVerifier(new WinTrustAuthenticodeVerifier()),
    executable,
    manifest,
    requireAuthenticode: false);
await using var manager = new TransportProcessManager(
    integrity,
    new WindowsTransportProcessLauncher(executable),
    new RemotePipeServerFactory(TimeSpan.FromSeconds(10)),
    TimeProvider.System);
await manager.StartAsync();
await using var remote = new PipeRemoteFrameTransport(
    manager.GetAuthenticatedStream(),
    kind => Console.Error.WriteLine($"HARNESS_PIPE_MESSAGE={kind}"),
    diagnostic => Console.Error.WriteLine($"HARNESS_DIAGNOSTIC event={diagnostic.Event ?? "none"} state={diagnostic.State ?? "none"} local={diagnostic.LocalType ?? "none"} remote={diagnostic.RemoteType ?? "none"} protocol={diagnostic.Protocol ?? "none"}"));

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();
var lifetime = app.Lifetime.ApplicationStopping;
var echoStarted = 0;

app.MapGet("/", () => Results.Content(
    "<!doctype html><meta charset=utf-8><title>Codex Bridge transport harness</title>",
    "text/html"));
app.MapPost("/offer", async (SessionDescription offer, CancellationToken cancellationToken) =>
{
    if (offer.Type != "offer" || string.IsNullOrWhiteSpace(offer.Sdp)) return Results.BadRequest();
    var iceServers = offer.IceServers?.Select(server =>
        new RemoteIceServer(server.Urls ?? [], server.Username ?? "", server.Credential ?? "")).ToArray() ?? [];
    Console.Error.WriteLine($"HARNESS_ICE_SERVERS={iceServers.Length} URLS={iceServers.Sum(server => server.Urls.Length)}");
    var answer = await remote.AcceptOfferAsync(offer.Sdp, iceServers, cancellationToken);
    var answerRelay = System.Text.RegularExpressions.Regex.IsMatch(answer, @" typ relay(?: |$)");
    Console.Error.WriteLine($"HARNESS_ANSWER_RELAY={answerRelay}");
    if (Interlocked.Exchange(ref echoStarted, 1) == 0)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in remote.ReadAllAsync(lifetime))
                {
                    Console.Error.WriteLine("HARNESS_REMOTE_FRAME_RECEIVED=1");
                    await remote.SendAsync(
                        new RemoteFrame(
                            RemoteFrameKind.Response,
                            frame.RequestId,
                            frame.Sequence,
                            frame.Payload),
                        lifetime);
                    Console.Error.WriteLine("HARNESS_RESPONSE_SENT=1");
                }
            }
            catch (Exception exception) when (!lifetime.IsCancellationRequested)
            {
                Console.Error.WriteLine($"HARNESS_ECHO_ERROR={exception.GetType().Name}:{exception.Message}");
            }
        }, CancellationToken.None);
    }
    return Results.Ok(new SessionDescription("answer", answer, null));
});
app.MapPost("/shutdown", (IHostApplicationLifetime applicationLifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(50);
        applicationLifetime.StopApplication();
    });
    return Results.NoContent();
});

await app.StartAsync();
var addresses = app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()?.Addresses;
Console.WriteLine($"HARNESS_URL={addresses?.Single() ?? throw new InvalidOperationException("Harness URL unavailable.")}");
await app.WaitForShutdownAsync();

internal sealed record SessionDescription(string Type, string Sdp, IceServer[]? IceServers);
internal sealed record IceServer(string[]? Urls, string? Username, string? Credential);
