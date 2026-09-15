using CodexBridge.Core;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Services;
using System.Text.RegularExpressions;
using CodexBridge.Host.Diagnostics;

namespace CodexBridge.Host.Remote;

public sealed class RemoteSessionFactory(
    RemoteAccessOptions options,
    IServiceProvider services,
    TimeProvider timeProvider,
    RemoteSessionConcurrencyGate concurrency,
    RemoteTelemetryReporter telemetry,
    ILogger<RemoteSessionFactory> logger)
{
    private static long _nextGeneration;

    public async Task<string> StartWithRelayRetryAsync(
        DevicePrincipal principal,
        string offerSdp,
        IReadOnlyList<RemoteIceServer> iceServers,
        CancellationToken cancellationToken)
        => await StartWithRelayRetryAsync(principal, offerSdp, iceServers, null, cancellationToken).ConfigureAwait(false);

    public async Task<string> StartWithRelayRetryAsync(
        DevicePrincipal principal,
        string offerSdp,
        IReadOnlyList<RemoteIceServer> iceServers,
        Func<CancellationToken, Task<IReadOnlyList<RemoteIceServer>>>? refreshIceServers,
        CancellationToken cancellationToken)
    {
        var attempts = BuildIceServerAttempts(iceServers);
        for (var attempt = 0; ; attempt++)
        {
            var currentIceServers = attempts[Math.Min(attempt, attempts.Count - 1)];
            logger.LogInformation(
                "Remote lifecycle phase=sidecar_attempt attempt={Attempt} total={TotalAttempts} channel=unknown pipe=unknown sidecar=starting",
                attempt + 1,
                attempts.Count);
            try
            {
                return await StartAsync(principal, offerSdp, currentIceServers, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteTransportNoRelayCandidateException) when (attempt < attempts.Count - 1 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Remote lifecycle phase=answer_retry attempt={Attempt} total={TotalAttempts} error=transport_answer_no_relay_candidate channel=unknown pipe=unknown sidecar=stopped",
                    attempt + 1,
                    attempts.Count);
                if (refreshIceServers is not null)
                {
                    var refreshed = await refreshIceServers(cancellationToken).ConfigureAwait(false);
                    attempts = BuildIceServerAttempts(refreshed);
                }
            }
        }
    }

    internal static List<IReadOnlyList<RemoteIceServer>> BuildIceServerAttempts(
        IReadOnlyList<RemoteIceServer> servers)
    {
        var valid = servers
            .Where(server => server.Urls.Any(url => !string.IsNullOrWhiteSpace(url)))
            .ToArray();
        if (valid.Length == 0) return [valid];
        // Do not fan out all TURN URLs in one answerer. Each URL can create a
        // relay allocation; parallel allocations exhaust coturn per-user
        // quota and poison the following fallback generations.
        var attempts = new List<IReadOnlyList<RemoteIceServer>>();
        var urls = valid
            .SelectMany(server => server.Urls.Select(url => (server, url)))
            .Where(item => !string.IsNullOrWhiteSpace(item.url))
            // Production coturn deliberately disables TCP relay. Do not start
            // generations that can never produce a relay answer; they only
            // consume time and leave stale allocations during reconnect.
            .Where(item => IsUdpTurnUrl(item.url))
            .OrderByDescending(item => IsUdpTurnUrl(item.url))
            .ToArray();
        foreach (var item in urls)
            attempts.Add([new RemoteIceServer([item.url], item.server.Username, item.server.Credential)]);
        if (attempts.Count == 0) attempts.Add([]);
        // A single UDP TURN route still needs bounded full-generation retry:
        // coturn may release the previous allocation asynchronously after a
        // mobile cold start. Reusing the same route is safe because each
        // retry receives fresh credentials from Signal.
        if (attempts.Count == 1 && attempts[0].Count > 0)
        {
            attempts.Add(attempts[0]);
            attempts.Add(attempts[0]);
        }
        return attempts;
    }

    private static bool IsUdpTurnUrl(string url) =>
        url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("?transport=tcp", StringComparison.OrdinalIgnoreCase);

    public async Task<string> StartAsync(
        DevicePrincipal principal,
        string offerSdp,
        IReadOnlyList<RemoteIceServer> iceServers,
        CancellationToken cancellationToken)
    {
        if (offerSdp.Length is < 1 or > 96 * 1024) throw new InvalidDataException("WebRTC offer is invalid.");
        var generation = Interlocked.Increment(ref _nextGeneration);
        var concurrencyLease = concurrency.AcquireCandidate(principal.DeviceId, cancellationToken);
        logger.LogInformation("Remote lifecycle generation={Generation} phase=lease_acquired channel=unknown pipe=unknown sidecar=unknown", generation);
        logger.LogInformation("Remote offer ICE candidates: {CandidateTypes}", SummarizeCandidateTypes(offerSdp));
        var integrity = new ConfiguredTransportIntegrityGate(
            new TransportIntegrityVerifier(new WinTrustAuthenticodeVerifier()),
            options.TransportExecutablePath,
            options.TransportManifestPath,
            options.RequireAuthenticode);
        var diagnostics = services.GetRequiredService<BridgeDiagnosticsService>();
        var manager = new TransportProcessManager(
            integrity,
            new WindowsTransportProcessLauncher(options.TransportExecutablePath),
            new RemotePipeServerFactory(TimeSpan.FromSeconds(10)),
            timeProvider,
            lifecycleObserver: state => ObserveSidecarLifecycle(diagnostics, state));
        var stage = "sidecar_start";
        try
        {
            diagnostics.Report(BridgeComponent.Sidecar, BridgeComponentState.Starting);
            logger.LogInformation("Remote lifecycle generation={Generation} phase=sidecar_start channel=unknown pipe=starting sidecar=starting", generation);
            await manager.StartAsync(cancellationToken).ConfigureAwait(false);
            diagnostics.Report(BridgeComponent.Sidecar, BridgeComponentState.Online);
            logger.LogInformation("Remote lifecycle generation={Generation} phase=sidecar_authenticated channel=unknown pipe=authenticated sidecar=online", generation);
            stage = "offer_accept";
            var transport = new PipeRemoteFrameTransport(
                manager.GetAuthenticatedStream(),
                messageObserver: kind =>
                {
                    if (kind is TransportPipeMessageKind.ChannelOpen or
                        TransportPipeMessageKind.ChannelClosed or
                        TransportPipeMessageKind.Error)
                        logger.LogInformation("Remote lifecycle generation={Generation} phase=transport_state channel={ChannelState} pipe=authenticated sidecar=online", generation, kind);
                },
                diagnosticObserver: diagnostic =>
                {
                    if (diagnostic.Event == "ice-state")
                    {
                        logger.LogInformation("Remote lifecycle generation={Generation} phase=ice_{IceState} channel=unknown pipe=authenticated sidecar=online", generation, diagnostic.State);
                        if (diagnostic.State is "failed" or "disconnected")
                            diagnostics.Report(
                                BridgeComponent.Sidecar,
                                BridgeComponentState.Degraded,
                                $"ice_{diagnostic.State}");
                        else if (diagnostic.State is "connected" or "completed")
                            diagnostics.Report(BridgeComponent.Sidecar, BridgeComponentState.Online);
                    }
                    else if (diagnostic.Event == "ice-config")
                    {
                        logger.LogInformation(
                            "Remote lifecycle generation={Generation} phase=ice_config state={ConfigState} channel=unknown pipe=authenticated sidecar=online",
                            generation,
                            diagnostic.State);
                    }
                    else if (diagnostic.Event == "offer-error")
                    {
                        logger.LogWarning(
                            "Remote lifecycle generation={Generation} phase=offer_error error={ErrorCode} channel=unknown pipe=authenticated sidecar=online",
                            generation,
                            diagnostic.State);
                    }
                    else
                    {
                        logger.LogInformation("Remote lifecycle generation={Generation} phase=ice_selected channel=unknown pipe=authenticated sidecar=online", generation);
                        telemetry.Report("connected", ClassifyCandidateType(
                            diagnostic.LocalType, diagnostic.RemoteType));
                    }
                });
            var answer = await transport.AcceptOfferAsync(offerSdp, iceServers, cancellationToken).ConfigureAwait(false);
            stage = "session_setup";
            logger.LogInformation(
                "Remote transport answer created with ICE candidates: {CandidateTypes}",
                SummarizeCandidateTypes(answer));
            if (iceServers.Count > 0 && !SummarizeCandidateTypes(answer).Contains("relay", StringComparison.Ordinal))
                throw new RemoteTransportNoRelayCandidateException();
            // Keep the previous healthy session alive until this replacement has
            // produced a valid relay answer and authenticated transport.
            concurrency.CommitCandidate(concurrencyLease);
            var catalog = services.GetRequiredService<IThreadCatalog>();
            var policy = services.GetRequiredService<TargetPolicy>();
            var stream = services.GetRequiredService<ConversationStreamService>();
            var files = services.GetRequiredService<TextFileAttachmentService>();
            var images = services.GetRequiredService<ImageAttachmentService>();
            var submissions = new RemoteMessageSubmissionService(
                services.GetRequiredService<RemoteCommandReceiptStore>(),
                services.GetRequiredService<MessageSubmissionService>());
            var dispatcher = new RemoteRpcDispatcher(
                services.GetRequiredService<WorkspaceQueryService>(), images, files,
                services.GetRequiredService<DesktopStatusService>(), submissions,
                services.GetRequiredService<IRemoteCapabilityResolver>());
            var connection = new RemoteConnectionService(
                dispatcher,
                new RemoteSubscriptionRegistry(catalog, policy, stream),
                new RemoteAttachmentStreamer(new HostRemoteAttachmentSource(
                    images,
                    files,
                    services.GetRequiredService<WorkspaceQueryService>())),
                services.GetRequiredService<ILogger<RemoteConnectionService>>());
            var revocationLease = new RemoteDeviceSessionLease(
                services.GetRequiredService<RemoteDeviceStore>(),
                principal.DeviceId,
                concurrencyLease.Token);
            logger.LogInformation("Remote lifecycle generation={Generation} phase=connection_start channel=open pipe=authenticated sidecar=online", generation);
            _ = RunConnectionAsync(
                connection,
                principal,
                transport,
                manager,
                revocationLease,
                concurrencyLease,
                diagnostics,
                generation);
            return answer;
        }
        catch (Exception exception)
        {
            telemetry.Report("failed", "unknown");
            concurrencyLease.Dispose();
            diagnostics.Report(
                BridgeComponent.Sidecar,
                manager.IsCircuitOpen ? BridgeComponentState.CircuitOpen : BridgeComponentState.Offline,
                manager.IsCircuitOpen ? "sidecar_circuit_open" : ClassifySidecarError(exception));
            logger.LogWarning("Remote lifecycle generation={Generation} phase={Stage} error={ErrorCode} channel=unknown pipe=unknown sidecar={SidecarState}",
                generation, stage, ClassifyOperationalError(exception), manager.IsCircuitOpen ? "circuit_open" : "offline");
            try
            {
                await manager.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    "Remote session cleanup failed: {ErrorCode}",
                    ClassifyOperationalError(cleanupException));
            }
            throw;
        }
    }

    internal static string ClassifyCandidateType(string? localType, string? remoteType)
    {
        var types = new[] { localType, remoteType };
        if (types.Contains("relay", StringComparer.Ordinal)) return "relay";
        if (types.Any(type => type is "srflx" or "prflx")) return "srflx";
        if (types.Contains("host", StringComparer.Ordinal)) return "host";
        return "unknown";
    }

    internal static string SummarizeCandidateTypes(string sdp)
    {
        var types = Regex.Matches(
                sdp,
                @"(?m)^a=candidate:[^\r\n]*\styp\s(?<type>host|srflx|prflx|relay)(?:\s|$)",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["type"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return types.Length == 0 ? "none" : string.Join(',', types);
    }

    private static string ClassifyOperationalError(Exception exception)
    {
        const string integrityPrefix = "Transport integrity check failed: ";
        if (exception is InvalidOperationException &&
            exception.Message.StartsWith(integrityPrefix, StringComparison.Ordinal))
        {
            var code = exception.Message[integrityPrefix.Length..];
            return code is "executable_missing" or "manifest_missing" or "manifest_invalid" or
                "executable_unavailable" or "hash_mismatch" or "signature_invalid"
                ? $"transport_integrity_{code}"
                : "transport_integrity_invalid";
        }

        return exception switch
        {
            RemoteTransportNoRelayCandidateException => "transport_answer_no_relay_candidate",
            InvalidOperationException when exception.Message == "Transport process could not be started."
                => "transport_process_start_failed",
            TimeoutException => "transport_pipe_timeout",
            RemotePipeAuthenticationException => "transport_pipe_authentication_failed",
            System.ComponentModel.Win32Exception win32 => $"transport_win32_{win32.NativeErrorCode}",
            IOException => "transport_io_error",
            InvalidDataException => "transport_protocol_invalid",
            OperationCanceledException => "transport_cancelled",
            _ => exception.GetType().Name,
        };
    }

    private static string ClassifySidecarError(Exception exception)
    {
        var operational = ClassifyOperationalError(exception);
        var normalized = Regex.Replace(
            operational,
            "[^A-Za-z0-9]+",
            "_",
            RegexOptions.CultureInvariant).Trim('_').ToLowerInvariant();
        return normalized.Length is > 0 and <= 55
            ? $"sidecar_{normalized}"
            : "sidecar_unavailable";
    }

    internal static void ObserveSidecarLifecycle(
        BridgeDiagnosticsService diagnostics,
        TransportLifecycleState state)
    {
        switch (state)
        {
            case TransportLifecycleState.Starting:
                diagnostics.Report(BridgeComponent.Sidecar, BridgeComponentState.Starting);
                break;
            case TransportLifecycleState.Online:
                diagnostics.Report(BridgeComponent.Sidecar, BridgeComponentState.Online);
                break;
            case TransportLifecycleState.Restarting:
                diagnostics.Report(
                    BridgeComponent.Sidecar,
                    BridgeComponentState.Degraded,
                    "sidecar_restarting");
                break;
            case TransportLifecycleState.CircuitOpen:
                diagnostics.Report(
                    BridgeComponent.Sidecar,
                    BridgeComponentState.CircuitOpen,
                    "sidecar_circuit_open");
                break;
            case TransportLifecycleState.Stopped:
                var current = diagnostics.Capture().Sidecar;
                if (current.State is BridgeComponentState.Offline or BridgeComponentState.CircuitOpen)
                    break;
                diagnostics.Report(
                    BridgeComponent.Sidecar,
                    BridgeComponentState.Offline);
                break;
        }
    }

    private async Task RunConnectionAsync(
        RemoteConnectionService connection,
        DevicePrincipal principal,
        PipeRemoteFrameTransport transport,
        TransportProcessManager manager,
        RemoteDeviceSessionLease revocationLease,
        RemoteSessionConcurrencyLease concurrencyLease,
        BridgeDiagnosticsService diagnostics,
        long generation)
    {
        try
        {
            await connection.RunAsync(principal, transport, revocationLease.Token).ConfigureAwait(false);
            logger.LogInformation("Remote lifecycle generation={Generation} phase=session_end error=channel_closed channel=closed pipe=closed sidecar=stopping", generation);
        }
        catch (Exception exception)
        {
            var code = ClassifyConnectionEnd(exception);
            diagnostics.Report(BridgeComponent.Sidecar, BridgeComponentState.Degraded, code);
            if (code == "remote_session_unhandled")
                logger.LogWarning("Remote lifecycle generation={Generation} phase=session_end error={ErrorCode} channel=error pipe=closed sidecar=stopping", generation, code);
            else
                logger.LogInformation("Remote lifecycle generation={Generation} phase=session_end error={ErrorCode} channel=closed pipe=closed sidecar=stopping", generation, code);
        }
        finally
        {
            revocationLease.Dispose();
            await manager.DisposeAsync().ConfigureAwait(false);
            concurrencyLease.Dispose();
            logger.LogInformation("Remote lifecycle generation={Generation} phase=lease_released channel=closed pipe=closed sidecar=stopped", generation);
        }
    }

    internal static string ClassifyConnectionEnd(Exception exception) => exception switch
    {
        OperationCanceledException => "remote_session_cancelled",
        EndOfStreamException or IOException => "remote_transport_closed",
        InvalidDataException => "remote_protocol_invalid",
        System.Text.Json.JsonException => "remote_payload_invalid",
        _ => "remote_session_unhandled",
    };
}
