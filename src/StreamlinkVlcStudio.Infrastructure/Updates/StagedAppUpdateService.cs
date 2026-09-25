using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Io;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Updates;

/// <summary>Consent-separated updater whose trust root is a pinned release-signing key.</summary>
public sealed class StagedAppUpdateService : IAppUpdateService, IDisposable
{
    public const int SupportedProtocolVersion = 1;
    public const string UpdateManifestName = "UPDATE-MANIFEST.json";
    public const string UpdateSignatureName = "UPDATE-MANIFEST.sig";
    public const string SetupAssetName = AppIdentity.SetupAssetName;
    public const string ZipAssetName = AppIdentity.ZipAssetName;
    public const string AppUpgradeCode = "{85EC9B91-312C-4B8E-A293-57D749B10C4B}";
    public const string TrustedKeyId = "5983e42ba44b37a245be5208c92ee7c7268e3cea1caa4728b3ec9af66a3b71e8";
    private const string Repository = "CorontoSiete/streamlink-vlc-studio";
    private const long MaximumPackageBytes = 1024L * 1024L * 1024L;
    private static readonly TimeSpan CheckLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan PackageDownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] PublicModulus = Convert.FromBase64String(
        "y9rHGIZ3zqxmr6a/wBJSogVhjxb2UZZl6YqGDbSBYhdkUVsMAm4mbIjF5vl0QCIkHKscRLAkKPNj7IvJYtihgWriYSVsQMWv/aCMjuSBGJyCXEONfn+EsY4Q6Yvu+XHFKrwMYEcXv2ToBN1VqnFcBHEkYVzvajJZ3RhVzL4XhnS1A47Opm8HaH23XPl9Fv4Ekcp2FOcNF1HntaPVuyAFerl2T8iwuM7fmHAcstf0j6a748bAw9uyGZDYXBjNcvcl1rOUNbKX7/sDTQ9pn5AXrvqRmQFFvjiHRfw7u1myzP2ck3t7J2g+rwjoLa7UTsC0JPX4Ahk6MVH2luNoq2wsqSAKNa8g9i0kep1XmHihqdRZCgOZ45TL4c2Rr9F9AvoDbJLLV+kC+g77JYEbB8VuIg1nc2q/GfkAR/CwI7014ApKjusBWkraDB++6y7VyMK/wLrX0Bzwo91hcTI14CT5tNtQSmXXXvo96mPTitlz/AugF/dqvBWz78VkbYsfYdSn");
    private static readonly byte[] PublicExponent = [1, 0, 1];

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly string applicationDirectory;
    private readonly string updateRoot;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<AppInstallKind> detectInstallKind;
    private readonly Func<Version?> getCurrentVersion;
    private readonly RSAParameters trustedKey;
    private readonly string trustedKeyId;
    private readonly bool disposeClient;
    private readonly TimeSpan downloadIdleTimeout;
    private readonly Threading.AsyncOperationGate operationGate = new();
    private AppUpdateState state = AppUpdateState.Idle;
    private AppUpdateRelease? lastVerifiedRelease;
    private PreparedAppUpdate? preparedUpdate;

    public StagedAppUpdateService(IAppLogger logger)
        : this(logger, new HttpClient(), AppIdentity.ExecutableDirectory, GetUpdateRoot(), () => DateTimeOffset.UtcNow, true)
    {
    }

    internal StagedAppUpdateService(
        IAppLogger logger,
        HttpClient httpClient,
        string applicationDirectory,
        string updateRoot,
        Func<DateTimeOffset>? utcNow = null,
        bool disposeClient = false,
        Func<AppInstallKind>? detectInstallKind = null,
        Func<Version?>? getCurrentVersion = null,
        RSAParameters? trustedKey = null,
        string? trustedKeyId = null,
        TimeSpan? downloadIdleTimeout = null)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.applicationDirectory = Path.GetFullPath(applicationDirectory);
        this.updateRoot = Path.GetFullPath(updateRoot);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.detectInstallKind = detectInstallKind ?? (() => DetectInstallKind(this.applicationDirectory));
        this.getCurrentVersion = getCurrentVersion ?? GetCurrentVersion;
        this.trustedKey = trustedKey ?? new RSAParameters { Modulus = PublicModulus, Exponent = PublicExponent };
        this.trustedKeyId = trustedKeyId ?? (trustedKey is null ? TrustedKeyId : ComputeKeyId(this.trustedKey));
        if (!string.Equals(ComputeKeyId(this.trustedKey), this.trustedKeyId, StringComparison.Ordinal))
            throw new CryptographicException("The embedded update public key does not match its key ID.");
        this.disposeClient = disposeClient;
        this.downloadIdleTimeout = downloadIdleTimeout ?? TimeSpan.FromSeconds(60);
        if (this.downloadIdleTimeout <= TimeSpan.Zero || this.downloadIdleTimeout > PackageDownloadTimeout)
            throw new ArgumentOutOfRangeException(nameof(downloadIdleTimeout));
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StreamStudioUpdater/1");
        }
    }

    public event EventHandler<AppUpdateStateChangedEventArgs>? StateChanged;
    public AppUpdateState State => state;

    public async Task<AppUpdateCheckResult> CheckAsync(UpdateCheckReason reason, CancellationToken cancellationToken = default)
    {
        using var lease = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await CheckCoreAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppUpdateCheckResult> CheckCoreAsync(UpdateCheckReason reason, CancellationToken cancellationToken)
    {
        var previousState = State;
        // Restore the trusted identity and cache ownership together with the visible state
        // if a refresh fails after verifying a different release.
        var previousRelease = lastVerifiedRelease;
        var previousPreparedUpdate = preparedUpdate;
        SetState(new(AppUpdatePhase.Checking, "Checking for updates..."));
        try
        {
            Directory.CreateDirectory(updateRoot);
            AssertPathNoReparsePoints(updateRoot);
            CleanupCache();
            // A failed package may have been replaced or withdrawn. Retry against fresh
            // signed metadata instead of retrying the same cached release for 24 hours.
            var cached = reason == UpdateCheckReason.Startup
                ? await ReadFreshCheckAsync(cancellationToken).ConfigureAwait(false)
                : null;
            AppUpdateRelease release;
            if (cached is null)
            {
                var envelope = await FetchReleaseEnvelopeAsync(cancellationToken).ConfigureAwait(false);
                release = VerifyReleaseEnvelope(envelope);
                await WriteAtomicJsonAsync(
                    Path.Combine(updateRoot, "last-check.json"),
                    envelope,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                release = cached;
            }

            var kind = detectInstallKind();
            var current = getCurrentVersion();
            if (current is null)
            {
                const string unknownMessage = "The installed application version is unknown; automatic installation is disabled.";
                SetState(new(AppUpdatePhase.Failed, unknownMessage, release));
                return new(true, false, true, kind, release, unknownMessage, utcNow());
            }
            lastVerifiedRelease = release;
            var available = release.Version > current;
            var notifyOnly = available && kind is AppInstallKind.Zip or AppInstallKind.Unmanaged;
            if (available && !notifyOnly)
            {
                var prepared = await RestorePreparedAsync(release, cancellationToken).ConfigureAwait(false);
                if (prepared is not null)
                {
                    const string readyMessage = "Update verified. Restart and install when you're ready.";
                    SetState(new(AppUpdatePhase.Ready, readyMessage, release, prepared));
                    return new(true, true, false, kind, release, readyMessage, utcNow());
                }
            }
            var message = !available
                ? $"You're on the latest version ({current})."
                : notifyOnly
                    ? $"Version {release.Version} is available. Open the release page to update this installation."
                    : $"Version {release.Version} is available. Download it when you're ready.";
            SetState(new(available ? (notifyOnly ? AppUpdatePhase.NotifyOnly : AppUpdatePhase.Available) : AppUpdatePhase.Completed, message, release));
            return new(true, available, notifyOnly, kind, release, message, utcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lastVerifiedRelease = previousRelease;
            preparedUpdate = previousPreparedUpdate;
            SetState(previousState);
            throw;
        }
        catch (Exception ex)
        {
            lastVerifiedRelease = previousRelease;
            preparedUpdate = previousPreparedUpdate;
            // A failed refresh must not strand a previously verified release or package.
            // Downloads and installation still validate that release's identity and bytes.
            var recoveryMessage = previousState.Phase switch
            {
                AppUpdatePhase.Ready => "Your verified update is still ready to install.",
                AppUpdatePhase.Available => "You can still download the previously verified release.",
                AppUpdatePhase.DownloadFailed => "You can still retry downloading the previously verified release.",
                AppUpdatePhase.NotifyOnly => "You can still open the previously verified release page.",
                _ => null
            };
            SetState(recoveryMessage is not null
                ? previousState with { Message = $"Could not refresh updates. {recoveryMessage} {ex.Message}" }
                : new(AppUpdatePhase.Failed, $"Update check failed. {ex.Message}"));
            logger.Write(AppLogLevel.Error, "Updater", "Update check failed.", ex);
            throw;
        }
    }

    public async Task<PreparedAppUpdate> DownloadAsync(
        AppUpdateRelease release,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        using var lease = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await DownloadCoreAsync(release, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreparedAppUpdate> DownloadCoreAsync(
        AppUpdateRelease release,
        IProgress<AppUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (detectInstallKind() is AppInstallKind.Zip or AppInstallKind.Unmanaged)
        {
            throw new InvalidOperationException("This installation can only be notified about verified releases.");
        }

        ValidateRelease(release);
        if (lastVerifiedRelease is null || !ReleaseEquals(lastVerifiedRelease, release))
        {
            throw new CryptographicException("The requested release was not produced by this updater's latest signed check.");
        }
        if (getCurrentVersion() is not { } current || release.Version <= current)
            throw new InvalidOperationException("Only a newer version can be downloaded for installation.");
        var existing = await RestorePreparedAsync(release, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            SetState(new(AppUpdatePhase.Ready, "Update verified. Restart and install when you're ready.", release, existing));
            return existing;
        }
        var id = Guid.NewGuid();
        var operationsRoot = Path.Combine(updateRoot, "operations");
        Directory.CreateDirectory(operationsRoot);
        AssertPathNoReparsePoints(updateRoot);
        AssertNotReparsePoint(operationsRoot);
        var operation = Path.Combine(operationsRoot, id.ToString("N"));
        Directory.CreateDirectory(operation);
        AssertNotReparsePoint(operation);
        var setupPath = Path.Combine(operation, SetupAssetName);
        try
        {
            SetState(new(AppUpdatePhase.Downloading, $"Downloading {release.Version}...", release));
            await DownloadExactAsync(release, setupPath, progress, cancellationToken).ConfigureAwait(false);
            SetState(new(AppUpdatePhase.Verifying, "Verifying the downloaded installer...", release));
            await Task.Run(() => AssertFile(setupPath, release.Setup), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The running executable path is unavailable.");
            var helperPath = Path.Combine(operation, Path.GetFileName(processPath));
            File.Copy(processPath, helperPath, overwrite: false);
            var prepared = new PreparedAppUpdate(id, release, operation, setupPath, helperPath, utcNow());
            await WriteAtomicJsonAsync(Path.Combine(operation, "operation.json"), prepared, cancellationToken).ConfigureAwait(false);
            preparedUpdate = prepared;
            SetState(new(AppUpdatePhase.Ready, "Update verified. Restart and install when you're ready.", release, prepared));
            return prepared;
        }
        catch (Exception ex)
        {
            TryDeleteCacheEntryWithRetries(operation);
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                SetState(new(AppUpdatePhase.Available, "Update download canceled. You can try again.", release));
            }
            else
            {
                SetState(new(AppUpdatePhase.DownloadFailed, $"Update download failed. Retry the download when you're ready. {ex.Message}", release));
            }
            throw;
        }
    }

    public async Task<AppUpdateLaunchResult> ApplyAndRestartAsync(PreparedAppUpdate update, CancellationToken cancellationToken = default)
    {
        using var lease = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ApplyAndRestartCore(update, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private AppUpdateLaunchResult ApplyAndRestartCore(PreparedAppUpdate update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (detectInstallKind() is not (AppInstallKind.Managed or AppInstallKind.LegacyManaged) ||
                lastVerifiedRelease is null || !ReleaseEquals(lastVerifiedRelease, update.Release) ||
                getCurrentVersion() is not { } current || update.Release.Version <= current)
                throw new InvalidOperationException("Check for a newer signed update before installing.");
            AssertPrepared(update);
        }
        catch (Exception ex)
        {
            SetState(new(AppUpdatePhase.Failed, $"Update verification failed. Check for updates to prepare it again. {ex.Message}", update.Release));
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var resultPath = Path.Combine(updateRoot, "results", update.OperationId.ToString("N") + ".json");
        var logPath = Path.Combine(updateRoot, "logs", update.OperationId.ToString("N") + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        AssertNotReparsePoint(Path.GetDirectoryName(resultPath)!);
        AssertNotReparsePoint(Path.GetDirectoryName(logPath)!);
        var info = new ProcessStartInfo(update.HelperPath) { UseShellExecute = true, WorkingDirectory = update.OperationDirectory };
        foreach (var argument in new[] { "--update-helper", update.OperationId.ToString("D"), "--parent-pid", Environment.ProcessId.ToString(), "--setup", update.SetupPath, "--setup-length", update.Release.Setup.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "--setup-sha256", update.Release.Setup.Sha256, "--target-version", update.Release.Version.ToString(3), "--install-dir", applicationDirectory, "--result", resultPath, "--log", logPath })
        {
            info.ArgumentList.Add(argument);
        }

        SetState(new(AppUpdatePhase.Launching, "Restarting to install the update...", update.Release, update));
        try
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException("The update helper did not start.");
            process.Dispose();
            return new AppUpdateLaunchResult(true, "Update helper started. The application will now close.", logPath);
        }
        catch (Exception ex)
        {
            SetState(new(AppUpdatePhase.Ready, $"Could not start the update helper. Try Restart and install again. {ex.Message}", update.Release, update));
            throw;
        }
    }

    public async Task<AppUpdateCompletion?> ConsumeCompletionAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ConsumeCompletionCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppUpdateCompletion?> ConsumeCompletionCoreAsync(CancellationToken cancellationToken)
    {
        var results = Path.Combine(updateRoot, "results");
        if (!Directory.Exists(results)) return null;
        AssertPathNoReparsePoints(results);
        foreach (var path in Directory.EnumerateFiles(results, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppUpdateCompletion completion;
            try
            {
                AssertNotReparsePoint(path);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("The update completion result has an invalid length.");
                completion = await JsonSerializer.DeserializeAsync<AppUpdateCompletion>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The update completion result is empty.");
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ||
                    completion.OperationId != id || id == Guid.Empty || !Enum.IsDefined(completion.Outcome) ||
                    string.IsNullOrWhiteSpace(completion.Message))
                    throw new InvalidDataException("The update completion result does not match its operation.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.Write(AppLogLevel.Warning, "Updater", "Discarding an invalid update completion result.", ex);
                TryDeleteCacheEntryWithRetries(path);
                continue;
            }
            DeleteCacheEntry(path);
            CleanupCompletedOperation(completion);
            SetState(new(completion.Outcome == AppUpdateCompletionOutcome.Failed ? AppUpdatePhase.Failed : AppUpdatePhase.Completed, completion.Message));
            return completion;
        }
        return null;
    }

    public void Dispose()
    {
        operationGate.Dispose();
        if (disposeClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task<VerifiedReleaseEnvelope> FetchReleaseEnvelopeAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri);
        var githubBytes = await ReadBoundedContentAsync(response.Content, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var github = JsonSerializer.Deserialize<GitHubRelease>(githubBytes, JsonOptions)
            ?? throw new InvalidDataException("GitHub returned an empty release.");
        if (github.Draft || github.Prerelease || !TryParseStableTag(github.Tag, out _))
            throw new InvalidDataException("The latest release is not a final semantic-version release.");
        var manifestAsset = SelectAsset(github, UpdateManifestName);
        var signatureAsset = SelectAsset(github, UpdateSignatureName);
        var manifestBytes = await DownloadBytesAsync(manifestAsset, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var signature = await DownloadBytesAsync(signatureAsset, 16 * 1024, cancellationToken).ConfigureAwait(false);
        return new VerifiedReleaseEnvelope(github, manifestBytes, signature, utcNow());
    }

    private AppUpdateRelease VerifyReleaseEnvelope(VerifiedReleaseEnvelope envelope)
    {
        var github = envelope.Release;
        if (github is null || github.Assets is null || github.Draft || github.Prerelease || !TryParseStableTag(github.Tag, out _))
        {
            throw new InvalidDataException("The cached release is not a final semantic-version release.");
        }

        var manifestAsset = SelectAsset(github, UpdateManifestName);
        var signatureAsset = SelectAsset(github, UpdateSignatureName);
        if (envelope.Manifest is null || envelope.Signature is null ||
            envelope.Manifest.Length != manifestAsset.Size ||
            envelope.Signature.Length != signatureAsset.Size)
        {
            throw new InvalidDataException("The cached signed-manifest lengths do not match release metadata.");
        }
        using var rsa = RSA.Create();
        rsa.ImportParameters(trustedKey);
        if (!rsa.VerifyData(envelope.Manifest, envelope.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The update manifest signature is invalid.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(envelope.Manifest, JsonOptions)
            ?? throw new InvalidDataException("The signed update manifest is empty.");
        return MaterializeManifest(manifest, github);
    }

    private AppUpdateRelease MaterializeManifest(UpdateManifest manifest, GitHubRelease github)
    {
        if (manifest.SchemaVersion != 1 || manifest.ProtocolVersion is < 1 or > SupportedProtocolVersion ||
            !string.Equals(manifest.Channel, "stable", StringComparison.Ordinal) || manifest.Prerelease ||
            !string.Equals(manifest.KeyId, trustedKeyId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Repository, Repository, StringComparison.Ordinal) ||
            !string.Equals(manifest.Tag, github.Tag, StringComparison.Ordinal) ||
            !TryParseStableTag(manifest.Tag, out var version) || version.ToString(3) != manifest.Version ||
            string.IsNullOrWhiteSpace(manifest.Commit) || manifest.Commit.Length != 40 || !manifest.Commit.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(manifest.ReleasePage, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(page.AbsoluteUri, $"https://github.com/{Repository}/releases/tag/{manifest.Tag}", StringComparison.Ordinal))
            throw new InvalidDataException("The signed update manifest metadata is inconsistent.");
        if (manifest.DependencyMinimums is null || manifest.DependencyMinimums.Count == 0 ||
            manifest.DependencyMinimums.Any(entry => string.IsNullOrWhiteSpace(entry.Key) ||
                !Regex.IsMatch(entry.Value ?? "", "^\\d+(?:\\.\\d+){1,3}(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("The signed dependency minimums are invalid.");
        var setup = ManifestAsset(manifest.Setup, SelectAsset(github, SetupAssetName));
        var zip = ManifestAsset(manifest.Zip, SelectAsset(github, ZipAssetName));
        return new(version, manifest.Tag, manifest.Commit, manifest.Repository, page, manifest.ProtocolVersion, setup, zip, manifest.DependencyMinimums ?? new Dictionary<string, string>());
    }

    private static AppUpdateAsset ManifestAsset(UpdateAsset? value, GitHubAsset github)
    {
        if (value is null || !string.Equals(value.Name, github.Name, StringComparison.Ordinal) || value.Length != github.Size ||
            value.Length <= 0 || value.Length > MaximumPackageBytes || !IsSha256(value.Sha256) ||
            !Uri.TryCreate(github.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Signed metadata for {github.Name} is invalid.");
        return new(value.Name, uri, value.Length, value.Sha256.ToLowerInvariant());
    }

    private async Task DownloadExactAsync(AppUpdateRelease release, string destination, IProgress<AppUpdateProgress>? progress, CancellationToken token)
    {
        var asset = release.Setup;
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, token, PackageDownloadTimeout).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri);
        if (response.Content.Headers.ContentLength is { } length && length != asset.Length) throw new InvalidDataException("Installer Content-Length does not match its signed length.");
        await AtomicFile.WriteAsync(destination, async (output, cancellationToken) =>
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var buffer = new byte[128 * 1024]; long total = 0;
            var progressClock = Stopwatch.StartNew();
            while (true)
            {
                int read;
                // Reset the idle budget after each read so a slow, progressing transfer
                // can use the full package timeout without leaving a stalled one waiting.
                readDeadline.CancelAfter(downloadIdleTimeout);
                try
                {
                    read = await input.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (readDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The update download stopped receiving data. Check your connection and retry.", ex);
                }
                finally
                {
                    readDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                if (read == 0) break;
                total += read; if (total > asset.Length) throw new InvalidDataException("Installer exceeds its signed length.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (total == asset.Length || progressClock.ElapsedMilliseconds >= 200)
                {
                    var update = new AppUpdateProgress(total, asset.Length);
                    progress?.Report(update);
                    SetState(new(AppUpdatePhase.Downloading, $"Downloading {update.Percentage:0}%…", release, Progress: update));
                    progressClock.Restart();
                }
            }
            if (total != asset.Length) throw new InvalidDataException("Installer does not match its signed length.");
        }, token).ConfigureAwait(false);
    }

    private static void AssertFile(string path, AppUpdateAsset asset)
    {
        var info = new FileInfo(path); if (!info.Exists || info.Length != asset.Length) throw new InvalidDataException("Staged installer length mismatch.");
        using var stream = info.OpenRead();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Staged installer hash mismatch.");
    }

    private void AssertPrepared(PreparedAppUpdate update, bool verifyHelper = true)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The running executable path is unavailable.");
        var operation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(update.OperationDirectory));
        var operationsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(updateRoot, "operations")));
        if (!IsSamePath(operation, Path.Combine(operationsRoot, update.OperationId.ToString("N"))) ||
            !string.Equals(Path.GetFileName(operation), update.OperationId.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(update.SetupPath)), operation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(update.SetupPath), SetupAssetName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(update.HelperPath)), operation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(update.HelperPath), Path.GetFileName(processPath), StringComparison.OrdinalIgnoreCase) ||
            (verifyHelper && !File.Exists(update.HelperPath))) throw new InvalidDataException("The prepared update paths are invalid.");
        AssertPathNoReparsePoints(operation);
        AssertNotReparsePoint(update.SetupPath);
        if (File.Exists(update.HelperPath)) AssertNotReparsePoint(update.HelperPath);
        AssertFile(update.SetupPath, update.Release.Setup);
        if (verifyHelper)
        {
            using var original = File.OpenRead(processPath);
            using var helper = File.OpenRead(update.HelperPath);
            if (original.Length != helper.Length || !SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(helper)))
                throw new CryptographicException("The staged update helper changed before launch. Check for updates to prepare it again.");
        }
    }

    private async Task<PreparedAppUpdate?> RestorePreparedAsync(AppUpdateRelease release, CancellationToken token)
    {
        var operationsRoot = Path.Combine(updateRoot, "operations");
        if (!Directory.Exists(operationsRoot)) return null;
        AssertPathNoReparsePoints(operationsRoot);
        foreach (var operation in Directory.EnumerateDirectories(operationsRoot).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            token.ThrowIfCancellationRequested();
            var metadataPath = Path.Combine(operation, "operation.json");
            if (!Guid.TryParseExact(Path.GetFileName(operation), "N", out var id) || !File.Exists(metadataPath)) continue;
            try
            {
                AssertPathNoReparsePoints(operation);
                AssertNotReparsePoint(metadataPath);
                await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > 64 * 1024) continue;
                var saved = await JsonSerializer.DeserializeAsync<PreparedAppUpdate>(stream, JsonOptions, token).ConfigureAwait(false);
                if (saved?.Release is null || saved.OperationId != id || !ReleaseEquals(saved.Release, release) ||
                    !IsSamePath(saved.OperationDirectory, operation) || saved.VerifiedAt > utcNow() ||
                    utcNow() - saved.VerifiedAt > TimeSpan.FromDays(7)) continue;
                // Local metadata is not a trust root: bind it to the signed check and rehash the installer.
                var restored = saved with { Release = release };
                await Task.Run(() => AssertPrepared(restored, verifyHelper: false), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The running executable path is unavailable.");
                var helperPath = Path.Combine(operation, Path.GetFileName(processPath));
                if (File.Exists(helperPath)) AssertNotReparsePoint(helperPath);
                File.Copy(processPath, helperPath, overwrite: true);
                preparedUpdate = restored with { HelperPath = helperPath };
                return preparedUpdate;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException)
            {
                logger.Write(AppLogLevel.Warning, "Updater", "Ignoring an unusable staged update.", ex);
            }
        }
        preparedUpdate = null;
        return null;
    }

    private async Task<AppUpdateRelease?> ReadFreshCheckAsync(CancellationToken token)
    {
        var path = Path.Combine(updateRoot, "last-check.json");
        if (!File.Exists(path)) return null;
        try
        {
            AssertNotReparsePoint(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 2 * 1024 * 1024) return null;
            var envelope = await JsonSerializer.DeserializeAsync<VerifiedReleaseEnvelope>(stream, JsonOptions, token).ConfigureAwait(false);
            if (envelope is null || envelope.CheckedAt > utcNow() || utcNow() - envelope.CheckedAt >= CheckLifetime) return null;
            return VerifyReleaseEnvelope(envelope);
        }
        catch (Exception ex) when (ex is JsonException or IOException or CryptographicException or InvalidDataException)
        {
            logger.Write(AppLogLevel.Warning, "Updater", "Discarding an invalid cached update check.", ex);
            return null;
        }
    }

    private static void ValidateRelease(AppUpdateRelease release)
    {
        if (release.ProtocolVersion != SupportedProtocolVersion || release.Tag != "v" + release.Version.ToString(3) || release.Setup.Name != SetupAssetName) throw new InvalidDataException("The update release is unsupported.");
    }

    private static bool ReleaseEquals(AppUpdateRelease left, AppUpdateRelease right) =>
        left.Version == right.Version &&
        string.Equals(left.Tag, right.Tag, StringComparison.Ordinal) &&
        string.Equals(left.Commit, right.Commit, StringComparison.Ordinal) &&
        left.ProtocolVersion == right.ProtocolVersion &&
        left.Setup == right.Setup;

    private void CleanupCache()
    {
        CleanupOperations(Path.Combine(updateRoot, "operations"));
        CleanupDirectory(Path.Combine(updateRoot, "results"), TimeSpan.FromDays(30));
        CleanupDirectory(Path.Combine(updateRoot, "logs"), TimeSpan.FromDays(30));
    }

    private void CleanupCompletedOperation(AppUpdateCompletion completion)
    {
        // Cancellation (including UAC) and failed installs can be retried using the
        // staged bytes. RestorePreparedAsync still binds them to signed metadata and
        // rehashes them before offering Ready; normal cache expiration also applies.
        if (completion.Outcome is AppUpdateCompletionOutcome.Canceled or AppUpdateCompletionOutcome.Failed) return;
        if (preparedUpdate?.OperationId == completion.OperationId) preparedUpdate = null;
        var operationsRoot = Path.Combine(updateRoot, "operations");
        AssertPathNoReparsePoints(operationsRoot);
        var operation = Path.Combine(updateRoot, "operations", completion.OperationId.ToString("N"));
        if (!Directory.Exists(operation)) return;

        if (TryDeleteCacheEntryWithRetries(operation))
        {
            logger.Write(AppLogLevel.Debug, "Updater", $"Removed completed update operation: {operation}");
            return;
        }

        TryExpireCacheEntry(operation);
    }

    private void CleanupOperations(string path)
    {
        if (!Directory.Exists(path)) return;
        AssertPathNoReparsePoints(path);
        var entries = new List<(string Path, DateTimeOffset Modified, long Length)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            try
            {
                if (preparedUpdate is { } prepared && IsSamePath(prepared.OperationDirectory, entry)) continue;
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(entry), TimeSpan.Zero);
                var verified = File.Exists(Path.Combine(entry, "operation.json"));
                var maximumAge = verified ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
                if (utcNow() - modified > maximumAge)
                {
                    DeleteCacheEntry(entry);
                    continue;
                }
                entries.Add((entry, modified, GetCacheEntryLength(entry)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Write(AppLogLevel.Debug, "Updater", $"Could not inspect update operation: {entry}", ex);
            }
        }

        var total = entries.Sum(entry => entry.Length);
        foreach (var entry in entries.OrderBy(entry => entry.Modified))
        {
            if (total <= MaximumPackageBytes) break;
            if (preparedUpdate is { } active && IsSamePath(active.OperationDirectory, entry.Path)) continue;
            try
            {
                DeleteCacheEntry(entry.Path);
                total -= entry.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Write(AppLogLevel.Debug, "Updater", $"Could not trim update operation: {entry.Path}", ex);
            }
        }
    }

    private bool TryDeleteCacheEntryWithRetries(string path)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                DeleteCacheEntry(path);
                return !FileOrDirectoryExists(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
                if (attempt < 5)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
                }
            }
        }

        logger.Write(AppLogLevel.Debug, "Updater", $"Could not remove completed update operation: {path}", lastFailure);
        return false;
    }

    private void TryExpireCacheEntry(string path)
    {
        try
        {
            if (FileOrDirectoryExists(path))
            {
                File.SetLastWriteTimeUtc(path, utcNow().UtcDateTime - TimeSpan.FromDays(8));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Write(AppLogLevel.Debug, "Updater", $"Could not mark completed update operation for later cleanup: {path}", ex);
        }
    }

    private void CleanupDirectory(string path, TimeSpan age)
    {
        if (!Directory.Exists(path)) return;
        AssertPathNoReparsePoints(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            try { if (utcNow() - File.GetLastWriteTimeUtc(entry) > age) DeleteCacheEntry(entry); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.Write(AppLogLevel.Debug, "Updater", $"Could not remove stale update cache item: {entry}", ex); }
        }
    }

    private void DeleteCacheEntry(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = Path.TrimEndingDirectorySeparator(updateRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Refusing to remove a path outside the update cache.");
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            return;
        }
        if (!Directory.Exists(fullPath)) return;
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(fullPath);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(fullPath)) DeleteCacheEntry(child);
        Directory.Delete(fullPath);
    }

    private static long GetCacheEntryLength(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return 0;
        long total = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            total = checked(total + GetCacheEntryLength(child));
        }
        return total;
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool FileOrDirectoryExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static void AssertNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Update cache paths cannot be symbolic links or junctions: {path}");
        }
    }

    private static void AssertPathNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists) AssertNotReparsePoint(current.FullName);
            current = current.Parent;
        }
    }

    private static AppInstallKind DetectInstallKind(string directory)
    {
        if (OperatingSystem.IsWindows() && EnumerateRelatedProducts(AppUpgradeCode).Any(product => ProductMatchesDirectory(product, directory))) return AppInstallKind.Managed;
        if (File.Exists(Path.Combine(directory, AppIdentity.OwnerFileName)) ||
            File.Exists(Path.Combine(directory, AppIdentity.LegacyOwnerFileName))) return AppInstallKind.Zip;
        return AppInstallKind.Unmanaged;
    }

    private static IEnumerable<string> EnumerateRelatedProducts(string upgradeCode)
    {
        for (uint index = 0; ; index++)
        {
            var product = new System.Text.StringBuilder(39);
            var result = MsiEnumRelatedProducts(upgradeCode, 0, index, product);
            if (result == 259) yield break;
            if (result != 0) yield break;
            yield return product.ToString();
        }
    }

    private static bool ProductMatchesDirectory(string product, string directory)
    {
        var buffer = new System.Text.StringBuilder(1024); uint size = (uint)buffer.Capacity;
        return MsiGetProductInfo(product, "InstallLocation", buffer, ref size) == 0 &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(buffer.ToString())), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);
    }

    private static Version? GetCurrentVersion()
    {
        var text = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion;
        return Version.TryParse(text?.Split('+')[0], out var version) ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
    }

    private static bool TryParseStableTag(string? tag, out Version version)
    {
        version = null!;
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var parsed) ||
            parsed.Revision >= 0 || parsed.Build < 0 ||
            !string.Equals(tag, "v" + parsed.ToString(3), StringComparison.Ordinal))
        {
            return false;
        }
        version = parsed;
        return true;
    }
    private static bool IsSha256(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string ComputeKeyId(RSAParameters key)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(key);
        return Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }
    private static GitHubAsset SelectAsset(GitHubRelease release, string name)
    {
        if (release.Assets is null || release.Assets.Any(asset => asset is null))
            throw new InvalidDataException("The release contains an invalid asset list.");
        return release.Assets.Where(asset => asset.Name == name).ToArray() is [var item]
            ? item
            : throw new InvalidDataException($"Release must contain exactly one {name}.");
    }
    private static void EnsureHttps(Uri? uri) { if (uri?.IsAbsoluteUri != true || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Update downloads and redirects must use HTTPS."); }
    private async Task<byte[]> DownloadBytesAsync(GitHubAsset asset, int maximum, CancellationToken token)
    {
        if (asset.Size <= 0 || asset.Size > maximum) throw new InvalidDataException($"{asset.Name} has an invalid size.");
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)) throw new InvalidDataException($"{asset.Name} has an invalid URL.");
        EnsureHttps(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, token).ConfigureAwait(false); response.EnsureSuccessStatusCode(); EnsureHttps(response.RequestMessage?.RequestUri);
        var bytes = await ReadBoundedContentAsync(response.Content, asset.Size, token).ConfigureAwait(false); if (bytes.Length != asset.Size) throw new InvalidDataException($"{asset.Name} length mismatch."); return bytes;
    }

    private static async Task<byte[]> ReadBoundedContentAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (maximum <= 0 || content.Headers.ContentLength is { } declared && (declared < 0 || declared > maximum))
            throw new InvalidDataException("An update response declared an invalid length.");
        try
        {
            return await BoundedByteReader.ReadOrThrowAsync(content, maximum, token).ConfigureAwait(false);
        }
        catch (PayloadTooLargeException ex)
        {
            throw new InvalidDataException("An update response exceeded its size limit.", ex);
        }
    }

    private static Task WriteAtomicJsonAsync<T>(string path, T value, CancellationToken token) =>
        AtomicFile.WriteAsync(
            path,
            (stream, cancellationToken) => JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken),
            token);

    private void SetState(AppUpdateState value) { state = value; StateChanged?.Invoke(this, new(value)); }
    private static string GetUpdateRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.UpdateDirectoryName, "Updates");

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, System.Text.StringBuilder productCode);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetProductInfo(string product, string property, System.Text.StringBuilder value, ref uint size);

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("draft")] bool Draft, [property: JsonPropertyName("prerelease")] bool Prerelease, [property: JsonPropertyName("assets")] GitHubAsset[] Assets);
    private sealed record GitHubAsset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("size")] int Size, [property: JsonPropertyName("browser_download_url")] string Url);
    private sealed record VerifiedReleaseEnvelope(GitHubRelease Release, byte[] Manifest, byte[] Signature, DateTimeOffset CheckedAt);
    private sealed record UpdateManifest(int SchemaVersion, int ProtocolVersion, string Channel, bool Prerelease, string Version, string Tag, string Commit, string Repository, string ReleasePage, string KeyId, Dictionary<string, string>? DependencyMinimums, UpdateAsset Setup, UpdateAsset Zip);
    private sealed record UpdateAsset(string Name, long Length, string Sha256);
}
