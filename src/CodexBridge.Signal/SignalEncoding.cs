namespace CodexBridge.Signal;

public static class SignalEncoding
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_') ||
            value.Length % 4 == 1)
            throw new FormatException("Invalid base64url value.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (value.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
