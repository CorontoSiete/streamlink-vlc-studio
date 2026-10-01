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

    internal static OfflineHlsPlaylist Parse(string content, Uri playlistUri)
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
        var finished = false;
        var encrypted = false;
        var hasEncryptionIv = false;

        string AddAsset(string value, long? offset = null, long? length = null, bool segment = false, bool key = false)
        {
            if (assets.Count >= MaximumAssets) throw new InvalidDataException("The VOD contains too many media resources.");
            var address = ResolveUri(playlistUri, value);
            var name = OfflineHlsAssetName.Create(address, assets.Count, key);
            assets.Add(new OfflineHlsAsset(address, name, offset, length, segment, key, encrypted && !key));
            return name;
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.Any(character => char.IsControl(character) && character != '\t'))
                throw new InvalidDataException("The VOD playlist contains invalid control characters.");
            if (!line.StartsWith('#'))
            {
                if (finished || pendingDuration is null) throw new InvalidDataException("A VOD segment has no valid duration.");
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
                if (seconds > (decimal)TimeSpan.FromDays(14).TotalSeconds)
                    throw new InvalidDataException("The VOD duration exceeds the supported safety limit.");
                pendingDuration = null;
                pendingRange = null;
                segments++;
                continue;
            }

            var separator = line.IndexOf(':');
            var tag = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            if (finished && tag.StartsWith("#EXT", StringComparison.Ordinal))
                throw new InvalidDataException("The VOD contains media tags after its end marker.");
            switch (tag)
            {
                case "#EXTINF":
                    var comma = value.IndexOf(',');
                    var duration = comma < 0 ? value : value[..comma];
                    if (pendingDuration is not null || !decimal.TryParse(duration, NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var parsedDuration) || parsedDuration <= 0)
                        throw new InvalidDataException("The VOD contains an invalid segment duration.");
                    pendingDuration = parsedDuration;
                    output.AppendLine($"#EXTINF:{duration},");
                    break;
                case "#EXT-X-BYTERANGE":
                    if (pendingDuration is null || pendingRange is not null)
                        throw new InvalidDataException("The VOD contains an invalid segment byte range.");
                    pendingRange = value;
                    break;
                case "#EXT-X-KEY":
                    var key = ParseAttributes(value);
                    if (!key.TryGetValue("METHOD", out var method)) throw new InvalidDataException("The VOD encryption method is missing.");
                    encrypted = method != "NONE";
                    hasEncryptionIv = key.ContainsKey("IV");
                    if (!encrypted)
                    {
                        if (key.Count != 1) throw new InvalidDataException("Invalid unencrypted VOD key declaration.");
                        output.AppendLine("#EXT-X-KEY:METHOD=NONE");
                        break;
                    }
                    if (method != "AES-128" || (key.TryGetValue("KEYFORMAT", out var format) && format != "identity") ||
                        (key.TryGetValue("KEYFORMATVERSIONS", out var versions) && versions != "1"))
                        throw new NotSupportedException("This VOD uses DRM or unsupported encryption. It cannot be downloaded for offline playback.");
                    if (!key.TryGetValue("URI", out var keyUri)) throw new InvalidDataException("The VOD encryption key URL is missing.");
                    var keyLine = $"#EXT-X-KEY:METHOD=AES-128,URI=\"{AddAsset(keyUri, key: true)}\"";
                    if (key.TryGetValue("IV", out var iv))
                    {
                        if (!iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || iv.Length is < 3 or > 34 ||
                            !iv[2..].All(char.IsAsciiHexDigit)) throw new InvalidDataException("The VOD encryption IV is invalid.");
                        keyLine += $",IV={iv}";
                    }
                    output.AppendLine(keyLine);
                    break;
                case "#EXT-X-MAP":
                    var map = ParseAttributes(value);
                    if (!map.TryGetValue("URI", out var mapUri)) throw new InvalidDataException("The VOD initialization section URL is missing.");
                    if (encrypted && !hasEncryptionIv) throw new InvalidDataException("An encrypted initialization section requires an explicit IV.");
                    long? mapOffset = null;
                    long? mapLength = null;
                    if (map.TryGetValue("BYTERANGE", out var mapRange)) (mapOffset, mapLength) = ParseRange(mapRange, null);
                    output.AppendLine($"#EXT-X-MAP:URI=\"{AddAsset(mapUri, mapOffset, mapLength)}\"");
                    break;
                case "#EXT-X-TARGETDURATION":
                    if (hasTargetDuration || segments != 0 || ReadNumber(value) <= 0)
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
                    if (pendingDuration is not null || pendingRange is not null) throw new InvalidDataException("The VOD ends with an incomplete segment.");
                    finished = true;
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

        if (!finished) throw new InvalidDataException("This VOD is still being recorded. Wait for the broadcast to finish before downloading it.");
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

    private static Dictionary<string, string> ParseAttributes(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        while (position < value.Length)
        {
            var equals = value.IndexOf('=', position);
            if (equals < 0) throw new InvalidDataException("The VOD contains a malformed attribute list.");
            var name = value[position..equals].Trim();
            if (name.Length == 0 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                throw new InvalidDataException("The VOD contains an invalid media attribute.");
            position = equals + 1;
            string attribute;
            if (position < value.Length && value[position] == '"')
            {
                var end = value.IndexOf('"', position + 1);
                if (end < 0) throw new InvalidDataException("The VOD contains an unterminated media attribute.");
                attribute = value[(position + 1)..end];
                position = end + 1;
            }
            else
            {
                var end = value.IndexOf(',', position);
                if (end < 0) end = value.Length;
                attribute = value[position..end].Trim();
                position = end;
            }
            if (!result.TryAdd(name, attribute) || result.Count > 32)
                throw new InvalidDataException("The VOD contains duplicate or excessive media attributes.");
            if (position == value.Length) break;
            if (value[position++] != ',' || position == value.Length)
                throw new InvalidDataException("The VOD contains a malformed attribute separator.");
        }
        return result;
    }
}
