using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Io;
using StreamlinkVlcStudio.Infrastructure.Text;

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
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public JsonSettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? ResolveDefaultSettingsPath();
    }

    public string SettingsPath { get; }

    public string? LastLoadWarning { get; private set; }

    private static string ResolveDefaultSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsPath = Path.Combine(appData, AppIdentity.ProductDirectoryName, "settings.json");
        var legacySettingsPath = Path.Combine(appData, AppIdentity.LegacyProductDirectoryName, "settings.json");
        TryCopyLegacySettingsForward(legacySettingsPath, settingsPath);
        return settingsPath;
    }

    private static void TryCopyLegacySettingsForward(string legacySettingsPath, string settingsPath)
    {
        try
        {
            if (File.Exists(settingsPath) || !File.Exists(legacySettingsPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(legacySettingsPath, settingsPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

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
                    var backupPath = TryPreserveSettingsFile(SettingsPath, "protected-secrets-corrupt", copy: true);
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
            var backupPath = TryPreserveSettingsFile(SettingsPath, "invalid", copy: false);
            LastLoadWarning = backupPath is null
                ? $"Saved settings were invalid or exceeded the size limit; defaults were loaded. The original file could not be moved from {SettingsPath}."
                : $"Saved settings were invalid or exceeded the size limit; defaults were loaded. A backup was preserved at {backupPath}.";
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
        var offset = EncodingPreamble.GetLength(bytes, Encoding.UTF8);
        return JsonNode.Parse(
            bytes.AsSpan(offset),
            documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
            ?? throw new JsonException("Settings root must be a JSON object.");
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
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

        try
        {
            await AtomicFile.WriteAsync(
                SettingsPath,
                (stream, token) => stream.WriteAsync(payload, token).AsTask(),
                cancellationToken,
                flushToDisk: true).ConfigureAwait(false);
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
        actualName = "";
        value = null;
        foreach (var property in parent)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                if (actualName.Length > 0)
                {
                    throw new JsonException($"Settings contain duplicate '{propertyName}' properties.");
                }

                actualName = property.Key;
                value = property.Value;
            }
        }

        return actualName.Length > 0;
    }

    private static string? TryPreserveSettingsFile(string settingsPath, string reason, bool copy)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        var targetDirectory = string.IsNullOrWhiteSpace(directory)
            ? Directory.GetCurrentDirectory()
            : directory;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(
            targetDirectory,
            $"{Path.GetFileName(settingsPath)}.{reason}-{timestamp}-{Guid.NewGuid():N}");
        try
        {
            if (copy)
            {
                File.Copy(settingsPath, backupPath, overwrite: false);
            }
            else
            {
                File.Move(settingsPath, backupPath);
            }
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

}
