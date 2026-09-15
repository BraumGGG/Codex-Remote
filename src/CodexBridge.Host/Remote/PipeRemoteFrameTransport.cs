using System.Text;
using System.Text.Json;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Remote;

public sealed class PipeRemoteFrameTransport : IRemoteFrameTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Stream _stream;
    private readonly Action<TransportPipeMessageKind>? _messageObserver;
    private readonly Action<RemoteTransportDiagnostic>? _diagnosticObserver;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _configured;
    private bool _disposed;

    public PipeRemoteFrameTransport(
        Stream stream,
        Action<TransportPipeMessageKind>? messageObserver = null,
        Action<RemoteTransportDiagnostic>? diagnosticObserver = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _messageObserver = messageObserver;
        _diagnosticObserver = diagnosticObserver;
    }

    public async Task<string> AcceptOfferAsync(string offerSdp, CancellationToken cancellationToken = default)
        => await AcceptOfferAsync(offerSdp, [], cancellationToken).ConfigureAwait(false);

    public async Task<string> AcceptOfferAsync(
        string offerSdp,
        IReadOnlyList<RemoteIceServer> iceServers,
        CancellationToken cancellationToken = default)
    {
        if (_configured) throw new InvalidOperationException("Remote transport is already configured.");
        if (string.IsNullOrWhiteSpace(offerSdp)) throw new ArgumentException("Offer SDP is required.", nameof(offerSdp));
        var payload = EncodeOfferPayload(offerSdp, iceServers);
        await WritePipeAsync(
            new TransportPipeMessage(TransportPipeMessageKind.Offer, payload),
            cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var response = await TransportPipeCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Transport closed before returning an answer.");
            _messageObserver?.Invoke(response.Kind);
            if (response.Kind == TransportPipeMessageKind.Diagnostic)
            {
                ObserveDiagnostic(response.Payload.Span);
                continue;
            }
            if (response.Kind == TransportPipeMessageKind.Error)
                throw new InvalidOperationException("Transport rejected the WebRTC offer.");
            if (response.Kind != TransportPipeMessageKind.Answer)
                throw new InvalidDataException("Transport returned an unexpected offer response.");
            _configured = true;
            return Encoding.UTF8.GetString(response.Payload.Span);
        }
    }

    internal static byte[] EncodeOfferPayload(string offerSdp, IReadOnlyList<RemoteIceServer> iceServers)
    {
        if (iceServers.Count == 0) return Encoding.UTF8.GetBytes(offerSdp);

        var entries = iceServers
            .Where(server => server.Urls.Any(url => !string.IsNullOrWhiteSpace(url)))
            .Select(server => new
            {
                urls = server.Urls.Where(url => !string.IsNullOrWhiteSpace(url)).ToArray(),
                username = server.Username,
                credential = server.Credential,
            })
            .ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new { sdp = offerSdp, iceServers = entries }, JsonOptions);
    }

    public async IAsyncEnumerable<RemoteFrame> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_configured) throw new InvalidOperationException("Remote transport has no accepted offer.");
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await TransportPipeCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (message is null || message.Kind == TransportPipeMessageKind.ChannelClosed) yield break;
            _messageObserver?.Invoke(message.Kind);
            if (message.Kind == TransportPipeMessageKind.ChannelOpen) continue;
            if (message.Kind == TransportPipeMessageKind.Diagnostic)
            {
                ObserveDiagnostic(message.Payload.Span);
                continue;
            }
            if (message.Kind == TransportPipeMessageKind.Error)
                throw new IOException("Transport sidecar reported a channel error.");
            if (message.Kind != TransportPipeMessageKind.RemoteData)
                throw new InvalidDataException("Unexpected transport pipe message.");
            yield return RemoteFrameCodec.Decode(message.Payload.Span);
        }
    }

    private void ObserveDiagnostic(ReadOnlySpan<byte> payload)
    {
        var diagnostic = JsonSerializer.Deserialize<RemoteTransportDiagnostic>(payload, JsonOptions)
            ?? throw new InvalidDataException("Transport diagnostic is invalid.");
        diagnostic.Validate();
        _diagnosticObserver?.Invoke(diagnostic);
    }

    public Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken) =>
        WritePipeAsync(
            new TransportPipeMessage(
                TransportPipeMessageKind.HostData,
                RemoteFrameCodec.Encode(frame)),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await WritePipeAsync(
                new TransportPipeMessage(TransportPipeMessageKind.Close, ReadOnlyMemory<byte>.Empty),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        _writeGate.Dispose();
    }

    private async Task WritePipeAsync(
        TransportPipeMessage message,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await TransportPipeCodec.WriteAsync(_stream, message, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }
}

public sealed record RemoteIceServer(string[] Urls, string Username, string Credential);

public sealed record RemoteTransportDiagnostic(
    string? Event,
    string? State,
    string? LocalType,
    string? RemoteType,
    string? Protocol)
{
    private static readonly HashSet<string> CandidateTypes = ["host", "srflx", "prflx", "relay"];
    private static readonly HashSet<string> Protocols = ["udp", "tcp"];
    private static readonly HashSet<string> IceStates = ["new", "checking", "connected", "completed", "disconnected", "failed", "closed"];

    public void Validate()
    {
        if (Event == "ice-state" && State is not null && IceStates.Contains(State)) return;
        if (Event == "ice-config" && State is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(State, "^servers_[0-9]+_urls_[0-9]+$")) return;
        if (Event == "offer-error" && State is "peer_create_failed" or "set_remote_failed" or
            "create_answer_failed" or "set_local_failed" or "offer_accept_failed" or
            "no_relay_answer" or "offer_final_failed") return;
        if (Event is null && LocalType is not null && RemoteType is not null && Protocol is not null &&
            CandidateTypes.Contains(LocalType) && CandidateTypes.Contains(RemoteType) && Protocols.Contains(Protocol)) return;
        if (Event == "selected" && LocalType is not null && RemoteType is not null && Protocol is not null &&
            CandidateTypes.Contains(LocalType) && CandidateTypes.Contains(RemoteType) && Protocols.Contains(Protocol)) return;
        throw new InvalidDataException("Transport diagnostic contains an invalid value.");
    }
}
