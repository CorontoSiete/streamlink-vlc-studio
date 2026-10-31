using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Logging;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

public sealed partial class VodDownloadService
{
    private readonly IVodMediaConverter converter;
    private VodDownloadFormat downloadFormat;
    private VodDownloadOptions formatOptions = new("", []);

    public async Task ChangeDownloadFormatAsync(VodDownloadFormat format, VodDownloadOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        ArgumentNullException.ThrowIfNull(options);
        List<VodDownloadItem> changed;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            downloadFormat = format;
            // Conversion needs the executable location, never provider credentials.
            formatOptions = new VodDownloadOptions(options.StreamlinkPath, []);
            changed = await ApplyDownloadFormatCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        foreach (var item in changed) Publish(item);
    }

    private async Task<List<VodDownloadItem>> ApplyDownloadFormatCoreAsync(CancellationToken cancellationToken)
    {
        List<VodDownloadItem> changed = [];
        foreach (var item in items.Values.ToArray())
        {
            VodDownloadItem updated;
            if (item.State == VodDownloadState.Completed && item.Format != downloadFormat)
                updated = await QueueConversionCoreAsync(item, downloadFormat, formatOptions, cancellationToken).ConfigureAwait(false);
            else if (((item.State is VodDownloadState.ConversionQueued or VodDownloadState.Converting) && item.RequestedFormat != downloadFormat) ||
                (item.State == VodDownloadState.Completed && item.RequestedFormat is not null))
            {
                updated = item with
                {
                    RequestedFormat = item.State == VodDownloadState.Completed ? null : downloadFormat,
                    Error = "",
                    Revision = item.Revision + 1
                };
                await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
                items[item.Id] = updated;
            }
            else continue;
            changed.Add(updated);
        }
        return changed;
    }

    private async Task<VodDownloadItem> QueueConversionCoreAsync(VodDownloadItem item, VodDownloadFormat format,
        VodDownloadOptions options, CancellationToken cancellationToken)
    {
        var changed = item with
        {
            State = VodDownloadState.ConversionQueued,
            RequestedFormat = format,
            Error = "",
            Revision = item.Revision + 1
        };
        await SaveCoreAsync(changed, cancellationToken).ConfigureAwait(false);
        items[item.Id] = changed;
        var job = new Job(item.Id, item.Target, new VodDownloadOptions(options.StreamlinkPath, []), shutdown.Token)
        {
            IsConversion = true
        };
        jobs[item.Id] = job;
        queue.Writer.TryWrite(job);
        return changed;
    }

    private async Task RunConversionJobAsync(Job job)
    {
        var token = job.Cancellation.Token;
        while (true)
        {
            VodDownloadItem item;
            string sourceDirectory;
            VodDownloadFormat format;
            bool alreadyConverted;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!jobs.TryGetValue(job.Id, out var current) || !ReferenceEquals(current, job) ||
                    !items.TryGetValue(job.Id, out item!) ||
                    item.State is not (VodDownloadState.ConversionQueued or VodDownloadState.Converting)) return;
                token.ThrowIfCancellationRequested();
                ValidateOwnedPaths(job.Id);
                sourceDirectory = MediaDirectory(job.Id);
                format = item.RequestedFormat ?? downloadFormat;
                alreadyConverted = item.Format == format;
                DeleteOwnedDirectory(job.Id, ConversionDirectory(job.Id));
                item = item with
                {
                    State = alreadyConverted ? VodDownloadState.Completed : VodDownloadState.Converting,
                    RequestedFormat = alreadyConverted ? null : format,
                    Error = "",
                    Revision = item.Revision + 1
                };
                await SaveCoreAsync(item, token).ConfigureAwait(false);
                items[job.Id] = item;
                job.WorkingFolder = alreadyConverted ? null : ".conversion";
            }
            finally { gate.Release(); }
            Publish(item);
            if (alreadyConverted) return;

            OfflineVodPackage source;
            try { source = await OfflineVodPackage.ValidateAsync(sourceDirectory, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                job.SourceValidationFailed = true;
                throw;
            }
            var output = ConversionDirectory(job.Id);
            var package = await converter.ConvertAsync(sourceDirectory, source, output, format,
                job.Options.StreamlinkPath, token).ConfigureAwait(false);
            if (package.Format != format) throw new InvalidDataException("The converted VOD has an unexpected format.");
            CopyProfileImage(sourceDirectory, output);
            await OfflineVodPackage.ValidateAsync(output, token).ConfigureAwait(false);

            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                item = GetItemCore(job.Id);
                if (item.State != VodDownloadState.Converting) return;
                // A newer selection supersedes this output before anything is committed.
                if (item.RequestedFormat != format)
                {
                    DeleteOwnedDirectory(job.Id, output);
                    continue;
                }
                var mediaFolder = "media-" + Guid.NewGuid().ToString("N");
                await PromoteMediaCoreAsync(job.Id, ".conversion", mediaFolder, token).ConfigureAwait(false);
                job.WorkingFolder = null;
                job.UncommittedMediaFolder = mediaFolder;
                var completed = CompleteItem(item, package, mediaFolder);
                await SaveCoreAsync(completed, CancellationToken.None).ConfigureAwait(false);
                items[job.Id] = item = completed;
                job.UncommittedMediaFolder = null;
                CleanupObsoleteMediaCore(job.Id);
            }
            finally { gate.Release(); }
            Publish(item);
            logger.WriteSafely(AppLogLevel.Info, "VOD downloads", $"Converted VOD {job.Id:N} to {format}.");
            return;
        }
    }

    private async Task FinishFailedConversionAsync(Job job, Exception exception)
    {
        VodDownloadItem? changed = null;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!jobs.TryGetValue(job.Id, out var current) || !ReferenceEquals(current, job) ||
                !items.TryGetValue(job.Id, out var item) ||
                item.State is not (VodDownloadState.ConversionQueued or VodDownloadState.Converting)) return;
            var reason = shutdown.IsCancellationRequested ? "Conversion interrupted." :
                job.Cancellation.IsCancellationRequested ? "Conversion canceled." :
                "Could not convert to " + (item.RequestedFormat == VodDownloadFormat.Mp4 ? "MP4" : "HLS") + ": " + ErrorText(exception);
            changed = job.SourceValidationFailed
                ? item with
                {
                    State = VodDownloadState.Failed,
                    RequestedFormat = null,
                    LocalMediaPath = "",
                    Error = "The original download failed validation: " + ErrorText(exception) + " Retry to download the full VOD again.",
                    Revision = item.Revision + 1
                }
                : RestoreOriginalAfterConversion(item, reason + " The original download has been kept. Retry to convert it again.");
            items[job.Id] = changed;
            job.WorkingFolder = null;
            CleanupTemporaryMediaCore(job.Id);
            if (job.UncommittedMediaFolder is { } folder) QueueDirectoryCleanupCore(job.Id, folder);
            try { await SaveCoreAsync(changed, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception saveException) { logger.WriteSafely(AppLogLevel.Error, "VOD downloads", "Could not save the conversion result; the original download remains recoverable.", saveException); }
            if (changed.Error.Length > 0) logger.WriteSafely(AppLogLevel.Warning, "VOD downloads", changed.Error);
        }
        finally { gate.Release(); }
        if (changed is not null) Publish(changed);
    }

    private static VodDownloadItem RestoreOriginalAfterConversion(VodDownloadItem item, string error) => item with
    {
        State = VodDownloadState.Completed,
        RequestedFormat = item.RequestedFormat == item.Format ? null : item.RequestedFormat,
        Error = item.RequestedFormat == item.Format ? "" : error,
        Revision = item.Revision + 1
    };

    private static void CopyProfileImage(string sourceDirectory, string outputDirectory)
    {
        var source = Path.Combine(sourceDirectory, OfflineVodProfileImage.FileName);
        OfflineVodPackage.RequireRegularPath(source);
        if (File.Exists(source) && new FileInfo(source).Length is > 0 and <= OfflineVodProfileImage.MaximumBytes)
            File.Copy(source, Path.Combine(outputDirectory, OfflineVodProfileImage.FileName));
    }

    private static bool IsMediaFolder(string? name) => string.Equals(name, "media", StringComparison.OrdinalIgnoreCase) ||
        (name is not null && name.StartsWith("media-", StringComparison.OrdinalIgnoreCase) &&
         Guid.TryParseExact(name["media-".Length..], "N", out var id) && id != Guid.Empty);

    private string ConversionDirectory(Guid id) => Path.Combine(ItemDirectory(id), ".conversion");
}
