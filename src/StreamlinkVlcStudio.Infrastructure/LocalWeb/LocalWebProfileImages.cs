using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Vod;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static class LocalWebProfileImages
{
    internal static string PublicUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : "";

    private static string CachedPath(StreamTarget target) => Path.Combine(Path.GetDirectoryName(target.LocalMediaPath)!, OfflineVodProfileImage.FileName);

    internal static bool HasCachedImage(StreamTarget target)
    {
        if (!target.IsOfflineVod || string.IsNullOrEmpty(target.LocalMediaPath)) return false;
        try
        {
            var path = CachedPath(target);
            OfflineVodPackage.RequireRegularPath(Path.GetDirectoryName(path)!);
            OfflineVodPackage.RequireRegularPath(path);
            return new FileInfo(path) is { Exists: true, Length: > 0 and <= OfflineVodProfileImage.MaximumBytes };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static async Task ServeCachedAsync(HttpContext context, StreamTarget target)
    {
        if (!HasCachedImage(target)) { context.Response.StatusCode = 404; return; }
        // Only the fixed, adjacent cache file in an acquired offline package is readable.
        var bytes = await BoundedByteReader.ReadFileAsync(CachedPath(target), OfflineVodProfileImage.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
        var type = bytes is null ? null : LocalWebImageContent.ContentType(bytes);
        if (type is null) { context.Response.StatusCode = 404; return; }
        context.Response.ContentType = type;
        context.Response.ContentLength = bytes!.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

}
