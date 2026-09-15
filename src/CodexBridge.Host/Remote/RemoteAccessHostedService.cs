using System.Net.WebSockets;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Diagnostics;

namespace CodexBridge.Host.Remote;

public sealed class RemoteAccessHostedService(
    RemoteAccessOptions options,
    RemoteIdentityStore identities,
    RemotePairingService pairing,
    RemoteDeviceStore devices,
    RemoteRoutingTicketIssuer tickets,
    RemoteSessionFactory sessions,
    RemoteEntitlementService entitlements,
    RemoteTelemetryReporter telemetry,
    BridgeDiagnosticsService diagnostics,
    ILogger<RemoteAccessHostedService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _pairingRotationGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IssuedRemotePairing? _currentPairing;
    private RemoteHostIdentity? _identity;
    private IReadOnlyList<RemoteIceServer> _iceServers = [];
    private DateTimeOffset _iceServersExpiresAt = DateTimeOffset.MinValue;
    private long _pairingClientReadyAt;
    public RemotePairingInfo? CurrentPairing { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            diagnostics.Report(BridgeComponent.Signal, BridgeComponentState.Starting);
            diagnostics.Report(BridgeComponent.Pairing, BridgeComponentState.Starting);
            try
            {
                await RunConnectionAsync(stoppingToken).ConfigureAwait(false);
                diagnostics.Report(
                    BridgeComponent.Signal,
                    BridgeComponentState.Offline,
                    "signal_connection_closed");
                diagnostics.Report(
                    BridgeComponent.Pairing,
                    BridgeComponentState.Offline,
                    "pairing_signal_unavailable");
                backoff.Reset();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                var error = ClassifySignalError(exception);
                diagnostics.Report(
                    BridgeComponent.Signal,
                    BridgeComponentState.Offline,
                    error);
                diagnostics.Report(
                    BridgeComponent.Pairing,
                    BridgeComponentState.Offline,
                    "pairing_signal_unavailable");
                logger.LogWarning(
                    "Remote signaling connection failed: {ErrorCode} ({ErrorType}, ws={WebSocketError}, socket={SocketError}, inner={InnerType})",
                    error,
                    exception.GetType().Name,
                    exception is WebSocketException webSocket ? webSocket.WebSocketErrorCode.ToString() : "none",
                    FindSocketError(exception)?.ToString() ?? "none",
                    exception.InnerException?.GetType().Name ?? "none");
            }
            CurrentPairing = null;
            await Task.Delay(backoff.Next(), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(options.SignalUri!, cancellationToken).ConfigureAwait(false);
        _identity?.Dispose();
        _identity = await identities.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        entitlements.Initialize(_identity.HostId);
        var hostVersion = typeof(RemoteAccessHostedService).Assembly.GetName().Version;
        await SendAsync(socket, new
        {
            type = "host-hello",
            hostId = _identity.HostId,
            hostPublicKeySpki = _identity.PublicKeySpki,
            hostVersion = hostVersion is null ? "unknown" : $"{hostVersion.Major}.{hostVersion.Minor}",
        }, cancellationToken);
        var challenge = await ReceiveAsync(socket, cancellationToken) ?? throw new IOException("Signal challenge is unavailable.");
        var challengeBytes = RemoteEncoding.Base64UrlDecode(challenge.RootElement.GetProperty("challenge").GetString()!);
        await SendAsync(socket, new { type = "host-auth", signature = RemoteEncoding.Base64UrlEncode(_identity.Sign(challengeBytes)) }, cancellationToken);
        EnsureType(await ReceiveAsync(socket, cancellationToken), "authenticated");
        logger.LogInformation("Signal host authenticated");
        diagnostics.Report(BridgeComponent.Signal, BridgeComponentState.Online);

        await RefreshIceServersAsync(socket, cancellationToken).ConfigureAwait(false);

        _currentPairing = pairing.Issue();
        await SendAsync(socket, new { type = "pairing-route", routeId = _currentPairing.RouteId, expiresAt = _currentPairing.ExpiresAt }, cancellationToken);
        var route = await ReceiveAsync(socket, cancellationToken) ?? throw new IOException("Pairing route was not acknowledged.");
        if (!route.RootElement.GetProperty("accepted").GetBoolean()) throw new IOException("Pairing route was rejected.");
        CurrentPairing = CreatePairingInfo(_identity, _currentPairing);
        diagnostics.Report(BridgeComponent.Pairing, BridgeComponentState.Online);

        using var telemetryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var telemetryPump = PumpTelemetryAsync(socket, telemetryCancellation.Token);
        var pairingPump = RefreshExpiredPairingAsync(socket, telemetryCancellation.Token);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, cancellationToken);
                if (message is null) return;
                var messageType = message.RootElement.GetProperty("type").GetString();
                if (messageType == "pairing-route-result")
                {
                    if (!message.RootElement.GetProperty("accepted").GetBoolean())
                    {
                        CurrentPairing = null;
                        _currentPairing = null;
                    }
                    continue;
                }
                if (messageType == "pairing-client-ready")
                {
                    Interlocked.Exchange(ref _pairingClientReadyAt, Environment.TickCount64);
                    diagnostics.Report(BridgeComponent.Pairing, BridgeComponentState.Starting);
                    continue;
                }
                if (messageType != "client-signal") continue;
                var deviceId = message.RootElement.GetProperty("deviceId").GetString()!;
                var payload = message.RootElement.GetProperty("payload").Clone();
                await HandleClientSignalAsync(socket, deviceId, payload, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            telemetryCancellation.Cancel();
            try { await telemetryPump.ConfigureAwait(false); }
            catch (OperationCanceledException) when (telemetryCancellation.IsCancellationRequested) { }
            try { await pairingPump.ConfigureAwait(false); }
            catch (OperationCanceledException) when (telemetryCancellation.IsCancellationRequested) { }
        }
    }

    private async Task RefreshExpiredPairingAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var readyAt = Interlocked.Read(ref _pairingClientReadyAt);
            if (readyAt > 0 && Environment.TickCount64 - readyAt >= 15_000 &&
                Interlocked.CompareExchange(ref _pairingClientReadyAt, 0, readyAt) == readyAt)
            {
                diagnostics.Report(
                    BridgeComponent.Pairing,
                    BridgeComponentState.Offline,
                    "pairing_client_timeout");
            }
            var current = _currentPairing;
            if (current is null || pairing.IsExpired(current))
                await RotatePairingAsync(socket, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpTelemetryAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        await foreach (var item in telemetry.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendAsync(socket, new
            {
                type = "telemetry",
                outcome = item.Outcome,
                candidateType = item.CandidateType,
                version = item.Version,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleClientSignalAsync(ClientWebSocket socket, string routedDeviceId, JsonElement payload, CancellationToken cancellationToken)
    {
        var type = payload.GetProperty("type").GetString();
        if (type == "pairing-offer")
        {
            Interlocked.Exchange(ref _pairingClientReadyAt, 0);
            RemoteDeviceRegistration? registration = null;
            try
            {
                var bytes = RemoteEncoding.Base64UrlDecode(payload.GetProperty("pairingPayloadBase64Url").GetString()!);
                var proof = payload.GetProperty("proof").GetString()!;
                var current = _currentPairing;
                var consumeResult = current is null
                    ? RemotePairingConsumeResult.NotIssued
                    : pairing.Consume(current.RouteId, bytes, proof);
                if (consumeResult != RemotePairingConsumeResult.Success)
                {
                    diagnostics.Report(
                        BridgeComponent.Pairing,
                        BridgeComponentState.Offline,
                        PairingErrorCode(consumeResult));
                    await SendClientErrorAsync(socket, routedDeviceId, PairingErrorCode(consumeResult), cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var offer = JsonSerializer.Deserialize<PairingOffer>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("Pairing offer is invalid.");
                if (offer.RouteId != current!.RouteId)
                {
                    await SendClientErrorAsync(socket, routedDeviceId, "pairing_invalid", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                registration = devices.RegisterForPairing(
                    offer.DeviceName,
                    offer.DevicePublicKeySpki,
                    canSend: true,
                    offer.InstallationId);
                var principal = registration.Principal;
                if (principal.DeviceId != routedDeviceId)
                    throw new InvalidDataException("Pairing identity does not match the routed device.");
                await RefreshIceServersAsync(socket, cancellationToken).ConfigureAwait(false);
                var answer = await sessions.StartWithRelayRetryAsync(
                    principal,
                    offer.OfferSdp,
                    _iceServers,
                    async token =>
                    {
                        await RefreshIceServersAsync(socket, token).ConfigureAwait(false);
                        return _iceServers;
                    },
                    cancellationToken).ConfigureAwait(false);
                var ticket = tickets.Issue(_identity!, devices.Find(principal.DeviceId)!);
                await SendSignedAnswerAsync(socket, routedDeviceId, answer, offer.OfferSdp, ticket, cancellationToken).ConfigureAwait(false);
                diagnostics.Report(BridgeComponent.Pairing, BridgeComponentState.Online);
            }
            catch (Exception exception)
            {
                if (registration is not null) devices.RollbackPairing(registration);
                var errorCode = ClassifyRemoteReconnectError(exception);
                logger.LogWarning("Remote pairing failed: {ErrorCode}", errorCode);
                diagnostics.Report(
                    BridgeComponent.Pairing,
                    BridgeComponentState.Offline,
                    errorCode);
                await SendClientErrorAsync(socket, routedDeviceId, errorCode, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _currentPairing = null;
                CurrentPairing = null;
                await RotatePairingAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        if (type == "offer")
        {
            try
            {
                await RefreshIceServersAsync(socket, cancellationToken).ConfigureAwait(false);
                var principal = devices.Authenticate(routedDeviceId);
                if (principal is null) return;
                var offerSdp = payload.GetProperty("offerSdp").GetString()!;
                var answer = await sessions.StartWithRelayRetryAsync(
                    principal,
                    offerSdp,
                    _iceServers,
                    async token =>
                    {
                        await RefreshIceServersAsync(socket, token).ConfigureAwait(false);
                        return _iceServers;
                    },
                    cancellationToken).ConfigureAwait(false);
                await SendSignedAnswerAsync(socket, routedDeviceId, answer, offerSdp, null, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Remote reconnect answer forwarded.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var errorCode = ClassifyRemoteReconnectError(exception);
                logger.LogWarning("Remote reconnect failed: {ErrorCode}", errorCode);
                await SendClientErrorAsync(socket, routedDeviceId, errorCode, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static string ClassifyRemoteReconnectError(Exception exception) => exception switch
    {
        RemoteTransportNoRelayCandidateException => "remote_answer_no_relay",
        RemoteSessionLimitException => "remote_session_limit",
        TimeoutException => "remote_timeout",
        IOException => "remote_pipe_error",
        InvalidDataException => "remote_protocol_error",
        InvalidOperationException => "remote_invalid_operation",
        OperationCanceledException => "remote_cancelled",
        _ => "remote_transport_failed",
    };

    private async Task RotatePairingAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        if (_identity is null || socket.State != WebSocketState.Open) return;
        await _pairingRotationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _currentPairing;
            if (current is not null && !pairing.IsExpired(current) &&
                CurrentPairing is not null)
                return;

            var issued = pairing.Issue();
            await SendAsync(socket, new
            {
                type = "pairing-route",
                routeId = issued.RouteId,
                expiresAt = issued.ExpiresAt,
            }, cancellationToken).ConfigureAwait(false);
            _currentPairing = issued;
            CurrentPairing = CreatePairingInfo(_identity, issued);
        }
        finally
        {
            _pairingRotationGate.Release();
        }
    }

    private Task SendClientErrorAsync(
        ClientWebSocket socket,
        string deviceId,
        string code,
        CancellationToken cancellationToken) =>
        SendAsync(socket, new
        {
            type = "forward",
            deviceId,
            payload = new { type = "error", code },
        }, cancellationToken);

    private static string PairingErrorCode(RemotePairingConsumeResult result) => result switch
    {
        RemotePairingConsumeResult.Expired => "pairing_expired",
        RemotePairingConsumeResult.Used => "pairing_used",
        RemotePairingConsumeResult.Invalid => "pairing_invalid",
        _ => "pairing_invalid",
    };

    private async Task RefreshIceServersAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        diagnostics.Report(BridgeComponent.Turn, BridgeComponentState.Starting);
        try
        {
            await SendAsync(socket, new { type = "turn-request" }, cancellationToken).ConfigureAwait(false);
            var turn = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("TURN credentials are unavailable.");
            EnsureType(turn, "turn-credentials");
            _iceServers = [new RemoteIceServer(
                turn.RootElement.GetProperty("urls").EnumerateArray().Select(item => item.GetString()!).ToArray(),
                turn.RootElement.GetProperty("username").GetString()!,
                turn.RootElement.GetProperty("credential").GetString()!)];
            _iceServersExpiresAt = turn.RootElement.TryGetProperty("expiresAt", out var expiresAt) &&
                expiresAt.TryGetDateTimeOffset(out var parsedExpiry)
                ? parsedExpiry
                : DateTimeOffset.UtcNow.AddMinutes(5);
            logger.LogInformation(
                "Remote lifecycle phase=turn_refreshed expiry_unix={ExpiryUnix} expires_in_seconds={ExpiresInSeconds} channel=unknown pipe=unknown sidecar=unknown",
                _iceServersExpiresAt.ToUnixTimeSeconds(),
                Math.Max(0, (long)(_iceServersExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
            diagnostics.Report(BridgeComponent.Turn, BridgeComponentState.Online);
        }
        catch
        {
            diagnostics.Report(
                BridgeComponent.Turn,
                BridgeComponentState.Offline,
                "turn_refresh_failed");
            throw;
        }
    }

    private async Task SendSignedAnswerAsync(
        ClientWebSocket socket, string deviceId, string answerSdp, string offerSdp,
        HostSignedRoutingTicket? ticket, CancellationToken cancellationToken)
    {
        var answerBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "answer",
            answerSdp,
            offerSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(offerSdp))).ToLowerInvariant(),
            issuedAt = DateTimeOffset.UtcNow,
        }, JsonOptions);
        var signed = SignedSignalPayload.Sign(answerBytes, _identity!);
        await SendAsync(socket, new { type = "forward", deviceId, payload = new { type = "answer", signed, ticket } }, cancellationToken);
    }

    private RemotePairingInfo CreatePairingInfo(RemoteHostIdentity identity, IssuedRemotePairing issued)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            signalUrl = options.SignalUri!.ToString(),
            hostId = identity.HostId,
            hostPublicKeySpki = identity.PublicKeySpki,
            routeId = issued.RouteId,
            secret = issued.Secret,
            issued.ExpiresAt,
        }, JsonOptions);
        var builder = new UriBuilder(options.PublicAppUri!) { Fragment = $"pair={RemoteEncoding.Base64UrlEncode(data)}" };
        return new RemotePairingInfo(builder.Uri.ToString(), issued.ExpiresAt);
    }

    private async Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > 64 * 1024) throw new InvalidDataException("Signal frame is too large.");
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024 + 1];
        var total = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Signal frame type is invalid.");
            total += result.Count;
            if (total > 64 * 1024) throw new InvalidDataException("Signal frame is too large.");
            if (result.EndOfMessage) return JsonDocument.Parse(buffer.AsMemory(0, total));
        }
    }

    private static void EnsureType(JsonDocument? document, string expected)
    {
        if (document?.RootElement.GetProperty("type").GetString() != expected)
            throw new InvalidDataException("Signal response is invalid.");
    }

    private static string ClassifySignalError(Exception exception) => exception switch
    {
        WebSocketException webSocket when FindSocketError(webSocket) is SocketError.HostNotFound => "signal_dns_failed",
        WebSocketException webSocket when FindSocketError(webSocket) is SocketError.TimedOut => "signal_connect_timeout",
        WebSocketException webSocket when webSocket.InnerException is AuthenticationException => "signal_tls_failed",
        WebSocketException => "signal_connection_failed",
        AuthenticationException => "signal_tls_failed",
        SocketException socket when socket.SocketErrorCode is SocketError.HostNotFound => "signal_dns_failed",
        SocketException socket when socket.SocketErrorCode is SocketError.TimedOut => "signal_connect_timeout",
        IOException => "signal_io_error",
        InvalidDataException => "signal_protocol_invalid",
        _ => "signal_unavailable",
    };

    private static SocketError? FindSocketError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket) return socket.SocketErrorCode;
        }
        return null;
    }

    public override void Dispose()
    {
        _identity?.Dispose();
        _sendGate.Dispose();
        _pairingRotationGate.Dispose();
        base.Dispose();
    }

    private sealed record PairingOffer(
        string RouteId,
        string DevicePublicKeySpki,
        string DeviceName,
        string OfferSdp,
        string? InstallationId = null);
}
