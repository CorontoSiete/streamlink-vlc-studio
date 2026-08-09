using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public JsonSettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamlinkVlcStudio",
            "settings.json");
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false)
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            MoveInvalidSettingsFileAside(SettingsPath);
            return new AppSettings();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var targetDirectory = string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory;
            var tempPath = Path.Combine(
                targetDirectory,
                $"{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(SettingsPath))
                {
                    try
                    {
                        File.Replace(tempPath, SettingsPath, null, ignoreMetadataErrors: true);
                    }
                    catch (FileNotFoundException)
                    {
                        // The settings file vanished between the existence check and the replace.
                        File.Move(tempPath, SettingsPath);
                    }
                }
                else
                {
                    File.Move(tempPath, SettingsPath);
                }
            }
            catch
            {
                TryDeleteTempFile(tempPath);
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void MoveInvalidSettingsFileAside(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        var targetDirectory = string.IsNullOrWhiteSpace(directory)
            ? Directory.GetCurrentDirectory()
            : directory;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(
            targetDirectory,
            $"{Path.GetFileName(settingsPath)}.invalid-{timestamp}-{Guid.NewGuid():N}");

        File.Move(settingsPath, backupPath);
    }
}
