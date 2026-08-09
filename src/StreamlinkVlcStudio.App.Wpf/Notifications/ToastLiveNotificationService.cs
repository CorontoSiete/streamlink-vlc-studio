using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Notifications;

/// <summary>
/// Windows toast implementation of <see cref="ILiveNotificationService"/> built on the
/// Windows Community Toolkit. Works for unpackaged WPF apps: the toolkit registers the
/// notification activator automatically the first time a toast is shown.
/// </summary>
public sealed class ToastLiveNotificationService : ILiveNotificationService, IDisposable
{
    private const string ToastGroup = "followed-live";
    private const int ToastTagMaxLength = 60;
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly IAppLogger logger;
    private readonly LiveNotificationDeliveryGate deliveryGate = new();
    private int disposed;

    public ToastLiveNotificationService(IAppLogger logger)
    {
        this.logger = logger;
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    public event Action<NotificationActivation>? Activated;

    public bool IsEnabled
    {
        get => deliveryGate.IsEnabled;
        set => deliveryGate.IsEnabled = value;
    }

    public void NotifyChannelLive(LiveChannelNotification notification)
    {
        if (!deliveryGate.TryBegin(out var deliveryGeneration))
        {
            return;
        }

        // Build and show off the calling (UI/refresh) thread so the best-effort
        // thumbnail download never blocks it. All failures are swallowed and logged.
        _ = Task.Run(() => ShowAsync(notification, deliveryGeneration));
    }

    private async Task ShowAsync(LiveChannelNotification notification, long deliveryGeneration)
    {
        try
        {
            var thumbnailPath = await TryDownloadThumbnailAsync(notification.ThumbnailUrl).ConfigureAwait(false);
            var platformText = notification.Platform.ToString();

            var builder = new ToastContentBuilder()
                .AddArgument("action", "watch")
                .AddArgument("platform", platformText)
                .AddArgument("channel", notification.Channel)
                .AddText($"{notification.DisplayName} is live");

            var subtitle = BuildSubtitle(notification);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                builder.AddText(subtitle);
            }

            if (!string.IsNullOrWhiteSpace(notification.Title))
            {
                builder.AddText(notification.Title);
            }

            if (thumbnailPath is not null)
            {
                builder.AddHeroImage(new Uri(thumbnailPath));
            }

            builder.AddButton(new ToastButton()
                .SetContent("Watch")
                .AddArgument("action", "watch")
                .AddArgument("platform", platformText)
                .AddArgument("channel", notification.Channel));

            var tag = BuildTag(notification);
            deliveryGate.TryRunIfCurrent(deliveryGeneration, () => builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = ToastGroup;
            }));
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Notifications", $"Failed to show live toast for {notification.Channel}.", ex);
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var arguments = ToastArguments.Parse(e.Argument);
            if (!arguments.TryGetValue("action", out var action) ||
                !string.Equals(action, "watch", StringComparison.Ordinal))
            {
                return;
            }

            if (!arguments.TryGetValue("platform", out var platformText) ||
                !Enum.TryParse<PlatformKind>(platformText, ignoreCase: true, out var platform))
            {
                return;
            }

            if (!arguments.TryGetValue("channel", out var channel) ||
                string.IsNullOrWhiteSpace(channel))
            {
                return;
            }

            Activated?.Invoke(new NotificationActivation(platform, channel));
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Notifications", "Failed to handle toast activation.", ex);
        }
    }

    private static string BuildSubtitle(LiveChannelNotification notification)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(notification.CategoryName))
        {
            parts.Add(notification.CategoryName);
        }

        if (notification.ViewerCount is { } viewers && viewers >= 0)
        {
            parts.Add($"{viewers.ToString("N0", CultureInfo.CurrentCulture)} viewers");
        }

        return string.Join(" · ", parts);
    }

    private async Task<string?> TryDownloadThumbnailAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(ThumbnailTimeout);
            using var response = await HttpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxThumbnailBytes)
            {
                return null;
            }

            var extension = ResolveImageExtension(response.Content.Headers.ContentType?.MediaType);
            if (extension is null)
            {
                return null;
            }

            var bytes = await ReadThumbnailBytesAsync(response.Content, cancellation.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            var directory = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudio", "toast");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, BuildThumbnailFileName(url) + extension);
            await File.WriteAllBytesAsync(path, bytes, cancellation.Token).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Debug, "Notifications", $"Could not load toast thumbnail from {url}.", ex);
            return null;
        }
    }

    private static async Task<byte[]?> ReadThumbnailBytesAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            if (destination.Length + bytesRead > MaxThumbnailBytes)
            {
                return null;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }

        return destination.Length == 0 ? null : destination.ToArray();
    }

    private static string? ResolveImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        _ => null,
    };

    private static string BuildThumbnailFileName(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static string BuildTag(LiveChannelNotification notification)
    {
        var raw = $"{notification.Platform}-{notification.Channel}";
        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        var tag = builder.ToString();
        return tag.Length <= ToastTagMaxLength ? tag : tag[..ToastTagMaxLength];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = ThumbnailTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");
        return client;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        deliveryGate.Dispose();
        ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
    }
}
