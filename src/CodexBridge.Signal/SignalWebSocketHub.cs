using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexBridge.Signal;

public sealed class SignalWebSocketHub(
    SignalConnectionRegistry registry,
    RoutingTicketVerifier ticketVerifier,
    TurnCredentialService turnCredentials,
    SignalOptions options,
    SignalMetrics metrics,
    ILogger<SignalWebSocketHub> logger)
{
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = Guid.NewGuid().ToString("N");
        var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        Peer? peer = null;
        try
        {
            peer = await AuthenticateAsync(socket, connectionId, ipAddress, context.RequestAborted);
            if (peer is null)
                return;
            _peers[peer.Connection.Id] = peer;
            metrics.AuthenticationSucceeded(peer.Role == "host" ? "host" : "client");
            logger.LogInformation("Signal peer authenticated: {Properties}", PrivacySafeLogger.Sanitize(
                new Dictionary<string, object?> { ["eventName"] = "authenticated", ["role"] = peer.Role }));
            await ReceiveLoopAsync(peer, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            if (peer is not null)
            {
                _peers.TryRemove(peer.Connection.Id, out _);
                if (peer.Role == "host") registry.RemoveHost(peer.HostId, peer.Connection.Id);
                else registry.RemoveClient(peer.HostId, peer.DeviceId, peer.Connection.Id);
            }
        }
    }

    private async Task<Peer?> AuthenticateAsync(
        WebSocket socket, string connectionId, string ipAddress, CancellationToken cancellationToken)
    {
        var first = await ReceiveJsonAsync(socket, cancellationToken);
        if (first is null || !first.RootElement.TryGetProperty("type", out var typeElement))
            return await RejectAsync(socket, "invalid_auth", cancellationToken);
        var type = typeElement.GetString();
        if (type == "host-hello")
            return await AuthenticateHostAsync(socket, connectionId, ipAddress, first.RootElement, cancellationToken);
        if (type == "client-auth")
            return await AuthenticateTicketClientAsync(socket, connectionId, ipAddress, first.RootElement, cancellationToken);
        if (type == "pairing-client")
            return await AuthenticatePairingClientAsync(socket, connectionId, ipAddress, first.RootElement, cancellationToken);
        return await RejectAsync(socket, "invalid_auth", cancellationToken);
    }

    private async Task<Peer?> AuthenticateHostAsync(
        WebSocket socket, string connectionId, string ipAddress, JsonElement message, CancellationToken cancellationToken)
    {
        HostChallenge challenge;
        try
        {
            challenge = HostChallengeVerifier.Create(
                message.GetProperty("hostId").GetString()!,
                message.GetProperty("hostPublicKeySpki").GetString()!);
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            return await RejectAsync(socket, "invalid_host", cancellationToken);
        }
        await SendJsonAsync(socket, new { type = "challenge", challenge = SignalEncoding.Encode(challenge.Challenge) }, cancellationToken);
        var proof = await ReceiveJsonAsync(socket, cancellationToken);
        if (proof is null || proof.RootElement.GetProperty("type").GetString() != "host-auth" ||
            !HostChallengeVerifier.Verify(challenge, proof.RootElement.GetProperty("signature").GetString()!))
            return await RejectAsync(socket, "invalid_proof", cancellationToken);
        var connection = new SignalConnection(connectionId, ipAddress);
        if (!registry.TryReplaceHost(challenge.HostId, connection, out var replaced))
            return await RejectAsync(socket, "capacity_or_duplicate", cancellationToken);
        await SendJsonAsync(socket, new { type = "authenticated", role = "host" }, cancellationToken);
        await CloseReplacedAsync(replaced, cancellationToken);
        return new Peer(socket, connection, "host", challenge.HostId, "");
    }

    private async Task<Peer?> AuthenticateTicketClientAsync(
        WebSocket socket, string connectionId, string ipAddress, JsonElement message, CancellationToken cancellationToken)
    {
        VerifiedRoutingTicket ticket;
        try
        {
            ticket = ticketVerifier.Verify(message.GetProperty("ticket").Deserialize<SignedRoutingTicket>(JsonOptions)!);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or ArgumentNullException)
        {
            return await RejectAsync(socket, "invalid_ticket", cancellationToken);
        }
        var deviceChallenge = RandomNumberGenerator.GetBytes(32);
        await SendJsonAsync(socket, new { type = "device-challenge", challenge = SignalEncoding.Encode(deviceChallenge) }, cancellationToken);
        var proof = await ReceiveJsonAsync(socket, cancellationToken);
        try
        {
            var publicKey = SignalEncoding.Decode(ticket.DevicePublicKeySpki);
            if (proof is null || proof.RootElement.GetProperty("type").GetString() != "device-auth" ||
                !HostChallengeVerifier.VerifyProof(publicKey, deviceChallenge, proof.RootElement.GetProperty("signature").GetString()!))
                return await RejectAsync(socket, "invalid_device_proof", cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return await RejectAsync(socket, "invalid_device_proof", cancellationToken);
        }
        return await RegisterClientAsync(socket, connectionId, ipAddress, ticket.HostId, ticket.DeviceId, "client", cancellationToken);
    }

    private async Task<Peer?> AuthenticatePairingClientAsync(
        WebSocket socket, string connectionId, string ipAddress, JsonElement message, CancellationToken cancellationToken)
    {
        string routeId;
        string deviceId;
        try
        {
            routeId = message.GetProperty("routeId").GetString()!;
            deviceId = message.GetProperty("deviceId").GetString()!;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            return await RejectAsync(socket, "invalid_pairing", cancellationToken);
        }
        var hostId = registry.ResolvePairingRoute(routeId);
        if (hostId is null || !IsIdentifier(deviceId))
            return await RejectAsync(socket, "invalid_pairing", cancellationToken);
        var peer = await RegisterClientAsync(
            socket, connectionId, ipAddress, hostId, deviceId, "pairing", cancellationToken);
        if (peer is not null)
            await NotifyHostPairingClientReadyAsync(peer, cancellationToken);
        return peer;
    }

    private async Task NotifyHostPairingClientReadyAsync(Peer pairingPeer, CancellationToken cancellationToken)
    {
        var connection = registry.GetHost(pairingPeer.HostId);
        if (connection is not null && _peers.TryGetValue(connection.Id, out var host))
            await host.SendJsonAsync(new { type = "pairing-client-ready" }, cancellationToken);
    }

    private async Task<Peer?> RegisterClientAsync(
        WebSocket socket, string connectionId, string ipAddress, string hostId, string deviceId,
        string role, CancellationToken cancellationToken)
    {
        var connection = new SignalConnection(connectionId, ipAddress);
        if (registry.GetHost(hostId) is null ||
            !registry.TryReplaceClient(hostId, deviceId, connection, out var replaced))
            return await RejectAsync(socket, "host_offline_or_capacity", cancellationToken);
        await SendJsonAsync(socket, new { type = "authenticated", role }, cancellationToken);
        await CloseReplacedAsync(replaced, cancellationToken);
        return new Peer(socket, connection, role, hostId, deviceId);
    }

    private async Task CloseReplacedAsync(SignalConnection? replaced, CancellationToken cancellationToken)
    {
        if (replaced is null || !_peers.TryGetValue(replaced.Id, out var previous) ||
            previous.Socket.State != WebSocketState.Open)
            return;
        try
        {
            await previous.Socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure, "connection_replaced", cancellationToken);
        }
        catch (WebSocketException) { }
    }

    private async Task ReceiveLoopAsync(Peer peer, CancellationToken cancellationToken)
    {
        while (peer.Socket.State == WebSocketState.Open)
        {
            var message = await ReceiveJsonAsync(peer.Socket, cancellationToken);
            if (message is null) return;
            if (!IsCurrentPeer(peer)) return;
            var root = message.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "turn-request")
            {
                var credentials = turnCredentials.Issue(peer.Role == "host" ? peer.HostId[..Math.Min(32, peer.HostId.Length)] : peer.DeviceId);
                metrics.TurnCredentialIssued();
                await peer.SendJsonAsync(new { type = "turn-credentials", urls = options.TurnUrls, credentials.Username, credentials.Credential, expiresAt = credentials.ExpiresAt }, cancellationToken);
                continue;
            }
            if (peer.Role == "host" && type == "pairing-route")
            {
                var ok = TryRegisterPairingRoute(peer, root);
                await peer.SendJsonAsync(new { type = "pairing-route-result", accepted = ok }, cancellationToken);
                continue;
            }
            if (peer.Role == "host" && type == "forward")
            {
                await ForwardFromHostAsync(peer, root, cancellationToken);
                continue;
            }
            if (peer.Role == "host" && type == "telemetry")
            {
                ObserveTelemetry(root);
                continue;
            }
            if (peer.Role != "host" && type == "client-telemetry")
            {
                ObserveClientTelemetry(root);
                continue;
            }
            if (peer.Role != "host")
                await ForwardFromClientAsync(peer, root, cancellationToken);
        }
    }

    private bool IsCurrentPeer(Peer peer)
    {
        var current = peer.Role == "host"
            ? registry.GetHost(peer.HostId)
            : registry.GetClient(peer.HostId, peer.DeviceId);
        return current?.Id == peer.Connection.Id;
    }

    private bool TryRegisterPairingRoute(Peer peer, JsonElement root)
    {
        try
        {
            return registry.TryAddPairingRoute(
                root.GetProperty("routeId").GetString()!,
                peer.HostId,
                root.GetProperty("expiresAt").GetDateTimeOffset());
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private async Task ForwardFromHostAsync(Peer sender, JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("deviceId", out var deviceElement) || !root.TryGetProperty("payload", out var payload)) return;
        var connection = registry.GetClient(sender.HostId, deviceElement.GetString()!);
        if (connection is not null && _peers.TryGetValue(connection.Id, out var target))
        {
            metrics.Forwarded(true);
            await target.SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(payload), cancellationToken);
        }
        else metrics.Forwarded(false);
    }

    private async Task ForwardFromClientAsync(Peer sender, JsonElement payload, CancellationToken cancellationToken)
    {
        var connection = registry.GetHost(sender.HostId);
        if (connection is not null && _peers.TryGetValue(connection.Id, out var target))
        {
            metrics.Forwarded(true);
            await target.SendJsonAsync(new { type = "client-signal", sender.DeviceId, payload }, cancellationToken);
        }
        else metrics.Forwarded(false);
    }

    private void ObserveTelemetry(JsonElement root)
    {
        try
        {
            var outcome = root.GetProperty("outcome").GetString();
            var candidateType = root.GetProperty("candidateType").GetString();
            var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
            if (outcome is not ("connected" or "failed") || candidateType is not ("host" or "srflx" or "relay" or "unknown"))
                return;
            metrics.ObserveTelemetry(outcome, candidateType, version);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException) { }
    }

    private void ObserveClientTelemetry(JsonElement root)
    {
        try
        {
            var stage = root.GetProperty("stage").GetString();
            metrics.ObserveClientStage(stage);
            if (stage is not null)
                logger.LogInformation("Signal client stage: {Stage}", stage);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException) { }
    }

    private static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private async Task<Peer?> RejectAsync(WebSocket socket, string code, CancellationToken cancellationToken)
    {
        metrics.AuthenticationRejected();
        if (socket.State == WebSocketState.Open)
        {
            await SendJsonAsync(socket, new { type = "error", code }, cancellationToken);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, code, cancellationToken);
        }
        return null;
    }

    private static async Task<JsonDocument?> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[SignalOptions.MaximumFrameBytes + 1];
        var total = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(total), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Only text signaling frames are accepted.");
            total += result.Count;
            if (total > SignalOptions.MaximumFrameBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_too_large", cancellationToken);
                return null;
            }
            if (result.EndOfMessage) break;
        }
        try { return JsonDocument.Parse(buffer.AsMemory(0, total)); }
        catch (JsonException) { return null; }
    }

    private static Task SendJsonAsync(WebSocket socket, object value, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), WebSocketMessageType.Text, true, cancellationToken);

    private sealed class Peer(WebSocket socket, SignalConnection connection, string role, string hostId, string deviceId)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public WebSocket Socket { get; } = socket;
        public SignalConnection Connection { get; } = connection;
        public string Role { get; } = role;
        public string HostId { get; } = hostId;
        public string DeviceId { get; } = deviceId;

        public Task SendJsonAsync(object value, CancellationToken cancellationToken) =>
            SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken);

        public async Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally { _sendLock.Release(); }
        }
    }
}
