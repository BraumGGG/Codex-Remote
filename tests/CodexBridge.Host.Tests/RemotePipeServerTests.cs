using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class RemotePipeServerTests
{
    [Fact]
    public void AuthenticationProof_MatchesCrossLanguageVector()
    {
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        Assert.Equal(
            "GG2gn3xdfPbtUz0YijMyEa2jI6uJhGkkpDqLg_-daSc",
            RemotePipeAuthentication.CreateProof(
                secret,
                "client",
                "AQIDBAUGBwgJCgsMDQ4PEA",
                1));
    }

    [Fact]
    public async Task AcceptAuthenticated_PerformsMutualHmacHandshake()
    {
        var pipeName = $"codex-bridge-test-{Guid.NewGuid():N}";
        var secret = RandomNumberGenerator.GetBytes(32);
        await using var server = new RemotePipeServer(pipeName, secret, TimeSpan.FromSeconds(5));
        var accept = server.AcceptAuthenticatedAsync();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var reader = new StreamReader(client, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var challengeLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var challenge = JsonDocument.Parse(challengeLine!).RootElement;
        var nonce = challenge.GetProperty("challenge").GetString()!;
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "authenticate",
            protocolVersion = 1,
            proof = RemotePipeAuthentication.CreateProof(secret, "client", nonce, 1),
        }));
        await writer.FlushAsync();
        var responseTask = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var responseLine = await responseTask;
        var response = JsonDocument.Parse(responseLine!).RootElement;

        Assert.Equal("authenticated", response.GetProperty("type").GetString());
        Assert.True(RemotePipeAuthentication.VerifyProof(
            secret,
            "host",
            nonce,
            1,
            response.GetProperty("proof").GetString()!));
        await accepted.DisposeAsync();
    }

    [Fact]
    public async Task AcceptAuthenticated_RejectsForgedClientAndVersionMismatch()
    {
        await AssertRejectedAsync(RandomNumberGenerator.GetBytes(32), protocolVersion: 1);
        var secret = RandomNumberGenerator.GetBytes(32);
        await AssertRejectedAsync(secret, protocolVersion: 2, clientSecret: secret);
    }

    private static async Task AssertRejectedAsync(byte[] serverSecret, int protocolVersion, byte[]? clientSecret = null)
    {
        var pipeName = $"codex-bridge-test-{Guid.NewGuid():N}";
        await using var server = new RemotePipeServer(pipeName, serverSecret, TimeSpan.FromSeconds(5));
        var accept = server.AcceptAuthenticatedAsync();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var reader = new StreamReader(client, Encoding.UTF8, false, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var challengeLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var nonce = JsonDocument.Parse(challengeLine!).RootElement.GetProperty("challenge").GetString()!;
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "authenticate",
            protocolVersion,
            proof = RemotePipeAuthentication.CreateProof(clientSecret ?? RandomNumberGenerator.GetBytes(32), "client", nonce, protocolVersion),
        }));
        await writer.FlushAsync();
        await Assert.ThrowsAsync<RemotePipeAuthenticationException>(async () =>
            await accept.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
