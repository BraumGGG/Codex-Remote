using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexBridge.Host.Remote;

public sealed record TransportVerificationResult(bool IsValid, string? ErrorCode, int ProtocolVersion)
{
    public static TransportVerificationResult Valid(int protocolVersion) => new(true, null, protocolVersion);
    public static TransportVerificationResult Invalid(string errorCode) => new(false, errorCode, 0);
}

public interface IAuthenticodeVerifier
{
    bool IsTrusted(string path);
}

public interface ITransportIntegrityGate
{
    TransportVerificationResult Verify();
}

public sealed class ConfiguredTransportIntegrityGate(
    TransportIntegrityVerifier verifier,
    string executablePath,
    string manifestPath,
    bool requireAuthenticode) : ITransportIntegrityGate
{
    public TransportVerificationResult Verify() =>
        verifier.Verify(executablePath, manifestPath, requireAuthenticode);
}

public sealed class TransportIntegrityVerifier(IAuthenticodeVerifier authenticodeVerifier)
{
    public TransportVerificationResult Verify(
        string executablePath,
        string manifestPath,
        bool requireAuthenticode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!File.Exists(executablePath)) return TransportVerificationResult.Invalid("executable_missing");
        if (!File.Exists(manifestPath)) return TransportVerificationResult.Invalid("manifest_missing");

        TransportManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TransportManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException();
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            return TransportVerificationResult.Invalid("manifest_invalid");
        }

        if (manifest.ProtocolVersion != 1 ||
            !string.Equals(manifest.FileName, Path.GetFileName(executablePath), StringComparison.Ordinal) ||
            manifest.Sha256.Length != 64)
        {
            return TransportVerificationResult.Invalid("manifest_invalid");
        }

        string actualHash;
        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return TransportVerificationResult.Invalid("executable_unavailable");
        }

        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return TransportVerificationResult.Invalid("hash_mismatch");
        }

        if (requireAuthenticode && !authenticodeVerifier.IsTrusted(executablePath))
        {
            return TransportVerificationResult.Invalid("signature_invalid");
        }

        return TransportVerificationResult.Valid(manifest.ProtocolVersion);
    }

    private sealed record TransportManifest(int ProtocolVersion, string FileName, string Sha256);
}

public sealed class WinTrustAuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public bool IsTrusted(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        using var fileInfo = new WinTrustFileInfo(path);
        using var trustData = new WinTrustData(fileInfo.Pointer);
        var action = GenericVerifyV2;
        return WinVerifyTrust(IntPtr.Zero, ref action, trustData.Pointer) == 0;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);

    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly IntPtr _path;
        public IntPtr Pointer { get; }

        public WinTrustFileInfo(string path)
        {
            _path = Marshal.StringToCoTaskMemUni(path);
            var value = new FileInfoNative
            {
                Size = (uint)Marshal.SizeOf<FileInfoNative>(),
                FilePath = _path,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<FileInfoNative>());
            Marshal.StructureToPtr(value, Pointer, false);
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Pointer);
            Marshal.FreeCoTaskMem(_path);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfoNative
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }
    }

    private sealed class WinTrustData : IDisposable
    {
        public IntPtr Pointer { get; }

        public WinTrustData(IntPtr fileInfo)
        {
            var value = new TrustDataNative
            {
                Size = (uint)Marshal.SizeOf<TrustDataNative>(),
                UIChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfo,
                StateAction = 0,
                ProviderFlags = 0x00000010,
                UIContext = 0,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustDataNative>());
            Marshal.StructureToPtr(value, Pointer, false);
        }

        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustDataNative
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UIChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UIContext;
            public IntPtr SignatureSettings;
        }
    }
}
