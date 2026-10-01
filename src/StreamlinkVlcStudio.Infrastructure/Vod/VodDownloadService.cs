using System.Text.Json;
using System.Threading.Channels;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Io;
using StreamlinkVlcStudio.Infrastructure.Logging;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

public sealed class VodDownloadService : IVodDownloadService
{
    private readonly IStreamlinkService streamlink;
    private readonly ITwitchSubOnlyVodResolver? twitchFallback;
    private readonly IAppLogger logger;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly ReplayUrlSecurityValidator validator;
    private readonly Func<StreamTarget, CancellationToken, Task<StreamTarget>>? resolveKick;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<Job> queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<Guid, VodDownloadItem> items = [];
    private readonly Dictionary<Guid, string> itemRoots = [];
    private readonly HashSet<string> libraryDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Job> jobs = [];
    private readonly VodDownloadBandwidthLimiter bandwidthLimiter = new();
    private readonly Task worker;
    private readonly object disposalGate = new();
    private Task? disposal;
    private bool loaded;
    private bool disposed;

    public VodDownloadService(string downloadDirectory, IStreamlinkService streamlink, IAppLogger logger,
        ITwitchSubOnlyVodResolver? twitchFallback = null, IEnumerable<string>? previousDownloadDirectories = null)
        : this(downloadDirectory, streamlink, logger, twitchFallback, null, null,
            new KickVodDownloadResolver(logger).ResolveAsync, previousDownloadDirectories)
    { }

    internal VodDownloadService(string downloadDirectory, IStreamlinkService streamlink, IAppLogger logger,
        ITwitchSubOnlyVodResolver? twitchFallback, HttpClient? client, ReplayUrlSecurityValidator? validator,
        Func<StreamTarget, CancellationToken, Task<StreamTarget>>? resolveKick = null,
        IEnumerable<string>? previousDownloadDirectories = null)
    {
        DownloadDirectory = NormalizeDownloadDirectory(downloadDirectory);
        libraryDirectories.Add(DownloadDirectory);
        foreach (var previousDirectory in previousDownloadDirectories ?? [])
        {
            try { libraryDirectories.Add(NormalizeDownloadDirectory(previousDirectory)); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                logger.Write(AppLogLevel.Warning, "VOD downloads", "An invalid previous download folder was ignored.", exception);
            }
        }
        this.streamlink = streamlink;
        this.logger = logger;
        this.twitchFallback = twitchFallback;
        this.client = client ?? HttpClientFactory.Create(TimeSpan.FromSeconds(45), includeUserAgent: true, allowAutoRedirect: false);
        ownsClient = client is null;
        this.validator = validator ?? ReplayUrlSecurityValidator.Shared;
        this.resolveKick = resolveKick;
        worker = Task.Run(ProcessQueueAsync);
    }

    public string DownloadDirectory { get; private set; }
    public event Action<VodDownloadItem>? DownloadChanged;

    public void SetBandwidthLimit(long bytesPerSecond) => bandwidthLimiter.SetLimit(bytesPerSecond);

    public async Task ChangeDownloadDirectoryAsync(string downloadDirectory, CancellationToken cancellationToken = default)
    {
        var directory = NormalizeDownloadDirectory(downloadDirectory);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            OfflineVodPackage.RequireRegularPath(directory);
            Directory.CreateDirectory(directory);
            OfflineVodPackage.RequireRegularPath(directory);
            using var writeProbe = new FileStream(Path.Combine(directory, ".streamstudio-write-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            await LoadDirectoryCoreAsync(directory, cancellationToken).ConfigureAwait(false);
            libraryDirectories.Add(directory);
            DownloadDirectory = directory;
        }
        finally { gate.Release(); }
    }

    private static string NormalizeDownloadDirectory(string downloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        if (!Path.IsPathFullyQualified(downloadDirectory))
            throw new ArgumentException("Select a fully qualified local download folder.", nameof(downloadDirectory));
        var directory = Path.GetFullPath(downloadDirectory);
        if (new Uri(directory).IsUnc)
            throw new ArgumentException("The offline VOD library must be on a local drive, not a network share.", nameof(downloadDirectory));
        return Path.TrimEndingDirectorySeparator(directory);
    }

    public static string GetDefaultDownloadDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return GetDefaultDownloadDirectory([
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(profile, "Videos"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)]);
    }

    internal static string GetDefaultDownloadDirectory(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
            if (!string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && !new Uri(folder).IsUnc)
                return Path.Combine(folder, "StreamStudio VODs");
        throw new DirectoryNotFoundException("A local folder is required for the offline VOD library.");
    }

    public async Task<IReadOnlyList<VodDownloadItem>> GetDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return items.Values.OrderByDescending(item => item.CreatedAtUtc).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<VodDownloadItem> EnqueueAsync(VodDownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = CanonicalTarget(request.Target);
        var options = CopyOptions(request.Options);
        var quality = NormalizeQuality(request.Quality);
        VodDownloadItem item;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var existing = items.Values.FirstOrDefault(candidate => candidate.Target.TabIdentityKey == target.TabIdentityKey &&
                candidate.Quality.Equals(quality, StringComparison.OrdinalIgnoreCase) &&
                (candidate.IsActive || candidate.State == VodDownloadState.Completed));
            if (existing is not null) return existing;
            item = new VodDownloadItem(Guid.NewGuid(), target, quality, DateTimeOffset.UtcNow);
            itemRoots.Add(item.Id, DownloadDirectory);
            try { await SaveCoreAsync(item, cancellationToken).ConfigureAwait(false); }
            catch
            {
                itemRoots.Remove(item.Id);
                throw;
            }
            items.Add(item.Id, item);
            var job = new Job(item.Id, request.Target, options, shutdown.Token);
            jobs.Add(item.Id, job);
            queue.Writer.TryWrite(job);
        }
        finally { gate.Release(); }
        Publish(item);
        return item;
    }

    public async Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        VodDownloadItem? changed = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!items.TryGetValue(id, out var item) || !item.IsActive) return;
            if (jobs.TryGetValue(id, out var job)) job.Cancellation.Cancel();
            if (item.State == VodDownloadState.Queued)
            {
                changed = item with { State = VodDownloadState.Canceled, Revision = item.Revision + 1 };
                await SaveCoreAsync(changed, cancellationToken).ConfigureAwait(false);
                items[id] = changed;
            }
        }
        finally { gate.Release(); }
        if (changed is not null) Publish(changed);
    }

    public async Task RetryAsync(Guid id, VodDownloadOptions options, CancellationToken cancellationToken = default)
    {
        options = CopyOptions(options);
        VodDownloadItem changed;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = GetItemCore(id);
            if (item.State is not (VodDownloadState.Failed or VodDownloadState.Canceled or VodDownloadState.Interrupted))
                throw new InvalidOperationException("Only failed, canceled, or interrupted VOD downloads can be retried.");
            changed = item with
            {
                State = VodDownloadState.Queued,
                Error = "",
                CompletedSegments = 0,
                TotalSegments = 0,
                BytesDownloaded = 0,
                LocalMediaPath = "",
                Revision = item.Revision + 1
            };
            await SaveCoreAsync(changed, cancellationToken).ConfigureAwait(false);
            items[id] = changed;
            var job = new Job(id, changed.Target, options, shutdown.Token);
            jobs[id] = job;
            queue.Writer.TryWrite(job);
        }
        finally { gate.Release(); }
        Publish(changed);
    }

    public async Task<StreamTarget> GetOfflineTargetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        VodDownloadItem? changed = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = GetItemCore(id);
            if (item.State != VodDownloadState.Completed) throw new InvalidOperationException("This VOD has not finished downloading.");
            try
            {
                ValidateOwnedPaths(id);
                await OfflineVodPackage.ValidateAsync(MediaDirectory(id), cancellationToken).ConfigureAwait(false);
                return item.OfflineTarget with
                {
                    ProfileImageUrl = OfflineVodProfileImage.GetUrl(MediaDirectory(id), item.Target)
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                changed = item with
                {
                    State = VodDownloadState.Failed,
                    Error = ErrorText(exception),
                    LocalMediaPath = "",
                    Revision = item.Revision + 1
                };
                await SaveCoreAsync(changed, cancellationToken).ConfigureAwait(false);
                items[id] = changed;
                throw new InvalidDataException(changed.Error, exception);
            }
        }
        finally
        {
            gate.Release();
            if (changed is not null) Publish(changed);
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (GetItemCore(id).IsActive) throw new InvalidOperationException("Cancel this download before deleting it.");
            DeleteOwnedDirectory(id, ItemDirectory(id));
            items.Remove(id);
            itemRoots.Remove(id);
        }
        finally { gate.Release(); }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        foreach (var directory in libraryDirectories.OrderBy(directory => !directory.Equals(DownloadDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            try { await LoadDirectoryCoreAsync(directory, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException &&
                !directory.Equals(DownloadDirectory, StringComparison.OrdinalIgnoreCase))
            {
                logger.Write(AppLogLevel.Warning, "VOD downloads", "A previous download folder is unavailable; its files have been preserved.", exception);
            }
        }
        loaded = true;
    }

    private async Task LoadDirectoryCoreAsync(string root, CancellationToken cancellationToken)
    {
        OfflineVodPackage.RequireRegularPath(root);
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty || items.ContainsKey(id)) continue;
                itemRoots[id] = root;
                try
                {
                    ValidateOwnedPaths(id);
                    var metadataPath = Path.Combine(directory, "download.json");
                    OfflineVodPackage.RequireRegularPath(metadataPath);
                    var bytes = await BoundedByteReader.ReadFileAsync(metadataPath, 128 * 1024, cancellationToken).ConfigureAwait(false);
                    if (bytes is null) continue;
                    var item = JsonSerializer.Deserialize<VodDownloadItem>(bytes);
                    if (item is null || item.Id != id || !Enum.IsDefined(item.State) || item.Revision < 0)
                        throw new InvalidDataException("Invalid VOD download metadata.");
                    item = item with
                    {
                        Target = CanonicalTarget(item.Target),
                        Quality = NormalizeQuality(item.Quality),
                        LocalMediaPath = ""
                    };
                    var recovered = false;
                    if (Directory.Exists(MediaDirectory(id)))
                    {
                        try
                        {
                            var package = await OfflineVodPackage.ValidateAsync(MediaDirectory(id), cancellationToken).ConfigureAwait(false);
                            item = CompleteItem(item, package);
                            recovered = true;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            item = item with { State = VodDownloadState.Failed, Error = ErrorText(exception), Revision = item.Revision + 1 };
                        }
                    }
                    else if (item.State == VodDownloadState.Completed)
                        item = item with { State = VodDownloadState.Failed, Error = "The downloaded VOD files are missing. Download it again.", Revision = item.Revision + 1 };
                    if (!recovered && item.IsActive)
                        item = item with
                        {
                            State = VodDownloadState.Interrupted,
                            Error = "The app closed before this download finished. Retry to download the full VOD again.",
                            Revision = item.Revision + 1
                        };
                    items[id] = item;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.Write(AppLogLevel.Warning, "VOD downloads", $"Could not load download {id:N}; its files have been preserved.", exception);
                }
                finally
                {
                    if (!items.ContainsKey(id)) itemRoots.Remove(id);
                }
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                try { await RunJobAsync(job).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    await FinishFailedJobAsync(job, exception).ConfigureAwait(false);
                }
                finally
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (jobs.TryGetValue(job.Id, out var current) && ReferenceEquals(job, current)) jobs.Remove(job.Id);
                    }
                    finally { gate.Release(); }
                    job.Cancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task RunJobAsync(Job job)
    {
        VodDownloadItem item;
        string partialDirectory;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!jobs.TryGetValue(job.Id, out var current) || !ReferenceEquals(current, job) ||
                !items.TryGetValue(job.Id, out item!) || item.State != VodDownloadState.Queued) return;
            job.Cancellation.Token.ThrowIfCancellationRequested();
            partialDirectory = PartialDirectory(job.Id);
            DeleteOwnedDirectory(job.Id, partialDirectory);
            DeleteOwnedDirectory(job.Id, MediaDirectory(job.Id));
            item = item with { State = VodDownloadState.Resolving, Revision = item.Revision + 1 };
            await SaveCoreAsync(item, job.Cancellation.Token).ConfigureAwait(false);
            items[job.Id] = item;
        }
        finally { gate.Release(); }
        Publish(item);
        var token = job.Cancellation.Token;
        if (Uri.TryCreate(job.Target.Url, UriKind.Absolute, out var source) && source.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            await validator.ValidateAsync(source, job.Target.Platform, token).ConfigureAwait(false);
        else
        {
            var page = VodDownloadUrlParser.Parse(job.Target.Url);
            if (page.Platform != item.Target.Platform || !page.MediaId.Equals(item.Target.MediaId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The VOD URL does not match the selected video.");
        }
        var request = new StreamTransportRequest(job.Target, item.Quality, job.Options.StreamlinkPath, false, job.Options.CustomArguments);
        Uri playlistUri;
        var allowLocalPlaylist = false;
        try
        {
            playlistUri = job.Target.IsExplicitKickVod && resolveKick is not null
                ? await ResolveKickPlaylistAsync(job, item.Target, request, token).ConfigureAwait(false)
                : (await streamlink.ResolveStreamUrlAsync(request, token).ConfigureAwait(false)).StreamUri;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && job.Target.IsExplicitTwitchVod && twitchFallback is not null)
        {
            try
            {
                var fallback = await twitchFallback.ResolveAsync(new TwitchSubOnlyVodRequest(item.Target.MediaId, item.Quality), token).ConfigureAwait(false);
                playlistUri = fallback.PlaybackUri;
                allowLocalPlaylist = playlistUri.IsFile && !playlistUri.IsUnc;
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Twitch VOD resolution failed: {exception.Message} Fallback: {fallbackException.Message}", fallbackException);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException && resolveKick is null &&
            job.Target.IsExplicitKickVod && job.Target.Url != item.Target.Url)
        {
            playlistUri = (await streamlink.ResolveStreamUrlAsync(request with { Target = item.Target }, token).ConfigureAwait(false)).StreamUri;
        }
        var downloader = new HlsVodDownloader(client, validator, bandwidthLimiter);
        var package = await downloader.DownloadAsync(playlistUri, item.Target.Platform, partialDirectory,
            update => UpdateProgressAsync(job, update), token, allowLocalPlaylist).ConfigureAwait(false);
        StreamTarget profileTarget;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { profileTarget = GetItemCore(job.Id).Target; }
        finally { gate.Release(); }
        await OfflineVodProfileImage.TryDownloadAsync(client, validator, profileTarget, partialDirectory,
            logger, token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateOwnedPaths(job.Id);
            Directory.Move(PartialDirectory(job.Id), MediaDirectory(job.Id));
            item = CompleteItem(GetItemCore(job.Id), package);
            await SaveCoreAsync(item, CancellationToken.None).ConfigureAwait(false);
            items[job.Id] = item;
        }
        finally { gate.Release(); }
        Publish(item);
        logger.Write(AppLogLevel.Info, "VOD downloads", $"Downloaded {item.Target.Platform} VOD {item.Target.MediaId}: {item.CompletedSegments} segments, {item.BytesDownloaded} bytes.");
    }

    private async Task<Uri> ResolveKickPlaylistAsync(Job job, StreamTarget canonicalTarget,
        StreamTransportRequest request, CancellationToken cancellationToken)
    {
        if (job.Target.Url != canonicalTarget.Url)
        {
            try { return (await streamlink.ResolveStreamUrlAsync(request, cancellationToken).ConfigureAwait(false)).StreamUri; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Info, "VOD downloads", "Refreshing the Kick VOD source after resolution failed: " + ErrorText(exception, job.Options));
            }
        }
        StreamTarget resolvedTarget;
        StreamlinkResolvedUrl result;
        try
        {
            resolvedTarget = await resolveKick!(canonicalTarget, cancellationToken).ConfigureAwait(false);
            await validator.ValidateAsync(new Uri(resolvedTarget.Url), PlatformKind.Kick, cancellationToken).ConfigureAwait(false);
            result = await streamlink.ResolveStreamUrlAsync(request with { Target = resolvedTarget }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "VOD downloads", "Kick website resolution failed; trying the configured Streamlink Kick plugin: " + ErrorText(exception, job.Options));
            try { return (await streamlink.ResolveStreamUrlAsync(request with { Target = canonicalTarget }, cancellationToken).ConfigureAwait(false)).StreamUri; }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Kick VOD resolution failed: {exception.Message} Streamlink: {fallbackException.Message}", fallbackException);
            }
        }
        VodDownloadItem changed;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = GetItemCore(job.Id);
            changed = item with { Target = CanonicalTarget(resolvedTarget), Revision = item.Revision + 1 };
            await SaveCoreAsync(changed, cancellationToken).ConfigureAwait(false);
            items[job.Id] = changed;
        }
        finally { gate.Release(); }
        Publish(changed);
        return result.StreamUri;
    }

    private async Task UpdateProgressAsync(Job job, VodDownloadProgress progress)
    {
        VodDownloadItem? changed = null;
        await gate.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
        try
        {
            var item = GetItemCore(job.Id);
            if (item.State is not (VodDownloadState.Resolving or VodDownloadState.Downloading) || progress.BytesDownloaded < item.BytesDownloaded) return;
            changed = item with
            {
                State = VodDownloadState.Downloading,
                CompletedSegments = progress.CompletedSegments,
                TotalSegments = progress.TotalSegments,
                BytesDownloaded = progress.BytesDownloaded,
                Duration = progress.Duration,
                Revision = item.Revision + 1
            };
            if (item.State == VodDownloadState.Resolving) await SaveCoreAsync(changed, job.Cancellation.Token).ConfigureAwait(false);
            items[job.Id] = changed;
        }
        finally { gate.Release(); }
        if (changed is not null) Publish(changed);
    }

    private async Task FinishFailedJobAsync(Job job, Exception exception)
    {
        VodDownloadItem? changed = null;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!jobs.TryGetValue(job.Id, out var current) || !ReferenceEquals(current, job) ||
                !items.TryGetValue(job.Id, out var item) || !item.IsActive) return;
            var state = shutdown.IsCancellationRequested ? VodDownloadState.Interrupted :
                job.Cancellation.IsCancellationRequested ? VodDownloadState.Canceled : VodDownloadState.Failed;
            var error = state == VodDownloadState.Failed ? ErrorText(exception, job.Options) :
                state == VodDownloadState.Interrupted ? "The app closed before this download finished. Retry to download it again." : "";
            try { DeleteOwnedDirectory(job.Id, PartialDirectory(job.Id)); }
            catch (Exception cleanupException) { logger.Write(AppLogLevel.Warning, "VOD downloads", "Partial VOD files could not be removed.", cleanupException); }
            changed = item with { State = state, Error = error, LocalMediaPath = "", Revision = item.Revision + 1 };
            items[job.Id] = changed;
            try { await SaveCoreAsync(changed, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception saveException) { logger.Write(AppLogLevel.Error, "VOD downloads", "Could not save the download failure; the previous record remains recoverable.", saveException); }
            if (state == VodDownloadState.Failed) logger.Write(AppLogLevel.Warning, "VOD downloads", $"Download {job.Id:N} failed: {error}");
        }
        finally { gate.Release(); }
        if (changed is not null) Publish(changed);
    }

    private VodDownloadItem CompleteItem(VodDownloadItem item, OfflineVodPackage package) => item with
    {
        State = VodDownloadState.Completed,
        CompletedSegments = package.SegmentCount,
        TotalSegments = package.SegmentCount,
        BytesDownloaded = package.BytesDownloaded,
        Duration = package.Duration,
        Error = "",
        LocalMediaPath = Path.Combine(MediaDirectory(item.Id), OfflineVodPackage.ManifestName),
        Revision = item.Revision + 1
    };

    private Task SaveCoreAsync(VodDownloadItem item, CancellationToken cancellationToken)
    {
        var root = ItemRoot(item.Id);
        OfflineVodPackage.RequireRegularPath(root);
        Directory.CreateDirectory(root);
        OfflineVodPackage.RequireRegularPath(root);
        var directory = ItemDirectory(item.Id);
        OfflineVodPackage.RequireRegularPath(directory);
        Directory.CreateDirectory(directory);
        ValidateOwnedPaths(item.Id);
        var path = Path.Combine(directory, "download.json");
        OfflineVodPackage.RequireRegularPath(path);
        var stored = item with { LocalMediaPath = "" };
        return AtomicFile.WriteAsync(path, (stream, token) => JsonSerializer.SerializeAsync(stream, stored, cancellationToken: token),
            cancellationToken, flushToDisk: true);
    }

    private void DeleteOwnedDirectory(Guid id, string path)
    {
        ValidateOwnedPaths(id);
        var owned = ItemDirectory(id);
        path = Path.GetFullPath(path);
        if (path != owned && path != PartialDirectory(id) && path != MediaDirectory(id))
            throw new IOException("Refusing to delete files outside this VOD download.");
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) ValidateDeleteTree(entry);
        Directory.Delete(path, recursive: true);
    }

    private static void ValidateDeleteTree(string path)
    {
        OfflineVodPackage.RequireRegularPath(path);
        if (Directory.Exists(path))
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) ValidateDeleteTree(entry);
    }

    private void ValidateOwnedPaths(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Invalid VOD download identity.");
        var directory = ItemDirectory(id);
        var root = ItemRoot(id);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The VOD download path is outside its library.");
        OfflineVodPackage.RequireRegularPath(root);
        OfflineVodPackage.RequireRegularPath(directory);
        OfflineVodPackage.RequireRegularPath(PartialDirectory(id));
        OfflineVodPackage.RequireRegularPath(MediaDirectory(id));
    }

    private string ItemRoot(Guid id) => itemRoots.TryGetValue(id, out var root) ? root : DownloadDirectory;
    private string ItemDirectory(Guid id) => Path.Combine(ItemRoot(id), id.ToString("N"));
    private string PartialDirectory(Guid id) => Path.Combine(ItemDirectory(id), ".partial");
    private string MediaDirectory(Guid id) => Path.Combine(ItemDirectory(id), "media");
    private VodDownloadItem GetItemCore(Guid id) => items.TryGetValue(id, out var item) ? item :
        throw new KeyNotFoundException("This VOD download is no longer in the library.");

    private static StreamTarget CanonicalTarget(StreamTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsExplicitVod || target.IsOfflineVod || string.IsNullOrWhiteSpace(target.MediaId))
            throw new ArgumentException("Only online Twitch and Kick VODs can be downloaded.");
        var page = VodDownloadUrlParser.Parse(target.Platform == PlatformKind.Twitch
            ? $"https://www.twitch.tv/videos/{target.MediaId}"
            : $"https://kick.com/{target.Channel}/videos/{target.MediaId}");
        if (target.DisplayTitle.Length > 4096 || target.Channel.Length > 128 || target.ProfileImageUrl.Length > 4096)
            throw new ArgumentException("The VOD metadata exceeds the supported limits.");
        return target with { Url = page.Url, LocalMediaPath = "" };
    }

    private static VodDownloadOptions CopyOptions(VodDownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StreamlinkPath);
        ArgumentNullException.ThrowIfNull(options.CustomArguments);
        return options with { CustomArguments = options.CustomArguments.ToArray() };
    }

    private static string NormalizeQuality(string quality)
    {
        if (string.IsNullOrWhiteSpace(quality) || quality.Length > 128 || quality.Any(char.IsControl))
            throw new ArgumentException("Select a valid VOD download quality.");
        return quality.Trim();
    }

    private static string ErrorText(Exception exception, VodDownloadOptions? options = null)
    {
        var message = exception is OperationCanceledException ? "The VOD server timed out. Retry when your connection is available." : exception.Message;
        if (options is not null)
        {
            foreach (var argument in options.CustomArguments)
            {
                if (string.IsNullOrWhiteSpace(argument)) continue;
                var separator = argument.IndexOf('=');
                var value = argument.StartsWith("-", StringComparison.Ordinal)
                    ? separator >= 0 ? argument[(separator + 1)..] : "" : argument;
                if (value.Length < 3) continue;
                message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
                separator = value.IndexOf('=');
                if (separator >= 0 && value.Length - separator > 3)
                    message = message.Replace(value[(separator + 1)..], "[REDACTED]", StringComparison.Ordinal);
            }
        }
        return FileAppLogger.Sanitize(message, 2048);
    }

    private void Publish(VodDownloadItem item) =>
        SafeEventDispatcher.Invoke(DownloadChanged, item, logger, "VOD downloads", nameof(DownloadChanged));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
            shutdown.Cancel();
            queue.Writer.TryComplete();
        }
        finally { gate.Release(); }
        await worker.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Cancellation can stop the reader before its first iteration. Release queued
            // generations as well as the current jobs, including canceled jobs replaced by retry.
            while (queue.Reader.TryRead(out var queuedJob)) queuedJob.Cancellation.Dispose();
            foreach (var item in items.Values.Where(item => item.IsActive).ToArray())
            {
                var interrupted = item with
                {
                    State = VodDownloadState.Interrupted,
                    Error = "The app closed before this download finished. Retry to download it again.",
                    Revision = item.Revision + 1
                };
                items[item.Id] = interrupted;
                try { await SaveCoreAsync(interrupted, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) { logger.Write(AppLogLevel.Warning, "VOD downloads", "Could not save an interrupted download.", exception); }
            }
            foreach (var job in jobs.Values) job.Cancellation.Dispose();
            jobs.Clear();
        }
        finally { gate.Release(); }
        if (ownsClient) client.Dispose();
        shutdown.Dispose();
    }

    private sealed class Job(Guid id, StreamTarget target, VodDownloadOptions options, CancellationToken shutdown)
    {
        internal Guid Id { get; } = id;
        internal StreamTarget Target { get; } = target;
        internal VodDownloadOptions Options { get; } = options;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    }
}
