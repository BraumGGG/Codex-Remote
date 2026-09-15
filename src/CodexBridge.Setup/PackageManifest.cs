using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBridge.Setup;

public sealed record PackageFile(string Path, long Length, string Sha256);

public sealed record PackageManifest(int SchemaVersion, string ProductVersion, IReadOnlyList<PackageFile> Files)
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "package-manifest.json";

    public static PackageManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), PackageJsonContext.Default.PackageManifest)
            ?? throw new InvalidDataException("安装包 manifest 内容无效。");
        if (manifest.SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(manifest.ProductVersion))
            throw new InvalidDataException("安装包 manifest 版本不受支持。");
        if (manifest.Files.Count == 0) throw new InvalidDataException("安装包不包含文件。");
        var normalizedFiles = manifest.Files.Select(file => file with { Path = NormalizePath(file.Path) }).ToArray();
        var duplicate = normalizedFiles.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"安装包包含重复路径：{duplicate.Key}");
        if (normalizedFiles.Any(file => string.Equals(file.Path, FileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("安装包文件列表不能覆盖 manifest。");
        if (normalizedFiles.Any(file => file.Length < 0 || file.Sha256.Length != 64 ||
                                        file.Sha256.Any(character => !Uri.IsHexDigit(character))))
            throw new InvalidDataException("安装包文件元数据无效。");
        return manifest with { Files = normalizedFiles };
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("安装包包含非法路径。");
        var segments = path.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("安装包包含非法路径。");
        return string.Join(Path.DirectorySeparatorChar, segments);
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PackageManifest))]
internal sealed partial class PackageJsonContext : JsonSerializerContext;
