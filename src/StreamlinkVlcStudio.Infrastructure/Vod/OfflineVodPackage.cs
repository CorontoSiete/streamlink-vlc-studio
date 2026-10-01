using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal sealed record OfflineVodFile(string Name, long Length);

internal sealed record OfflineVodPackage(int Version, string ManifestHash, TimeSpan Duration,
    int SegmentCount, long BytesDownloaded, IReadOnlyList<OfflineVodFile> Files)
{
    internal const string ManifestName = "index.m3u8";
    internal const string PackageName = "package.json";

    internal static async Task<OfflineVodPackage> CreateAsync(string directory, OfflineHlsPlaylist playlist,
        CancellationToken cancellationToken)
    {
        var manifest = Encoding.UTF8.GetBytes(playlist.Content);
        await AtomicFile.WriteAsync(Path.Combine(directory, ManifestName),
            (stream, token) => stream.WriteAsync(manifest, token).AsTask(), cancellationToken, flushToDisk: true).ConfigureAwait(false);
        var files = playlist.Assets.Select(asset => new OfflineVodFile(asset.FileName,
            new FileInfo(Path.Combine(directory, asset.FileName)).Length)).ToArray();
        var package = new OfflineVodPackage(1, Convert.ToHexString(SHA256.HashData(manifest)), playlist.Duration,
            playlist.SegmentCount, files.Sum(file => file.Length), files);
        await AtomicFile.WriteAsync(Path.Combine(directory, PackageName),
            (stream, token) => JsonSerializer.SerializeAsync(stream, package, cancellationToken: token),
            cancellationToken, flushToDisk: true).ConfigureAwait(false);
        return package;
    }

    internal static async Task<OfflineVodPackage> ValidateAsync(string directory, CancellationToken cancellationToken)
    {
        RequireRegularPath(directory);
        RequireRegularPath(Path.Combine(directory, PackageName));
        RequireRegularPath(Path.Combine(directory, ManifestName));
        var bytes = await BoundedByteReader.ReadFileAsync(Path.Combine(directory, PackageName),
            32 * 1024 * 1024, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("The offline VOD package information is missing or too large.");
        var package = JsonSerializer.Deserialize<OfflineVodPackage>(bytes) ??
            throw new InvalidDataException("The offline VOD package information is invalid.");
        if (package.Version != 1 || package.Files is null || package.Files.Count is <= 0 or > OfflineHlsPlaylist.MaximumAssets)
            throw new InvalidDataException("The offline VOD package format is invalid.");
        var manifest = await BoundedByteReader.ReadFileAsync(Path.Combine(directory, ManifestName),
            16 * 1024 * 1024, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("The offline VOD playlist is missing or too large.");
        if (!Convert.ToHexString(SHA256.HashData(manifest)).Equals(package.ManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("The offline VOD playlist has changed or is damaged.");
        var localBase = new Uri("https://offline.invalid/index.m3u8");
        var playlist = OfflineHlsPlaylist.Parse(new UTF8Encoding(false, true).GetString(manifest), localBase);
        if (playlist.Duration != package.Duration || playlist.SegmentCount != package.SegmentCount ||
            playlist.Assets.Count != package.Files.Count)
            throw new InvalidDataException("The offline VOD package does not match its playlist.");
        var inventory = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var file in package.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null || !OfflineHlsAssetName.IsValid(file.Name) || file.Length is <= 0 or > OfflineHlsPlaylist.MaximumAssetBytes ||
                !inventory.TryAdd(file.Name, file.Length)) throw new InvalidDataException("The offline VOD file inventory is invalid.");
            var path = Path.Combine(directory, file.Name);
            RequireRegularPath(path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
                throw new InvalidDataException("The downloaded VOD has missing or truncated media files. Download it again.");
            totalBytes = checked(totalBytes + file.Length);
        }
        if (totalBytes != package.BytesDownloaded) throw new InvalidDataException("The offline VOD size is invalid.");
        foreach (var asset in playlist.Assets)
        {
            if (asset.Uri.Host != localBase.Host || !string.IsNullOrEmpty(asset.Uri.Query) ||
                asset.Uri.AbsolutePath != "/" + asset.FileName || !inventory.TryGetValue(asset.FileName, out var length) ||
                asset.Offset is not null || asset.Length is not null || (asset.IsKey && length != 16))
                throw new InvalidDataException("The offline VOD still references external or invalid media resources.");
        }
        return package;
    }

    internal static void RequireRegularPath(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("VOD downloads cannot use symbolic links or directory junctions inside the download library.");
    }

}
