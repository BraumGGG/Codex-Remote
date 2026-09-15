using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBridge.Host.Remote;

public sealed class RemotePipeAuthenticationException(string message) : Exception(message);

public static class RemotePipeAuthentication
{
    public static string CreateProof(byte[] secret, string role, string challenge, int protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var message = Encoding.UTF8.GetBytes($"codex-bridge-pipe-v1|{role}|{protocolVersion}|{challenge}");
        return RemoteEncoding.Base64UrlEncode(HMACSHA256.HashData(secret, message));
    }

    public static bool VerifyProof(
        byte[] secret,
        string role,
        string challenge,
        int protocolVersion,
        string suppliedProof)
    {
        try
        {
            var expected = RemoteEncoding.Base64UrlDecode(CreateProof(secret, role, challenge, protocolVersion));
            var supplied = RemoteEncoding.Base64UrlDecode(suppliedProof);
            return CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class AuthenticatedRemotePipe(NamedPipeServerStream stream) : IAsyncDisposable
{
    public Stream Stream => stream;
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

public interface IRemotePipeAcceptor : IAsyncDisposable
{
    Task<AuthenticatedRemotePipe> AcceptAuthenticatedAsync(CancellationToken cancellationToken = default);
}

public interface IRemotePipeServerFactory
{
    IRemotePipeAcceptor Create(string pipeName, byte[] secret);
}

public sealed class RemotePipeServerFactory(TimeSpan authenticationTimeout) : IRemotePipeServerFactory
{
    public IRemotePipeAcceptor Create(string pipeName, byte[] secret) =>
        new RemotePipeServer(pipeName, secret, authenticationTimeout);
}

public sealed class RemotePipeServer : IRemotePipeAcceptor
{
    private const int ProtocolVersion = 1;
    private readonly byte[] _secret;
    private readonly TimeSpan _timeout;
    private NamedPipeServerStream? _stream;
    private bool _transferred;

    public RemotePipeServer(string pipeName, byte[] secret, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (secret is null || secret.Length != 32) throw new ArgumentException("Pipe secret must be 256 bits.", nameof(secret));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _secret = secret.ToArray();
        _timeout = timeout;
        _stream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public async Task<AuthenticatedRemotePipe> AcceptAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(RemotePipeServer));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await stream.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var challenge = RemoteEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "challenge",
                protocolVersion = ProtocolVersion,
                challenge,
            })).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null) throw new RemotePipeAuthenticationException("Pipe client disconnected during authentication.");
            PipeAuthenticationRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<PipeAuthenticationRequest>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                throw new RemotePipeAuthenticationException("Pipe authentication message is invalid.");
            }

            if (request?.Type != "authenticate" ||
                request.ProtocolVersion != ProtocolVersion ||
                !RemotePipeAuthentication.VerifyProof(
                    _secret, "client", challenge, request.ProtocolVersion, request.Proof ?? string.Empty))
            {
                throw new RemotePipeAuthenticationException("Pipe client authentication failed.");
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "authenticated",
                protocolVersion = ProtocolVersion,
                proof = RemotePipeAuthentication.CreateProof(_secret, "host", challenge, ProtocolVersion),
            })).ConfigureAwait(false);
            _transferred = true;
            return new AuthenticatedRemotePipe(stream);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Pipe authentication timed out.");
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_secret);
        if (!_transferred && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        _stream = null;
    }

    private sealed record PipeAuthenticationRequest(string? Type, int ProtocolVersion, string? Proof);
}
