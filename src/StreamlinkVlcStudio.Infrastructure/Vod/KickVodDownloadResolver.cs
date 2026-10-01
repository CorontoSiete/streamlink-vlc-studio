using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal sealed class KickVodDownloadResolver
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(TimeSpan.FromSeconds(20), allowAutoRedirect: false);
    private static readonly Regex ScriptPattern = new(@"<script\b[^>]*>(.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private const string PushMarker = "self.__next_f.push(";
    private readonly KickWebsiteJsonReader reader;

    internal KickVodDownloadResolver(IAppLogger logger, HttpClient? client = null,
        Func<string, string, CancellationToken, Task<string?>>? curlOverride = null)
    {
        reader = new KickWebsiteJsonReader(client ?? SharedClient, logger, "VOD downloads", TimeSpan.FromSeconds(15), curlOverride);
    }

    internal async Task<StreamTarget> ResolveAsync(StreamTarget target, CancellationToken cancellationToken)
    {
        var page = VodDownloadUrlParser.Parse(target.Url);
        if (!target.IsExplicitKickVod || page.Platform != PlatformKind.Kick || page.MediaId != target.MediaId)
            throw new ArgumentException("A matching Kick VOD page is required.", nameof(target));
        var direct = await reader.ReadDirectAsync(page.Url, page.Url, cancellationToken, KickWebsitePayloadKind.Html).ConfigureAwait(false);
        long channelId;
        try { channelId = ReadChannelId(direct.Body ?? ""); }
        catch (Exception exception) when (exception is InvalidDataException or RegexMatchTimeoutException)
        {
            var html = await reader.ReadFallbackAsync(page.Url, page.Url, cancellationToken, KickWebsitePayloadKind.Html).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Kick's VOD page could not be loaded. Try again when Kick is available.", exception);
            channelId = ReadChannelId(html);
        }
        var api = $"https://web.kick.com/api/v1/channels/{channelId.ToString(CultureInfo.InvariantCulture)}/videos/{Uri.EscapeDataString(page.MediaId)}";
        var json = await reader.ReadAsync(api, page.Url, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Kick's VOD metadata could not be loaded. The VOD may no longer be available.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            ReadString(data, "id") != page.MediaId || !data.TryGetProperty("channel", out var channel) ||
            channel.ValueKind != JsonValueKind.Object || !channel.TryGetProperty("id", out var returnedId) ||
            returnedId.ValueKind != JsonValueKind.Number || !returnedId.TryGetInt64(out var actualChannelId) || actualChannelId != channelId ||
            !ReadString(channel, "slug").Equals(page.Channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kick returned metadata for a different VOD or channel.");
        if (!Uri.TryCreate(ReadString(data, "recording_url"), UriKind.Absolute, out var source) ||
            !ProviderUriPolicy.IsApprovedReplayUri(source, PlatformKind.Kick) ||
            !source.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kick did not return an approved VOD playlist URL.");
        var title = ReadString(data, "title");
        var category = data.TryGetProperty("category", out var value) && value.ValueKind == JsonValueKind.Object
            ? ReadString(value, "name") : "";
        return target with
        {
            Url = source.AbsoluteUri,
            BroadcasterId = channelId.ToString(CultureInfo.InvariantCulture),
            DisplayTitle = string.IsNullOrWhiteSpace(title) ? target.DisplayTitle : title,
            CategoryName = category
        };
    }

    internal static long ReadChannelId(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > PayloadLimits.ProcessOutputBytes)
            throw new InvalidDataException("Kick's VOD page exceeds the supported size.");
        using var payload = new MemoryStream();
        foreach (Match match in ScriptPattern.Matches(html))
        {
            var script = match.Groups[1].Value;
            var position = 0;
            while ((position = script.IndexOf(PushMarker, position, StringComparison.Ordinal)) >= 0)
            {
                position += PushMarker.Length;
                var bytes = Encoding.UTF8.GetBytes(script.AsSpan(position).ToString());
                var jsonReader = new Utf8JsonReader(bytes);
                try
                {
                    using var document = JsonDocument.ParseValue(ref jsonReader);
                    position += Encoding.UTF8.GetCharCount(bytes.AsSpan(0, checked((int)jsonReader.BytesConsumed)));
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2 &&
                        root[0].ValueKind == JsonValueKind.Number && root[0].TryGetInt32(out var kind) && kind is 1 or 3 &&
                        root[1].ValueKind == JsonValueKind.String)
                    {
                        var part = kind == 1 ? Encoding.UTF8.GetBytes(root[1].GetString()!) : Convert.FromBase64String(root[1].GetString()!);
                        if (payload.Length + part.Length > PayloadLimits.ProcessOutputBytes)
                            throw new InvalidDataException("Kick's VOD page data exceeds the supported size.");
                        payload.Write(part);
                    }
                }
                catch (JsonException) { break; }
                catch (FormatException exception) { throw new InvalidDataException("Kick's VOD page data could not be decoded.", exception); }
            }
        }
        var data = payload.ToArray();
        var offset = 0;
        var ids = new HashSet<long>();
        while (offset < data.Length)
        {
            ReadHexNumber(data, ref offset, (byte)':', allowEmpty: true);
            if (offset >= data.Length) throw new InvalidDataException("Kick's VOD page data is truncated.");
            var tag = (char)data[offset];
            if ("TAOobUSsLlGgMmV".Contains(tag))
            {
                offset++;
                var length = ReadHexNumber(data, ref offset, (byte)',');
                if (length > data.Length - offset) throw new InvalidDataException("Kick's VOD page text data is truncated.");
                offset += length;
                continue;
            }
            var end = Array.IndexOf(data, (byte)'\n', offset);
            if (end < 0) throw new InvalidDataException("Kick's VOD page data is truncated.");
            var content = data.AsMemory(offset, end - offset);
            offset = end + 1;
            if (content.Length == 0 || content.Span[0] is not ((byte)'{' or (byte)'[')) continue;
            try
            {
                using var document = JsonDocument.Parse(content);
                CollectChannelIds(document.RootElement, ids);
                if (ids.Count > 1) throw new InvalidDataException("Kick's VOD page contains ambiguous channel metadata.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Kick's VOD channel metadata could not be parsed.", exception);
            }
        }
        if (ids.Count == 1) return ids.Single();
        throw new InvalidDataException("Kick's VOD page did not contain a channel ID. Use a current Streamlink version if Kick changed its page format.");
    }

    private static int ReadHexNumber(byte[] data, ref int offset, byte delimiter, bool allowEmpty = false)
    {
        var start = offset;
        var value = 0;
        while (offset < data.Length && data[offset] != delimiter)
        {
            var character = (char)data[offset++];
            var digit = character is >= '0' and <= '9' ? character - '0' :
                character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
            if (digit < 0 || value > (int.MaxValue - digit) / 16)
                throw new InvalidDataException("Kick's VOD page has invalid data framing.");
            value = value * 16 + digit;
        }
        if ((!allowEmpty && offset == start) || offset >= data.Length) throw new InvalidDataException("Kick's VOD page data is truncated.");
        offset++;
        return value;
    }

    private static void CollectChannelIds(JsonElement element, HashSet<long> ids)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("channel_id") && property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt64(out var id) && id > 0) ids.Add(id);
                CollectChannelIds(property.Value, ids);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CollectChannelIds(child, ids);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
