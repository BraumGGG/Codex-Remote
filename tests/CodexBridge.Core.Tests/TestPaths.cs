namespace CodexBridge.Core.Tests;

internal static class TestPaths
{
    private static readonly string Root = ResolveRoot();

    public static string CreateDirectory(string prefix)
    {
        var path = Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string PathFor(string name) => Path.Combine(Root, name);

    private static string ResolveRoot()
    {
        var repository = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var root = Path.Combine(repository, ".test-state", "core");
        Directory.CreateDirectory(root);
        return root;
    }
}
