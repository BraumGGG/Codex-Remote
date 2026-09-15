using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class TransportPipeCodecTests
{
    [Fact]
    public async Task RoundTrip_MatchesCrossLanguageHeader()
    {
        await using var stream = new MemoryStream();
        await TransportPipeCodec.WriteAsync(
            stream,
            new TransportPipeMessage(TransportPipeMessageKind.HostData, "abc"u8.ToArray()),
            CancellationToken.None);
        Assert.Equal("0200000003616263", Convert.ToHexString(stream.ToArray()));
        stream.Position = 0;
        var decoded = await TransportPipeCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(TransportPipeMessageKind.HostData, decoded!.Kind);
        Assert.Equal("abc"u8.ToArray(), decoded.Payload.ToArray());
    }

    [Fact]
    public async Task Read_RejectsOversizedLengthBeforeAllocation()
    {
        var bytes = Convert.FromHexString("0200020001");
        await using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TransportPipeCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Diagnostic_RoundTripsWithoutNetworkAddresses()
    {
        await using var stream = new MemoryStream();
        await TransportPipeCodec.WriteAsync(
            stream,
            new TransportPipeMessage(
                TransportPipeMessageKind.Diagnostic,
                "{\"localType\":\"relay\",\"remoteType\":\"srflx\",\"protocol\":\"udp\"}"u8.ToArray()),
            CancellationToken.None);

        stream.Position = 0;
        var decoded = await TransportPipeCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(TransportPipeMessageKind.Diagnostic, decoded!.Kind);
        Assert.DoesNotContain("address", System.Text.Encoding.UTF8.GetString(decoded.Payload.Span));
    }

    [Fact]
    public void IceStateDiagnostic_AcceptsKnownStateWithoutAddresses()
    {
        var diagnostic = new RemoteTransportDiagnostic("ice-state", "checking", null, null, null);

        diagnostic.Validate();
        Assert.Throws<InvalidDataException>(() =>
            new RemoteTransportDiagnostic("ice-state", "unknown", null, null, null).Validate());
    }

    [Fact]
    public void IceCandidateSummary_ContainsTypesButNoAddresses()
    {
        const string sdp = "a=candidate:1 1 udp 1 192.168.1.3 5000 typ host\r\n" +
                           "a=candidate:2 1 udp 1 192.0.2.1 49160 typ relay raddr 0.0.0.0 rport 0\r\n";

        var summary = RemoteSessionFactory.SummarizeCandidateTypes(sdp);

        Assert.Equal("host,relay", summary);
        Assert.DoesNotContain("192.168", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("129.211", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host", "host", "host")]
    [InlineData("host", "srflx", "srflx")]
    [InlineData("relay", "host", "relay")]
    [InlineData(null, null, "unknown")]
    public void CandidateTelemetry_ReportsOnlyBoundedType(
        string? localType,
        string? remoteType,
        string expected) =>
        Assert.Equal(expected, RemoteSessionFactory.ClassifyCandidateType(localType, remoteType));

    [Fact]
    public void OfferPayload_UsesPionIceServerPropertyNames()
    {
        var payload = PipeRemoteFrameTransport.EncodeOfferPayload(
            "offer",
            [new RemoteIceServer(["turn:relay.example:3478", "turns:relay.example:5349?transport=tcp"], "user", "secret")]);
        var json = System.Text.Encoding.UTF8.GetString(payload);

        Assert.Contains("\"iceServers\"", json, StringComparison.Ordinal);
        Assert.Contains("\"urls\"", json, StringComparison.Ordinal);
        Assert.Contains("\"username\"", json, StringComparison.Ordinal);
        Assert.Contains("\"credential\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Urls\"", json, StringComparison.Ordinal);
        Assert.Contains("turn:relay.example:3478", json, StringComparison.Ordinal);
        Assert.Contains("turns:relay.example:5349?transport=tcp", json, StringComparison.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var urls = document.RootElement.GetProperty("iceServers")[0].GetProperty("urls");
        Assert.Equal(2, urls.GetArrayLength());
    }

    [Fact]
    public void RelayRetry_UsesOnlyUdpTurnGenerations()
    {
        var attempts = RemoteSessionFactory.BuildIceServerAttempts(
            [new RemoteIceServer([
                "turn:relay.example:3478?transport=udp",
                "turn:relay.example:3478?transport=tcp",
                "turns:relay.example:5349?transport=tcp",
            ], "user", "secret")]);

        Assert.Equal(3, attempts.Count);
        Assert.All(attempts, attempt => Assert.Single(Assert.Single(attempt).Urls));
        Assert.All(attempts, attempt =>
        {
            Assert.StartsWith("turn:", attempt[0].Urls[0], StringComparison.Ordinal);
            Assert.DoesNotContain("transport=tcp", attempt[0].Urls[0], StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void RelayRetry_ReturnsEmptyAttemptWhenOnlyTcpTurnIsAdvertised()
    {
        var attempts = RemoteSessionFactory.BuildIceServerAttempts(
            [new RemoteIceServer(["turns:relay.example:5349?transport=tcp"], "user", "secret")]);

        Assert.Single(attempts);
        Assert.Empty(attempts[0]);
    }
}
