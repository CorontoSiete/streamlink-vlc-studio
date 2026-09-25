using System.Security.Cryptography;
using System.Text.Json;

namespace StreamlinkVlcStudio.Maintenance;

public sealed record ManagedInstallFile(string RelativePath, long Length, string Sha256);

public sealed class InstallOwnership
{
    public const string OwnerFileName = ".stream-studio-owner.json";
    public const string ManifestFileName = ".stream-studio-files.json";
    private const string ProductId = "stream-studio";
    private const int MaximumStateBytes = 16 * 1024 * 1024;
    private const int MaximumManagedFiles = 100_000;

    private InstallOwnership(
        string root,
        string ownerPath,
        string manifestPath,
        IReadOnlyList<ManagedInstallFile> files)
    {
        Root = root;
        OwnerPath = ownerPath;
        ManifestPath = manifestPath;
        Files = files;
    }

    public string Root { get; }

    public string OwnerPath { get; }

    public string ManifestPath { get; }

    public IReadOnlyList<ManagedInstallFile> Files { get; }

    public string GetManagedPath(ManagedInstallFile file)
    {
        var target = Path.GetFullPath(Path.Combine(Root, file.RelativePath));
        if (!PathSafety.IsSameOrUnder(target, Root) || PathSafety.PathsEqual(target, Root))
        {
            throw new InvalidDataException($"Managed path escapes the installation: {file.RelativePath}");
        }

        return target;
    }

    public static InstallOwnership Load(string directory)
    {
        var root = PathSafety.Normalize(directory);
        if (!PathSafety.IsSafeInstallRoot(root))
        {
            throw new InvalidDataException($"Refusing to uninstall an unsafe installation directory: {root}");
        }

        var ownerPath = Path.Combine(root, OwnerFileName);
        var manifestPath = Path.Combine(root, ManifestFileName);
        AssertPlainStateFile(ownerPath);
        AssertPlainStateFile(manifestPath);

        var ownerBytes = ReadBounded(ownerPath);
        var manifestBytes = ReadBounded(manifestPath);
        using var ownerDocument = JsonDocument.Parse(ownerBytes);
        using var manifestDocument = JsonDocument.Parse(manifestBytes);
        var owner = ownerDocument.RootElement;
        var manifest = manifestDocument.RootElement;

        AssertHeader(owner, "owner");
        AssertHeader(manifest, "manifest");
        var ownerInstallId = RequiredString(owner, "installId");
        var manifestInstallId = RequiredString(manifest, "installId");
        if (!string.Equals(ownerInstallId, manifestInstallId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Installation owner and managed-file manifest identifiers do not match.");
        }

        if (!string.Equals(RequiredString(owner, "manifest"), ManifestFileName, StringComparison.Ordinal) ||
            !IsSha256(RequiredString(owner, "manifestSha256")))
        {
            throw new InvalidDataException("The installation owner marker names an invalid manifest.");
        }

        var expectedManifestHash = RequiredString(owner, "manifestSha256");
        var actualManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        if (!string.Equals(expectedManifestHash, actualManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The managed-file manifest hash does not match the owner marker.");
        }

        if (!manifest.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array ||
            filesElement.GetArrayLength() > MaximumManagedFiles)
        {
            throw new InvalidDataException("The installation managed-file list is invalid or too large.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ManagedInstallFile>(filesElement.GetArrayLength());
        foreach (var element in filesElement.EnumerateArray())
        {
            var relativePath = RequiredString(element, "path").Replace('\\', '/');
            if (!PathSafety.IsSafeManifestRelativePath(relativePath) || !seen.Add(relativePath))
            {
                throw new InvalidDataException($"Unsafe or duplicate managed path: {relativePath}");
            }

            if (!element.TryGetProperty("length", out var lengthElement) ||
                !lengthElement.TryGetInt64(out var length) ||
                length < 0)
            {
                throw new InvalidDataException($"Managed path has an invalid length: {relativePath}");
            }

            var sha256 = RequiredString(element, "sha256");
            if (!IsSha256(sha256))
            {
                throw new InvalidDataException($"Managed path has an invalid SHA-256 value: {relativePath}");
            }

            var managedFile = new ManagedInstallFile(relativePath, length, sha256);
            _ = new InstallOwnership(root, ownerPath, manifestPath, Array.Empty<ManagedInstallFile>())
                .GetManagedPath(managedFile);
            files.Add(managedFile);
        }

        return new InstallOwnership(root, ownerPath, manifestPath, files);
    }

    private static void AssertHeader(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("schemaVersion", out var schema) ||
            !schema.TryGetInt32(out var schemaVersion) ||
            schemaVersion != 1 ||
            !string.Equals(RequiredString(element, "product"), ProductId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The installation {description} uses an unsupported schema or product.");
        }
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Installation state is missing '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static byte[] ReadBounded(string path)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumStateBytes)
        {
            throw new InvalidDataException($"Installation state file has an invalid size: {path}");
        }

        return File.ReadAllBytes(path);
    }

    private static void AssertPlainStateFile(string path)
    {
        if (!PathSafety.TryGetAttributes(path, out var attributes) ||
            !PathSafety.IsPlainFile(attributes))
        {
            throw new InvalidDataException($"Installation state is missing or is a reparse point: {path}");
        }
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }
}
