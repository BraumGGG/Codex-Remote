namespace CodexBridge.Host.Tests;

internal static class TestPaths
{
    private static readonly string Root = ResolveRoot();

    public static string CreateDirectory(string prefix)
    {
        var path = Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("CODEX_BRIDGE_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configured = Path.GetFullPath(configuredRoot);
            Directory.CreateDirectory(configured);
            return configured;
        }
        var repository = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var root = Path.Combine(repository, ".test-state", "host");
        Directory.CreateDirectory(root);
        return root;
    }
}
