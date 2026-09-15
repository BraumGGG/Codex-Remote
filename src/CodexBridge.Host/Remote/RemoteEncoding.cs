namespace CodexBridge.Host.Remote;

public static class RemoteEncoding
{
    public static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new FormatException("The value is not valid base64url.");
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (value.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new FormatException("The value is not valid base64url."),
        };
        return Convert.FromBase64String(padded);
    }
}
