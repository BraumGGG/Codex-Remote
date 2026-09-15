namespace CodexBridge.Signal;

public static class PrivacySafeLogger
{
    private static readonly string[] ForbiddenNames =
        ["sdp", "ice", "secret", "ticket", "credential", "signature", "payload", "proof"];

    public static IReadOnlyDictionary<string, object?> Sanitize(
        IReadOnlyDictionary<string, object?> properties) =>
        properties
            .Where(item => !ForbiddenNames.Any(name =>
                item.Key.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}
