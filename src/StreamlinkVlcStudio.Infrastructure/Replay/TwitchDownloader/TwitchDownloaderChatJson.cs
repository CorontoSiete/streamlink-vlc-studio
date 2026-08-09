// Adapted from TwitchDownloaderCore/TwitchObjects/ChatRoot.cs and Chat/ChatJson.cs.
// Original project: https://github.com/lay295/TwitchDownloader
//
// The MIT License
//
// Copyright (c) lay295
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamlinkVlcStudio.Infrastructure.Replay.TwitchDownloader;

public static class TwitchDownloaderChatJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<TwitchDownloaderComment> ReadComments(JsonElement root)
    {
        var comments = ResolveCommentsArray(root);
        if (comments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return comments.Deserialize<List<TwitchDownloaderComment>>(JsonOptions) ?? [];
    }

    private static JsonElement ResolveCommentsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (root.TryGetProperty("comments", out var comments))
        {
            return comments;
        }

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("comments", out comments))
        {
            return comments;
        }

        return default;
    }
}

public sealed class TwitchDownloaderCommenter
{
    public string? display_name { get; set; }

    public string? _id { get; set; }

    public string? name { get; set; }
}

public sealed class TwitchDownloaderFragment
{
    public string? text { get; set; }
}

public sealed class TwitchDownloaderUserBadge
{
    public string? _id { get; set; }

    [JsonPropertyName("setID")]
    public string? setID { get; set; }

    public string? setId { get; set; }

    public string? set_id { get; set; }

    [JsonPropertyName("badgeSetID")]
    public string? badgeSetID { get; set; }

    public string? badge_set_id { get; set; }

    public string? name { get; set; }

    public string? type { get; set; }

    public string? version { get; set; }

    public string? title { get; set; }

    public string? image_url { get; set; }

    public string? image_url_1x { get; set; }

    public string? image_url_2x { get; set; }

    public string? image_url_4x { get; set; }

    [JsonPropertyName("imageURL")]
    public string? imageURL { get; set; }
}

public sealed class TwitchDownloaderEmoticon
{
    public string? _id { get; set; }

    public int begin { get; set; }

    public int end { get; set; }
}

public sealed class TwitchDownloaderMessage
{
    public string? body { get; set; }

    public List<TwitchDownloaderFragment>? fragments { get; set; }

    public List<TwitchDownloaderUserBadge>? user_badges { get; set; }

    [JsonPropertyName("userBadges")]
    public List<TwitchDownloaderUserBadge>? userBadges { get; set; }

    public string? user_color { get; set; }

    public List<TwitchDownloaderEmoticon>? emoticons { get; set; }
}

public sealed class TwitchDownloaderComment
{
    public string? _id { get; set; }

    public DateTime created_at { get; set; }

    public string? channel_id { get; set; }

    public string? content_type { get; set; }

    public string? content_id { get; set; }

    public double content_offset_seconds { get; set; }

    public TwitchDownloaderCommenter? commenter { get; set; }

    public TwitchDownloaderMessage? message { get; set; }
}
