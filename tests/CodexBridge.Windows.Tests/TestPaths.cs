namespace CodexBridge.Windows.Tests;

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
        var repository = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var root = Path.Combine(repository, ".test-state", "windows");
        Directory.CreateDirectory(root);
        return root;
    }
}
