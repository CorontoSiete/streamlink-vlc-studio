using System.Globalization;
using System.Text;
using StreamlinkVlcStudio.Infrastructure.Hls;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal sealed record OfflineHlsAsset(Uri Uri, string FileName, long? Offset, long? Length,
    bool IsSegment = false, bool IsKey = false, bool IsEncrypted = false);

internal sealed record OfflineHlsPlaylist(string Content, IReadOnlyList<OfflineHlsAsset> Assets,
    int SegmentCount, TimeSpan Duration)
{
    internal const int MaximumAssets = 200000;
    internal const long MaximumAssetBytes = 512L * 1024 * 1024;
    private const decimal MaximumDurationSeconds = 14 * 24 * 60 * 60;

    internal static OfflineHlsPlaylist Parse(string content, Uri playlistUri, bool requireLocalAssets = false)
    {
        var lines = HlsPlaylistPolicy.SplitLines(content);
        if (lines.Length == 0 || lines[0] != "#EXTM3U")
            throw new InvalidDataException("The VOD source did not return an HLS playlist.");
        if (HlsPlaylistPolicy.HasSkippedSegments(lines))
            throw new InvalidDataException("An offline VOD requires a complete HLS playlist.");

        var output = new StringBuilder("#EXTM3U\n");
        var assets = new List<OfflineHlsAsset>();
        var segments = 0;
        decimal seconds = 0;
        decimal? pendingDuration = null;
        string? pendingRange = null;
        Uri? previousUri = null;
        long? previousRangeEnd = null;
        var hasTargetDuration = false;
        var hasMediaSequence = false;
        var hasEndList = false;
        var encrypted = false;
        var hasEncryptionIv = false;

        string AddAsset(string value, long? offset = null, long? length = null, bool segment = false, bool key = false)
        {
            if (assets.Count >= MaximumAssets) throw new InvalidDataException("The VOD contains too many media resources.");
            var address = ResolveUri(playlistUri, value);
            var name = OfflineHlsAssetName.Create(address, assets.Count, key);
            if (requireLocalAssets && !string.Equals(value, name, StringComparison.Ordinal))
                throw new InvalidDataException("The offline VOD must reference its local media file names directly.");
            assets.Add(new OfflineHlsAsset(address, name, offset, length, segment, key, encrypted && !key));
            return name;
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.Any(character => char.IsControl(character) && character != '\t'))
                throw new InvalidDataException("The VOD playlist contains invalid control characters.");
            if (!line.StartsWith('#'))
            {
                if (pendingDuration is null) throw new InvalidDataException("A VOD segment has no valid duration.");
                var address = ResolveUri(playlistUri, line);
                long? offset = null;
                long? length = null;
                if (pendingRange is not null)
                {
                    (offset, length) = ParseRange(pendingRange, address == previousUri ? previousRangeEnd : null);
                    previousRangeEnd = checked(offset.Value + length.Value);
                }
                else previousRangeEnd = null;
                output.AppendLine(AddAsset(line, offset, length, segment: true));
                previousUri = address;
                seconds += pendingDuration.Value;
                if (seconds > MaximumDurationSeconds)
                    throw new InvalidDataException("The VOD duration exceeds the supported safety limit.");
                pendingDuration = null;
                pendingRange = null;
                segments++;
                continue;
            }

            var separator = line.IndexOf(':');
            var tag = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            switch (tag)
            {
                case "#EXTINF":
                    if (pendingDuration is not null || !HlsPlaylistPolicy.TryReadSegmentDuration(
                        value, MaximumDurationSeconds, out var parsedDuration))
                        throw new InvalidDataException("The VOD contains an invalid segment duration.");
                    pendingDuration = parsedDuration;
                    output.AppendLine($"#EXTINF:{parsedDuration.ToString(CultureInfo.InvariantCulture)},");
                    break;
                case "#EXT-X-BYTERANGE":
                    if (pendingRange is not null)
                        throw new InvalidDataException("The VOD contains an invalid segment byte range.");
                    pendingRange = value;
                    break;
                case "#EXT-X-KEY":
                    if (!HlsAttributeList.TryParse(value, out var key)) throw new InvalidDataException("The VOD contains invalid encryption key attributes.");
                    if (!key.TryGetValue("METHOD", out var method) || method.IsQuoted) throw new InvalidDataException("The VOD encryption method is missing or invalid.");
                    encrypted = method.Value != "NONE";
                    hasEncryptionIv = key.ContainsKey("IV");
                    if (!encrypted)
                    {
                        if (key.Count != 1) throw new InvalidDataException("Invalid unencrypted VOD key declaration.");
                        output.AppendLine("#EXT-X-KEY:METHOD=NONE");
                        break;
                    }
                    if (method.Value != "AES-128" || (key.TryGetValue("KEYFORMAT", out var format) && (!format.IsQuoted || format.Value != "identity")) ||
                        (key.TryGetValue("KEYFORMATVERSIONS", out var versions) && (!versions.IsQuoted || versions.Value != "1")))
                        throw new NotSupportedException("This VOD uses DRM or unsupported encryption. It cannot be downloaded for offline playback.");
                    if (!key.TryGetValue("URI", out var keyUri) || !keyUri.IsQuoted) throw new InvalidDataException("The VOD encryption key URL is missing or invalid.");
                    var keyLine = $"#EXT-X-KEY:METHOD=AES-128,URI=\"{AddAsset(keyUri.Value, key: true)}\"";
                    if (key.TryGetValue("IV", out var iv))
                    {
                        if (iv.IsQuoted || !iv.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || iv.Value.Length is < 3 or > 34 ||
                            !iv.Value[2..].All(char.IsAsciiHexDigit)) throw new InvalidDataException("The VOD encryption IV is invalid.");
                        keyLine += $",IV={iv.Value}";
                    }
                    output.AppendLine(keyLine);
                    break;
                case "#EXT-X-MAP":
                    if (!HlsAttributeList.TryParse(value, out var map)) throw new InvalidDataException("The VOD contains invalid initialization section attributes.");
                    if (!map.TryGetValue("URI", out var mapUri) || !mapUri.IsQuoted) throw new InvalidDataException("The VOD initialization section URL is missing or invalid.");
                    if (encrypted && !hasEncryptionIv) throw new InvalidDataException("An encrypted initialization section requires an explicit IV.");
                    long? mapOffset = null;
                    long? mapLength = null;
                    if (map.TryGetValue("BYTERANGE", out var mapRange))
                    {
                        if (!mapRange.IsQuoted) throw new InvalidDataException("The VOD initialization byte range is invalid.");
                        (mapOffset, mapLength) = ParseRange(mapRange.Value, null);
                    }
                    output.AppendLine($"#EXT-X-MAP:URI=\"{AddAsset(mapUri.Value, mapOffset, mapLength)}\"");
                    break;
                case "#EXT-X-TARGETDURATION":
                    if (hasTargetDuration || ReadNumber(value) <= 0)
                        throw new InvalidDataException("The VOD target duration is invalid.");
                    hasTargetDuration = true;
                    output.AppendLine(line);
                    break;
                case "#EXT-X-MEDIA-SEQUENCE":
                    if (hasMediaSequence || segments != 0) throw new InvalidDataException("The VOD media sequence is invalid.");
                    ReadNumber(value);
                    hasMediaSequence = true;
                    output.AppendLine(line);
                    break;
                case "#EXT-X-VERSION":
                case "#EXT-X-DISCONTINUITY-SEQUENCE":
                    ReadNumber(value);
                    output.AppendLine(line);
                    break;
                case "#EXT-X-PLAYLIST-TYPE":
                    if (value is not ("VOD" or "EVENT")) throw new InvalidDataException("The VOD playlist type is invalid.");
                    break;
                case "#EXT-X-DISCONTINUITY":
                case "#EXT-X-INDEPENDENT-SEGMENTS":
                    output.AppendLine(line);
                    break;
                case "#EXT-X-PROGRAM-DATE-TIME":
                    if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        throw new InvalidDataException("The VOD program date is invalid.");
                    output.AppendLine(line);
                    break;
                case "#EXT-X-ENDLIST":
                    if (hasEndList || line != "#EXT-X-ENDLIST") throw new InvalidDataException("The VOD end marker is invalid.");
                    hasEndList = true;
                    break;
                case "#EXT-X-GAP":
                    throw new InvalidDataException("The VOD declares missing media segments; a complete offline copy is unavailable.");
                case "#EXT-X-STREAM-INF":
                case "#EXT-X-MEDIA":
                case "#EXT-X-I-FRAME-STREAM-INF":
                case "#EXT-X-I-FRAMES-ONLY":
                case "#EXT-X-SESSION-KEY":
                case "#EXT-X-DEFINE":
                    throw new NotSupportedException("The resolved VOD must be a complete media rendition, not a master, partial, or I-frame playlist.");
                default:
                    if (value.Contains("URI=", StringComparison.OrdinalIgnoreCase) && tag != "#EXT-X-DATERANGE")
                        throw new NotSupportedException($"The VOD contains an unsupported external resource tag: {tag}.");
                    break;
            }
        }

        if (!hasEndList) throw new InvalidDataException("This VOD is still being recorded. Wait for the broadcast to finish before downloading it.");
        if (pendingDuration is not null || pendingRange is not null) throw new InvalidDataException("The VOD ends with an incomplete segment.");
        if (!hasTargetDuration || segments == 0) throw new InvalidDataException("The VOD playlist contains no complete playable media.");
        output.AppendLine("#EXT-X-ENDLIST");
        return new OfflineHlsPlaylist(output.ToString(), assets, segments,
            TimeSpan.FromTicks(decimal.ToInt64(seconds * TimeSpan.TicksPerSecond)));
    }

    private static Uri ResolveUri(Uri playlistUri, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            !Uri.TryCreate(playlistUri, value, out var address) || address.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Fragment))
            throw new InvalidDataException("The VOD contains an invalid media resource URL.");
        return address;
    }

    private static long ReadNumber(string value) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var number) && number >= 0
        ? number : throw new InvalidDataException("The VOD contains an invalid numeric media tag.");

    private static (long Offset, long Length) ParseRange(string value, long? previousEnd)
    {
        var parts = value.Split('@');
        if (parts.Length is < 1 or > 2) throw new InvalidDataException("The VOD byte range is invalid.");
        var length = ReadNumber(parts[0]);
        var offset = parts.Length == 2 ? ReadNumber(parts[1]) : previousEnd ??
            throw new InvalidDataException("The VOD byte range has no unambiguous starting offset.");
        if (length is <= 0 or > MaximumAssetBytes || offset > long.MaxValue - length)
            throw new InvalidDataException("The VOD byte range exceeds the supported safety limit.");
        return (offset, length);
    }
}
