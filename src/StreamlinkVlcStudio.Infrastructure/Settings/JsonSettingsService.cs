using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private const int MaximumSettingsBytes = 4 * 1024 * 1024;
    private const string ProtectedSecretsProperty = "ProtectedSecrets";
    private static readonly string[] SecretPropertyNames =
    [
        nameof(ChatSettings.TwitchOAuthToken),
        nameof(ChatSettings.KickOAuthToken),
        nameof(ChatSettings.KickRefreshToken),
        nameof(ChatSettings.KickClientSecret)
    ];
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

    public string? LastLoadWarning { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastLoadWarning = null;
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var root = await ReadSettingsRootAsync(cancellationToken).ConfigureAwait(false);
            var legacySecrets = ReadLegacySecrets(root, out var hadLegacySecrets);
            RemoveSecretProperties(root);

            SettingsSecrets secrets;
            var hasProtectedSecrets = RemovePropertyCaseInsensitive(
                root,
                ProtectedSecretsProperty,
                out var protectedSecretsNode);
            var protectedSecretsWereCorrupt = false;
            if (hasProtectedSecrets)
            {
                try
                {
                    var envelope = protectedSecretsNode?.Deserialize<ProtectedSecretsEnvelope>(SerializerOptions)
                        ?? throw new CryptographicException("The protected settings envelope was empty.");
                    secrets = SettingsSecretProtector.Unprotect(envelope);
                }
                catch (Exception ex) when (ex is CryptographicException or JsonException or PlatformNotSupportedException)
                {
                    protectedSecretsWereCorrupt = true;
                    secrets = new SettingsSecrets();
                    var backupPath = BackupCorruptProtectedSettings(SettingsPath);
                    LastLoadWarning = backupPath is null
                        ? "Saved account secrets could not be decrypted and were cleared. Reconnect Twitch and Kick in Settings."
                        : $"Saved account secrets could not be decrypted and were cleared. Reconnect Twitch and Kick in Settings. A backup was preserved at {backupPath}.";
                }
            }
            else
            {
                secrets = legacySecrets;
            }

            var settings = root.Deserialize<AppSettings>(SerializerOptions) ?? new AppSettings();
            ApplySecrets(settings.Chat, secrets);

            if (hadLegacySecrets || protectedSecretsWereCorrupt)
            {
                await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or PayloadTooLargeException)
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
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<JsonObject> ReadSettingsRootAsync(CancellationToken cancellationToken)
    {
        var file = new FileInfo(SettingsPath);
        if (!file.Exists || file.Length > MaximumSettingsBytes)
        {
            throw new PayloadTooLargeException(MaximumSettingsBytes);
        }

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await BoundedByteReader
            .ReadOrThrowAsync(stream, MaximumSettingsBytes, cancellationToken)
            .ConfigureAwait(false);
        return JsonNode.Parse(bytes) as JsonObject
            ?? throw new JsonException("Settings root must be a JSON object.");
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var root = JsonSerializer.SerializeToNode(settings, SerializerOptions) as JsonObject
            ?? throw new JsonException("Settings could not be serialized as a JSON object.");
        RemoveSecretProperties(root);
        var envelope = SettingsSecretProtector.Protect(new SettingsSecrets
        {
            TwitchOAuthToken = settings.Chat.TwitchOAuthToken,
            KickOAuthToken = settings.Chat.KickOAuthToken,
            KickRefreshToken = settings.Chat.KickRefreshToken,
            KickClientSecret = settings.Chat.KickClientSecret
        });
        root[ProtectedSecretsProperty] = JsonSerializer.SerializeToNode(envelope, SerializerOptions);
        var payload = JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions);
        if (payload.Length > MaximumSettingsBytes)
        {
            throw new InvalidDataException(
                $"Settings exceeded the {MaximumSettingsBytes:N0}-byte limit.");
        }

        var targetDirectory = string.IsNullOrWhiteSpace(directory)
            ? Directory.GetCurrentDirectory()
            : directory;
        var tempPath = Path.Combine(
            targetDirectory,
            $"{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81_920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
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
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static SettingsSecrets ReadLegacySecrets(JsonObject root, out bool foundAny)
    {
        foundAny = false;
        var secrets = new SettingsSecrets();
        if (!TryGetObjectCaseInsensitive(root, nameof(AppSettings.Chat), out var chat))
        {
            return secrets;
        }

        secrets.TwitchOAuthToken = ReadLegacySecret(chat, nameof(ChatSettings.TwitchOAuthToken), ref foundAny);
        secrets.KickOAuthToken = ReadLegacySecret(chat, nameof(ChatSettings.KickOAuthToken), ref foundAny);
        secrets.KickRefreshToken = ReadLegacySecret(chat, nameof(ChatSettings.KickRefreshToken), ref foundAny);
        secrets.KickClientSecret = ReadLegacySecret(chat, nameof(ChatSettings.KickClientSecret), ref foundAny);
        return secrets;
    }

    private static string ReadLegacySecret(JsonObject chat, string propertyName, ref bool foundAny)
    {
        if (!TryGetPropertyCaseInsensitive(chat, propertyName, out _, out var node))
        {
            return "";
        }

        foundAny = true;
        return node is JsonValue value && value.TryGetValue<string>(out var secret)
            ? secret ?? ""
            : "";
    }

    private static void ApplySecrets(ChatSettings chat, SettingsSecrets secrets)
    {
        chat.TwitchOAuthToken = secrets.TwitchOAuthToken;
        chat.KickOAuthToken = secrets.KickOAuthToken;
        chat.KickRefreshToken = secrets.KickRefreshToken;
        chat.KickClientSecret = secrets.KickClientSecret;
    }

    private static void RemoveSecretProperties(JsonObject root)
    {
        if (!TryGetObjectCaseInsensitive(root, nameof(AppSettings.Chat), out var chat))
        {
            return;
        }

        foreach (var propertyName in SecretPropertyNames)
        {
            _ = RemovePropertyCaseInsensitive(chat, propertyName, out _);
        }
    }

    private static bool TryGetObjectCaseInsensitive(
        JsonObject parent,
        string propertyName,
        out JsonObject value)
    {
        if (TryGetPropertyCaseInsensitive(parent, propertyName, out _, out var node) &&
            node is JsonObject objectValue)
        {
            value = objectValue;
            return true;
        }

        value = null!;
        return false;
    }

    private static bool RemovePropertyCaseInsensitive(
        JsonObject parent,
        string propertyName,
        out JsonNode? value)
    {
        if (!TryGetPropertyCaseInsensitive(parent, propertyName, out var actualName, out value))
        {
            return false;
        }

        return parent.Remove(actualName);
    }

    private static bool TryGetPropertyCaseInsensitive(
        JsonObject parent,
        string propertyName,
        out string actualName,
        out JsonNode? value)
    {
        foreach (var property in parent)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                actualName = property.Key;
                value = property.Value;
                return true;
            }
        }

        actualName = "";
        value = null;
        return false;
    }

    private static string? BackupCorruptProtectedSettings(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        var targetDirectory = string.IsNullOrWhiteSpace(directory)
            ? Directory.GetCurrentDirectory()
            : directory;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(
            targetDirectory,
            $"{Path.GetFileName(settingsPath)}.protected-secrets-corrupt-{timestamp}-{Guid.NewGuid():N}");
        try
        {
            File.Copy(settingsPath, backupPath, overwrite: false);
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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

        try
        {
            File.Move(settingsPath, backupPath);
        }
        catch (IOException)
        {
            // Loading defaults is still safer than failing application startup when
            // another process has the invalid settings file open.
        }
        catch (UnauthorizedAccessException)
        {
            // The original file is left in place so the user can recover it manually.
        }
    }
}
