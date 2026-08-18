using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Updates;

public sealed class GitHubReleaseAppUpdateService : IAppUpdateService, IDisposable
{
    private const long MaximumInstallerBytes = 512L * 1024L * 1024L;
    private const long MaximumChecksumBytes = 1024L * 1024L;
    private const string DefaultRepository = "CorontoSiete/streamlink-vlc-studio";
    private const string MsiAssetName = "StreamlinkVlcStudio-Setup.msi";
    private const string ChecksumAssetName = "SHA256SUMS.txt";
    private const string ProductDisplayName = "Streamlink VLC Studio";
    private const string UninstallRegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly Regex ChecksumLinePattern = new(
        "^(?<hash>[0-9a-fA-F]{64}) \\*(?<name>[^\\\\/]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseOrdinalTagPattern = new(
        "^release-(?<ordinal>\\d+)-run-",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SemanticVersionPattern = new(
        "(?<!\\d)(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<patch>\\d+)(?:\\.\\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly string repository;
    private readonly string applicationDirectory;
    private readonly string temporaryRoot;
    private readonly Func<ProcessStartInfo, bool> startProcess;
    private readonly Func<string?> getCurrentInstalledVersion;
    private readonly bool disposeHttpClient;

    public GitHubReleaseAppUpdateService(IAppLogger logger)
        : this(
            logger,
            new HttpClient(),
            DefaultRepository,
            GetDefaultApplicationDirectory(),
            GetDefaultTemporaryRoot(),
            StartProcessCore,
            disposeHttpClient: true)
    {
    }

    internal GitHubReleaseAppUpdateService(
        IAppLogger logger,
        HttpClient httpClient,
        string repository,
        string applicationDirectory,
        string temporaryRoot,
        Func<ProcessStartInfo, bool> startProcess,
        bool disposeHttpClient = false,
        Func<string?>? getCurrentInstalledVersion = null)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.repository = NormalizeRepository(repository);
        this.applicationDirectory = Path.GetFullPath(applicationDirectory);
        this.temporaryRoot = Path.GetFullPath(temporaryRoot);
        this.startProcess = startProcess;
        this.getCurrentInstalledVersion = getCurrentInstalledVersion ?? GetCurrentInstalledVersion;
        this.disposeHttpClient = disposeHttpClient;

        if (!this.httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"StreamlinkVlcStudioUpdater/1.0 (+https://github.com/{this.repository})");
        }
    }

    public async Task<AppUpdateStartResult> StartLatestReleaseUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        var release = await GetLatestFinalReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (TryGetCurrentVersionStatus(release, out var upToDateMessage))
        {
            return new AppUpdateStartResult(upToDateMessage, RequestApplicationShutdown: false);
        }

        var installerScript = Path.Combine(applicationDirectory, "install.ps1");
        if (File.Exists(installerScript))
        {
            StartPowerShellUpdater(installerScript);
            return new AppUpdateStartResult(
                "Updater started from the bundled installer script. The app will close while it updates.",
                RequestApplicationShutdown: true);
        }

        var launchPath = await DownloadVerifiedLatestMsiAsync(release, cancellationToken).ConfigureAwait(false);
        StartMsiInstaller(launchPath);
        return new AppUpdateStartResult(
            "Updater downloaded and verified the latest MSI package. The app will close while Windows Installer starts.",
            RequestApplicationShutdown: true);
    }

    public void Dispose()
    {
        if (disposeHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private void StartPowerShellUpdater(string installerScript)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerScript) ?? applicationDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(installerScript);
        startInfo.ArgumentList.Add("-InstallDir");
        startInfo.ArgumentList.Add(applicationDirectory);
        startInfo.ArgumentList.Add("-GitHubRepository");
        startInfo.ArgumentList.Add(repository);
        startInfo.ArgumentList.Add("-AppSource");
        startInfo.ArgumentList.Add("GitHub");
        startInfo.ArgumentList.Add("-ForceStopApp");
        startInfo.ArgumentList.Add("-Launch");
        StartOrThrow(startInfo, "PowerShell updater");
        logger.Write(AppLogLevel.Info, "Updater", $"Started bundled installer script: {installerScript}");
    }

    private void StartMsiInstaller(string installerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? temporaryRoot
        };
        StartOrThrow(startInfo, "MSI installer");
        logger.Write(AppLogLevel.Info, "Updater", $"Started verified MSI installer: {installerPath}");
    }

    private void StartOrThrow(ProcessStartInfo startInfo, string description)
    {
        try
        {
            if (!startProcess(startInfo))
            {
                throw new InvalidOperationException($"Could not start the {description}.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start the {description}: {ex.Message}", ex);
        }
    }

    private async Task<string> DownloadVerifiedLatestMsiAsync(
        GitHubRelease release,
        CancellationToken cancellationToken)
    {
        var msiAsset = SelectAsset(release, MsiAssetName);
        var checksumAsset = SelectAsset(release, ChecksumAssetName);
        var updateDirectory = Path.Combine(temporaryRoot, "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);

        var msiPath = Path.Combine(updateDirectory, msiAsset.Name);
        var checksumPath = Path.Combine(updateDirectory, checksumAsset.Name);
        await DownloadAssetAsync(msiAsset, msiPath, MaximumInstallerBytes, cancellationToken).ConfigureAwait(false);
        await DownloadAssetAsync(checksumAsset, checksumPath, MaximumChecksumBytes, cancellationToken).ConfigureAwait(false);
        AssertChecksum(msiPath, ReadChecksumManifest(checksumPath));
        return msiPath;
    }

    private async Task<GitHubRelease> GetLatestFinalReleaseAsync(CancellationToken cancellationToken)
    {
        var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (release.Draft || release.Prerelease)
        {
            throw new InvalidOperationException(
                $"Latest GitHub release is not a final published release: {release.TagName}");
        }

        return release;
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.github.com/repos/{repository}/releases/latest");
        using var response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub release request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName) || release.Assets is null)
        {
            throw new InvalidOperationException("GitHub latest release response was incomplete.");
        }

        return release;
    }

    private bool TryGetCurrentVersionStatus(GitHubRelease release, out string message)
    {
        message = string.Empty;
        var currentVersionText = getCurrentInstalledVersion();
        if (!TryParseAppVersion(currentVersionText, out var currentVersion) ||
            !TryGetReleaseVersion(release.TagName, out var latestVersion))
        {
            logger.Write(
                AppLogLevel.Debug,
                "Updater",
                $"Could not compare installed version '{currentVersionText ?? "(unknown)"}' with GitHub release '{release.TagName}'.");
            return false;
        }

        if (currentVersion.CompareTo(latestVersion) < 0)
        {
            return false;
        }

        message = $"You're on the latest version ({currentVersion}).";
        return true;
    }

    private string? GetCurrentInstalledVersion()
    {
        return FirstNonEmpty(
            GetCurrentInstalledVersionFromRegistry(),
            GetCurrentExecutableVersion());
    }

    private string? GetCurrentInstalledVersionFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var fallbackVersions = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(UninstallRegistrySubKey);
                    if (uninstallKey is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey is null ||
                            !string.Equals(
                                ReadRegistryString(appKey, "DisplayName"),
                                ProductDisplayName,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var displayVersion = ReadRegistryString(appKey, "DisplayVersion");
                        if (string.IsNullOrWhiteSpace(displayVersion))
                        {
                            continue;
                        }

                        if (IsSameDirectory(ReadRegistryString(appKey, "InstallLocation"), applicationDirectory))
                        {
                            return displayVersion.Trim();
                        }

                        fallbackVersions.Add(displayVersion.Trim());
                    }
                }
                catch (Exception ex) when (IsRegistryReadException(ex))
                {
                    logger.Write(
                        AppLogLevel.Debug,
                        "Updater",
                        $"Could not read installed app version from {hive}/{view}.",
                        ex);
                }
            }
        }

        return fallbackVersions.FirstOrDefault();
    }

    private string? GetCurrentExecutableVersion()
    {
        var processPath = Environment.ProcessPath;
        var candidates = new[]
            {
                Path.Combine(applicationDirectory, "StreamlinkVlcStudio.exe"),
                string.IsNullOrWhiteSpace(processPath) ? null : processPath
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(candidate);
                var version = FirstNonEmpty(versionInfo.ProductVersion, versionInfo.FileVersion);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Write(AppLogLevel.Debug, "Updater", $"Could not read app version from {candidate}.", ex);
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryString(RegistryKey key, string valueName)
    {
        return key.GetValue(valueName) as string;
    }

    private static bool IsRegistryReadException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    }

    private static bool IsSameDirectory(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                NormalizeDirectoryPath(left),
                NormalizeDirectoryPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        var rootLength = Path.GetPathRoot(fullPath)?.Length ?? 0;
        while (fullPath.Length > rootLength &&
               (fullPath[^1] == Path.DirectorySeparatorChar || fullPath[^1] == Path.AltDirectorySeparatorChar))
        {
            fullPath = fullPath[..^1];
        }

        return fullPath;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool TryGetReleaseVersion(string? tagName, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        var normalizedTag = tagName.Trim();
        var ordinalMatch = ReleaseOrdinalTagPattern.Match(normalizedTag);
        if (ordinalMatch.Success &&
            long.TryParse(
                ordinalMatch.Groups["ordinal"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ordinal))
        {
            return TryCreateMsiProductVersion(ordinal, out version);
        }

        return TryParseAppVersion(normalizedTag, out version);
    }

    private static bool TryParseAppVersion(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = SemanticVersionPattern.Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new AppVersion(major, minor, patch);
        return true;
    }

    private static bool TryCreateMsiProductVersion(long ordinal, out AppVersion version)
    {
        version = default;
        if (ordinal < 0)
        {
            return false;
        }

        var major = 1 + ordinal / 65536;
        if (major is < 1 or > 255)
        {
            return false;
        }

        version = new AppVersion(
            (int)major,
            (int)((ordinal % 65536) / 256),
            (int)(ordinal % 256));
        return true;
    }

    private static GitHubReleaseAsset SelectAsset(GitHubRelease release, string name)
    {
        var matches = release.Assets
            .Where(asset => string.Equals(asset.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            var available = release.Assets.Length == 0
                ? "(none)"
                : string.Join(", ", release.Assets.Select(asset => asset.Name));
            throw new InvalidOperationException(
                $"GitHub release {release.TagName} must contain exactly one {name} asset. Available assets: {available}");
        }

        var match = matches[0];
        if (!IsSafeAssetFileName(match.Name) ||
            match.Size <= 0 ||
            string.IsNullOrWhiteSpace(match.BrowserDownloadUrl))
        {
            throw new InvalidOperationException($"GitHub release asset metadata is incomplete for {name}.");
        }

        return match;
    }

    private async Task DownloadAssetAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"GitHub release asset URL must be absolute HTTPS: {asset.Name}");
        }

        if (asset.Size > maximumBytes)
        {
            throw new InvalidOperationException(
                $"GitHub release asset {asset.Name} exceeds the {maximumBytes}-byte limit.");
        }

        using var response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (finalUri is null ||
            !finalUri.IsAbsoluteUri ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"GitHub release asset {asset.Name} redirected to a non-HTTPS URL.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub release asset {asset.Name} download failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        if (response.Content.Headers.ContentLength is { } contentLength &&
            (contentLength <= 0 || contentLength > maximumBytes))
        {
            throw new InvalidOperationException(
                $"GitHub release asset {asset.Name} declared an invalid download length.");
        }

        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            var buffer = new byte[81920];
            long totalBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > maximumBytes)
                {
                    throw new InvalidOperationException(
                        $"GitHub release asset {asset.Name} exceeds the {maximumBytes}-byte limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (totalBytes != asset.Size)
            {
                throw new InvalidOperationException(
                    $"GitHub release asset {asset.Name} size mismatch. Expected {asset.Size} bytes, found {totalBytes}.");
            }

            output.Close();
            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static Dictionary<string, string> ReadChecksumManifest(string path)
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = ChecksumLinePattern.Match(line);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Malformed release checksum manifest line: {line}");
            }

            var name = match.Groups["name"].Value;
            if (!checksums.TryAdd(name, match.Groups["hash"].Value.ToLowerInvariant()))
            {
                throw new InvalidOperationException($"Duplicate release checksum entry: {name}");
            }
        }

        if (checksums.Count == 0)
        {
            throw new InvalidOperationException("Release checksum manifest is empty.");
        }

        return checksums;
    }

    private static void AssertChecksum(string path, IReadOnlyDictionary<string, string> checksums)
    {
        var name = Path.GetFileName(path);
        if (!checksums.TryGetValue(name, out var expectedHash))
        {
            throw new InvalidOperationException($"Release checksum manifest does not contain {name}.");
        }

        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release checksum mismatch for {name}. Expected {expectedHash}, found {actualHash}.");
        }
    }

    private static string NormalizeRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            !Regex.IsMatch(repository, "^[^/\\s]+/[^/\\s]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("GitHub repository must be in owner/name form.", nameof(repository));
        }

        return repository;
    }

    private static bool IsSafeAssetFileName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) &&
            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static string GetDefaultApplicationDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                return processDirectory;
            }
        }

        return AppContext.BaseDirectory;
    }

    private static string GetDefaultTemporaryRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, "StreamlinkVlcStudio", "Updates");
    }

    private static bool StartProcessCore(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] GitHubReleaseAsset[] Assets);

    private sealed record GitHubReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

    private readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
    {
        public int CompareTo(AppVersion other)
        {
            var majorComparison = Major.CompareTo(other.Major);
            if (majorComparison != 0)
            {
                return majorComparison;
            }

            var minorComparison = Minor.CompareTo(other.Minor);
            if (minorComparison != 0)
            {
                return minorComparison;
            }

            return Patch.CompareTo(other.Patch);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch);
        }
    }
}
