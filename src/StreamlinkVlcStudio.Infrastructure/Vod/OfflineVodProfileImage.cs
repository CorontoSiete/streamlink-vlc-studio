using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Io;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal static class OfflineVodProfileImage
{
    internal const string FileName = "profile-image";
    internal const int MaximumBytes = 8 * 1024 * 1024;

    internal static string GetUrl(string directory, StreamTarget target)
    {
        var fallbackUrl = Uri.TryCreate(target.ProfileImageUrl, UriKind.Absolute, out var uri) &&
            ReplayUrlSecurityValidator.TryValidateProviderUri(uri, target.Platform) ? target.ProfileImageUrl : "";
        try
        {
            OfflineVodPackage.RequireRegularPath(directory);
            var path = Path.Combine(directory, FileName);
            OfflineVodPackage.RequireRegularPath(path);
            var file = new FileInfo(path);
            return file.Exists && file.Length is > 0 and <= MaximumBytes
                ? new Uri(file.FullName).AbsoluteUri
                : fallbackUrl;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return fallbackUrl;
        }
    }

    internal static async Task TryDownloadAsync(HttpClient client, ReplayUrlSecurityValidator validator,
        StreamTarget target, string directory, IAppLogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.ProfileImageUrl)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var uri = new Uri(target.ProfileImageUrl, UriKind.Absolute);
            using var response = await ValidatedReplayHttpClient.SendGetAsync(client, validator, uri, target.Platform,
                address =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, address);
                    if (target.Platform == PlatformKind.Kick) request.Headers.Referrer = new Uri("https://kick.com/");
                    return request;
                }, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json")
                throw new InvalidDataException("The broadcaster's profile image returned an error page.");
            var bytes = await BoundedByteReader.ReadAsync(response.Content, MaximumBytes, deadline.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The broadcaster's profile image is empty or exceeds the supported size.");
            OfflineVodPackage.RequireRegularPath(directory);
            var path = Path.Combine(directory, FileName);
            OfflineVodPackage.RequireRegularPath(path);
            await AtomicFile.WriteAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask(),
                deadline.Token, flushToDisk: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
                OperationCanceledException or ArgumentException or UriFormatException or InvalidOperationException or NotSupportedException)
        {
            logger.Write(AppLogLevel.Warning, "VOD downloads",
                "The broadcaster's profile image could not be cached; its saved URL has been retained.", exception);
        }
    }
}
