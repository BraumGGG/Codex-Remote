using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodexBridge.Signal.Tests;

public sealed class SignalServerTests
{
    [Fact]
    public async Task WebSocket_AuthenticatesPairsAndForwardsBothDirections()
    {
        await using var server = await TestSignalServer.StartAsync();
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostId = HostId(hostKey);
        using var host = await ConnectHostAsync(server, hostKey, hostId);

        var expiry = DateTimeOffset.UtcNow.AddMinutes(4);
        await SendAsync(host, new { type = "pairing-route", routeId = "route_1", expiresAt = expiry });
        Assert.True((await ReceiveAsync(host)).GetProperty("accepted").GetBoolean());

        using var client = await server.ConnectAsync();
        await SendAsync(client, new { type = "pairing-client", routeId = "route_1", deviceId = "device_1" });
        Assert.Equal("authenticated", (await ReceiveAsync(client)).GetProperty("type").GetString());

        var ready = await ReceiveAsync(host);
        Assert.Equal("pairing-client-ready", ready.GetProperty("type").GetString());
        Assert.Single(ready.EnumerateObject());

        await SendAsync(client, new { type = "offer", marker = "from-client" });
        var hostMessage = await ReceiveAsync(host);
        Assert.Equal("client-signal", hostMessage.GetProperty("type").GetString());
        Assert.Equal("device_1", hostMessage.GetProperty("deviceId").GetString());
        Assert.Equal("from-client", hostMessage.GetProperty("payload").GetProperty("marker").GetString());

        await SendAsync(host, new
        {
            type = "forward",
            deviceId = "device_1",
            payload = new { type = "answer", marker = "from-host" },
        });
        var clientMessage = await ReceiveAsync(client);
        Assert.Equal("answer", clientMessage.GetProperty("type").GetString());
        Assert.Equal("from-host", clientMessage.GetProperty("marker").GetString());

        await SendAsync(client, new { type = "turn-request" });
        var turn = await ReceiveAsync(client);
        Assert.Equal("turn-credentials", turn.GetProperty("type").GetString());
        Assert.Contains("device_1", turn.GetProperty("username").GetString());
        Assert.Equal("turn:relay.example:3478", turn.GetProperty("urls")[0].GetString());

        await SendAsync(client, new { type = "client-telemetry", stage = "datachannel_open" });
        await SendAsync(client, new { type = "client-telemetry", stage = "not_allowed" });

        await SendAsync(host, new
        {
            type = "telemetry",
            outcome = "connected",
            candidateType = "relay",
            version = "1.2.345.0",
        });
        using var metricsClient = new HttpClient();
        var metrics = await metricsClient.GetStringAsync(new Uri(server.HttpUri, "/metrics"));
        Assert.Contains("codex_bridge_signal_active_hosts 1", metrics, StringComparison.Ordinal);
        Assert.Contains("codex_bridge_signal_active_clients 1", metrics, StringComparison.Ordinal);
        Assert.Contains("codex_bridge_remote_candidate_total{type=\"relay\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("codex_bridge_remote_version_total{version=\"1.2\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("codex_bridge_client_stage_total{stage=\"datachannel_open\"} 1", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain(hostId, metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("device_1", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_AcceptsValidRoutingTicketInFirstFrame()
    {
        await using var server = await TestSignalServer.StartAsync();
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostId = HostId(hostKey);
        using var host = await ConnectHostAsync(server, hostKey, hostId);
        var ticket = RoutingTicketVerifier.Sign(new RoutingTicketPayload(
            1,
            hostId,
            "ticket_device",
            SignalEncoding.Encode(deviceKey.ExportSubjectPublicKeyInfo()),
            DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
            SignalEncoding.Encode(RandomNumberGenerator.GetBytes(16))), hostKey);

        using var client = await server.ConnectAsync();
        await SendAsync(client, new { type = "client-auth", ticket });
        await AnswerDeviceChallengeAsync(client, deviceKey);
        var response = await ReceiveAsync(client);
        Assert.Equal("authenticated", response.GetProperty("type").GetString());
        Assert.Equal("client", response.GetProperty("role").GetString());
    }

    [Fact]
    public async Task WebSocket_NewConnectionForSameDeviceReplacesOldAndRemainsRoutable()
    {
        await using var server = await TestSignalServer.StartAsync();
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostId = HostId(hostKey);
        using var host = await ConnectHostAsync(server, hostKey, hostId);
        var ticket = RoutingTicketVerifier.Sign(new RoutingTicketPayload(
            1,
            hostId,
            "replacement_device",
            SignalEncoding.Encode(deviceKey.ExportSubjectPublicKeyInfo()),
            DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
            SignalEncoding.Encode(RandomNumberGenerator.GetBytes(16))), hostKey);

        using var first = await ConnectTicketClientAsync(server, ticket, deviceKey);
        using var replacement = await ConnectTicketClientAsync(server, ticket, deviceKey);
        var closed = await first.ReceiveAsync(new byte[128], CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);

        await SendAsync(replacement, new { type = "offer", marker = "replacement" });
        var forwarded = await ReceiveAsync(host);
        Assert.Equal("replacement_device", forwarded.GetProperty("deviceId").GetString());
        Assert.Equal("replacement", forwarded.GetProperty("payload").GetProperty("marker").GetString());
    }

    [Fact]
    public async Task WebSocket_RejectsTicketWithoutBoundDevicePrivateKey()
    {
        await using var server = await TestSignalServer.StartAsync();
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostId = HostId(hostKey);
        using var host = await ConnectHostAsync(server, hostKey, hostId);
        var ticket = RoutingTicketVerifier.Sign(new RoutingTicketPayload(
            1, hostId, "bound_device", SignalEncoding.Encode(deviceKey.ExportSubjectPublicKeyInfo()),
            DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), SignalEncoding.Encode(new byte[16])), hostKey);
        using var client = await server.ConnectAsync();
        await SendAsync(client, new { type = "client-auth", ticket });
        await AnswerDeviceChallengeAsync(client, attackerKey);
        Assert.Equal("invalid_device_proof", (await ReceiveAsync(client)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoteFrontend_IsServedWithoutExposingSignalSecrets()
    {
        await using var server = await TestSignalServer.StartAsync();
        using var client = new HttpClient();
        var htmlResponse = await client.GetAsync(new Uri(server.HttpUri, "/remote/"));
        var html = await htmlResponse.Content.ReadAsStringAsync();
        var css = await client.GetAsync(new Uri(server.HttpUri, "/remote/app.css"));
        var script = await client.GetAsync(new Uri(server.HttpUri, "/remote/app.js"));
        Assert.Contains("Codex", html, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("href=\"(?:/remote/|/)?app\\.css\\?", html);
        Assert.Matches("src=\"(?:/remote/|/)?app\\.js\\?", html);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.True(htmlResponse.Headers.CacheControl?.NoStore);
        Assert.True(htmlResponse.Headers.CacheControl?.NoCache);
        Assert.DoesNotContain("TURN_SECRET", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebSocket_RejectsOversizedFrameAndRestartHasNoQueue()
    {
        await using (var firstServer = await TestSignalServer.StartAsync())
        {
            using var oversized = await firstServer.ConnectAsync();
            var bytes = new byte[SignalOptions.MaximumFrameBytes + 1];
            Array.Fill(bytes, (byte)'a');
            await oversized.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[128];
            var close = await oversized.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Close, close.MessageType);
            Assert.Equal(WebSocketCloseStatus.MessageTooBig, close.CloseStatus);
        }

        await using var restarted = await TestSignalServer.StartAsync();
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostId = HostId(hostKey);
        var ticket = RoutingTicketVerifier.Sign(new RoutingTicketPayload(
            1, hostId, "offline_device", SignalEncoding.Encode(deviceKey.ExportSubjectPublicKeyInfo()),
            DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), SignalEncoding.Encode(new byte[16])), hostKey);
        using var client = await restarted.ConnectAsync();
        await SendAsync(client, new { type = "client-auth", ticket });
        await AnswerDeviceChallengeAsync(client, deviceKey);
        Assert.Equal("host_offline_or_capacity", (await ReceiveAsync(client)).GetProperty("code").GetString());
    }

    private static async Task<ClientWebSocket> ConnectHostAsync(TestSignalServer server, ECDsa key, string hostId)
    {
        var socket = await server.ConnectAsync();
        await SendAsync(socket, new
        {
            type = "host-hello",
            hostId,
            hostPublicKeySpki = SignalEncoding.Encode(key.ExportSubjectPublicKeyInfo()),
        });
        var challenge = SignalEncoding.Decode((await ReceiveAsync(socket)).GetProperty("challenge").GetString()!);
        var signature = key.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await SendAsync(socket, new { type = "host-auth", signature = SignalEncoding.Encode(signature) });
        Assert.Equal("authenticated", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        return socket;
    }

    private static async Task<ClientWebSocket> ConnectTicketClientAsync(
        TestSignalServer server, SignedRoutingTicket ticket, ECDsa deviceKey)
    {
        var socket = await server.ConnectAsync();
        await SendAsync(socket, new { type = "client-auth", ticket });
        await AnswerDeviceChallengeAsync(socket, deviceKey);
        Assert.Equal("authenticated", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        return socket;
    }

    private static string HostId(ECDsa key) =>
        SignalEncoding.Encode(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    private static async Task AnswerDeviceChallengeAsync(ClientWebSocket socket, ECDsa deviceKey)
    {
        var challenge = SignalEncoding.Decode((await ReceiveAsync(socket)).GetProperty("challenge").GetString()!);
        var signature = deviceKey.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await SendAsync(socket, new { type = "device-auth", signature = SignalEncoding.Encode(signature) });
    }

    private static Task SendAsync(ClientWebSocket socket, object value) => socket.SendAsync(
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[SignalOptions.MaximumFrameBytes];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class TestSignalServer(WebApplication app, Uri webSocketUri, Uri httpUri) : IAsyncDisposable
    {
        public Uri HttpUri { get; } = httpUri;
        public static async Task<TestSignalServer> StartAsync()
        {
            var secret = new string('a', 64);
            var app = Program.BuildApplication([
                "--urls", "http://127.0.0.1:0",
                $"--CODEX_BRIDGE_TURN_SECRET={secret}",
                "--TurnUrls:0=turn:relay.example:3478",
            ]);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            var httpUri = new Uri(addresses.Single());
            return new TestSignalServer(app, new UriBuilder(httpUri) { Scheme = "ws", Path = "/signal" }.Uri, httpUri);
        }

        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(webSocketUri, CancellationToken.None);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
