using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

/// <summary>Loads the provider's small storyboard assets without opening a second video stream.</summary>
internal sealed class TwitchSeekPreviewClient
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(8), allowAutoRedirect: false);
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator validator;

    internal TwitchSeekPreviewClient() : this(SharedClient, ReplayUrlSecurityValidator.Shared) { }

    internal TwitchSeekPreviewClient(HttpClient httpClient, ReplayUrlSecurityValidator validator)
    {
        this.httpClient = httpClient;
        this.validator = validator;
    }

    internal async Task<TwitchSeekStoryboard?> GetStoryboardAsync(string videoId, CancellationToken cancellationToken)
    {
        if (videoId.Length is 0 or > 32 || !videoId.All(char.IsAsciiDigit)) return null;
        var payload = JsonSerializer.Serialize(new
        {
            query = "query SeekPreview($id: ID!) { video(id: $id) { lengthSeconds seekPreviewsURL } }",
            variables = new { id = videoId }
        });
        using var metadata = await new TwitchGraphQlTransport(httpClient).SendAsync(
            payload, TwitchGraphQlTransport.PublicClientId, TwitchGraphQlTransport.CreateDeviceId(),
            cancellationToken).ConfigureAwait(false);
        if (metadata.RootElement.ValueKind != JsonValueKind.Object ||
            !metadata.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object ||
            !video.TryGetProperty("lengthSeconds", out var length) || length.ValueKind != JsonValueKind.Number ||
            !length.TryGetDouble(out var duration) || !double.IsFinite(duration) || duration <= 0 ||
            !video.TryGetProperty("seekPreviewsURL", out var url) || url.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)) return null;

        using var response = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await BoundedHttpContentReader.ReadStringAsync(response.Content, 512 * 1024, cancellationToken)
            .ConfigureAwait(false);
        // Relative sprite URLs are relative to the final manifest location after redirects.
        return TwitchSeekStoryboard.Parse(json, response.RequestMessage?.RequestUri ?? uri, duration);
    }

    internal async Task<byte[]> GetImageAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await BoundedByteReader.ReadOrThrowAsync(response.Content, 8 * 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken) =>
        ValidatedReplayHttpClient.SendGetAsync(httpClient, validator, uri, PlatformKind.Twitch,
            static address => new HttpRequestMessage(HttpMethod.Get, address), cancellationToken);
}

internal sealed record TwitchSeekStoryboard(
    int Width, int Height, int Columns, int Rows, int Count, double Duration, double Interval, IReadOnlyList<Uri> Images)
{
    internal static TwitchSeekStoryboard? Parse(string json, Uri manifestUri, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0 ||
            !ProviderUriPolicy.IsApprovedReplayUri(manifestUri, PlatformKind.Twitch)) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        TwitchSeekStoryboard? selected = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var width = ReadInt(item, "width");
            var height = ReadInt(item, "height");
            var columns = ReadInt(item, "cols");
            var rows = ReadInt(item, "rows");
            var count = ReadInt(item, "count");
            if (width is < 1 or > 1024 || height is < 1 or > 1024 || columns is < 1 or > 100 ||
                rows is < 1 or > 100 || count is < 1 or > 200_000 ||
                (long)width * columns > 8192 || (long)height * rows > 8192 ||
                (long)width * height * columns * rows > 16_000_000 ||
                !item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array ||
                images.GetArrayLength() != (count + columns * rows - 1) / (columns * rows)) continue;
            // Twitch rounds its sampling interval up; duration/count drifts into later cells.
            var interval = duration / count;
            if (item.TryGetProperty("interval", out var spacing) &&
                (spacing.ValueKind != JsonValueKind.Number || !spacing.TryGetDouble(out interval))) continue;
            if (!double.IsFinite(interval) || interval <= 0) continue;
            var urls = new List<Uri>();
            foreach (var image in images.EnumerateArray())
            {
                if (image.ValueKind != JsonValueKind.String ||
                    !ProviderUriPolicy.TryResolveReplayUri(image.GetString(), manifestUri, PlatformKind.Twitch, out var uri))
                    break;
                urls.Add(uri);
            }
            if (urls.Count != images.GetArrayLength()) continue;
            if (selected is null || Math.Abs(width - 192) < Math.Abs(selected.Width - 192))
                selected = new(width, height, columns, rows, count, duration, interval, urls);
        }
        return selected;
    }

    internal TwitchSeekPreviewFrame? GetFrame(double seconds)
    {
        // A growing archive can outrun its last published storyboard. Keep the time preview
        // there instead of labeling an old image with a newer timestamp.
        if (!double.IsFinite(seconds) || seconds > Duration) return null;
        var index = (int)Math.Clamp(Math.Floor(Math.Max(0, seconds) / Interval), 0, Count - 1);
        var cellsPerImage = Columns * Rows;
        var cell = index % cellsPerImage;
        return new(Images[index / cellsPerImage], cell % Columns * Width, cell / Columns * Height, Width, Height);
    }

    private static int ReadInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : 0;
}

internal sealed record TwitchSeekPreviewFrame(Uri ImageUri, int X, int Y, int Width, int Height);
