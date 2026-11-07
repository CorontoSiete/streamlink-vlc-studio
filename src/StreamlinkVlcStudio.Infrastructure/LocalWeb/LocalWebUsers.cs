using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Io;
using static StreamlinkVlcStudio.Infrastructure.LocalWeb.LocalWebRequestValues;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed record LocalWebUserSummary(string Id, string Username);

internal sealed record LocalWebHotkey(string Key, bool Ctrl, bool Alt, bool Shift, bool Meta);

internal sealed record LocalWebPreferences
{
    public string Quality { get; init; } = "best";
    public bool LowLatency { get; init; } = true;
    public string Layout { get; init; } = "docked";
    public int Size { get; init; } = 16;
    public int Opacity { get; init; } = 90;
    public bool KeepInactive { get; init; }
    public bool PauseInactiveVod { get; init; } = true;
    public Dictionary<string, LocalWebHotkey?> Hotkeys { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Volumes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WebChatOverlayLayout> ChatLayouts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CategoryLanguages { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    internal LocalWebPreferences Copy() => this with
    {
        Hotkeys = new(Hotkeys, StringComparer.Ordinal),
        Volumes = new(Volumes, StringComparer.OrdinalIgnoreCase),
        ChatLayouts = new(ChatLayouts, StringComparer.OrdinalIgnoreCase),
        CategoryLanguages = new(CategoryLanguages, StringComparer.OrdinalIgnoreCase)
    };

    internal static LocalWebPreferences Apply(LocalWebPreferences current, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid preferences.");
        var next = current.Copy();
        foreach (var property in patch.EnumerateObject())
        {
            switch (property.Name)
            {
                case "quality":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        !LocalWebWorkspace.Qualities.Contains(property.Value.GetString(), StringComparer.Ordinal))
                        throw new ArgumentException("Unsupported quality.");
                    next = next with { Quality = property.Value.GetString()! };
                    break;
                case "lowLatency": next = next with { LowLatency = Boolean(property.Value) }; break;
                case "layout":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        property.Value.GetString() is not ("docked" or "overlay" or "hidden"))
                        throw new ArgumentException("Invalid chat layout.");
                    next = next with { Layout = property.Value.GetString()! };
                    break;
                case "size": next = next with { Size = Integer(property.Value, 12, 32) }; break;
                case "opacity": next = next with { Opacity = Integer(property.Value, 0, 100) }; break;
                case "keepInactive": next = next with { KeepInactive = Boolean(property.Value) }; break;
                case "pauseInactiveVod": next = next with { PauseInactiveVod = Boolean(property.Value) }; break;
                case "hotkeys": next = next with { Hotkeys = ReadHotkeys(property.Value) }; break;
                default: throw new ArgumentException("Unknown preference.");
            }
        }
        return next;
    }

    internal static WebChatOverlayLayout ReadLayout(JsonElement input)
    {
        if (!input.TryGetProperty("layout", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid chat layout.");
        double? Number(string name, bool nullable = false)
        {
            if (value.TryGetProperty(name, out var property))
            {
                if (nullable && property.ValueKind == JsonValueKind.Null) return null;
                if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)) return number;
            }
            throw new ArgumentException("Invalid chat layout.");
        }
        var layout = new WebChatOverlayLayout(Number("width", true), Number("height", true), Number("x")!.Value, Number("y")!.Value);
        return WebChatOverlayLayout.IsValid(layout) ? layout : throw new ArgumentException("Invalid chat layout.");
    }

    private static Dictionary<string, LocalWebHotkey?> ReadHotkeys(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > 6)
            throw new ArgumentException("Invalid shortcuts.");
        var result = new Dictionary<string, LocalWebHotkey?>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(["back", "nextTab", "previousTab", "grid", "keepAudio", "mute"], StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject())
        {
            if (!allowed.Contains(entry.Name) || !result.TryAdd(entry.Name, null)) throw new ArgumentException("Invalid shortcuts.");
            if (entry.Value.ValueKind == JsonValueKind.Null) continue;
            if (entry.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid shortcuts.");
            if (entry.Value.EnumerateObject().Count() != 5 ||
                !entry.Value.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String ||
                !entry.Value.TryGetProperty("ctrl", out var ctrl) ||
                !entry.Value.TryGetProperty("alt", out var alt) ||
                !entry.Value.TryGetProperty("shift", out var shift) ||
                !entry.Value.TryGetProperty("meta", out var meta)) throw new ArgumentException("Invalid shortcuts.");
            var key = keyValue.GetString();
            if (key is null || !ValidHotkeyKey(key)) throw new ArgumentException("Invalid shortcuts.");
            var binding = new LocalWebHotkey(key,
                Boolean(ctrl), Boolean(alt), Boolean(shift), Boolean(meta));
            if ((binding.Ctrl || binding.Meta) && !binding.Alt && key == "k" ||
                !binding.Ctrl && !binding.Alt && !binding.Meta && (key == "/" || !binding.Shift && key is "k" or "f") ||
                result.Values.Contains(binding)) throw new ArgumentException("Invalid shortcuts.");
            result[entry.Name] = binding;
        }
        return result;
    }

    private static bool ValidHotkeyKey(string key)
    {
        // Match the browser's single Unicode character and named-key rules.
        if (key.Length > 0 && Rune.TryGetRuneAt(key, 0, out var rune) && rune.Utf16SequenceLength == key.Length)
            return !Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) is not
                (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);
        return key is "Escape" or "Backspace" or "Delete" or "Insert" or "Home" or "End" or "PageUp" or "PageDown" or
            "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" or "Mouse4" or "Mouse5" or
            ['F', >= '1' and <= '9'] or ['F', '1', >= '0' and <= '2'];
    }

    internal static void Validate(LocalWebPreferences preferences)
    {
        if (!LocalWebWorkspace.Qualities.Contains(preferences.Quality, StringComparer.Ordinal) ||
            preferences.Layout is not ("docked" or "overlay" or "hidden") ||
            preferences.Size is < 12 or > 32 || preferences.Opacity is < 0 or > 100 ||
            preferences.Hotkeys is null || preferences.Volumes is null || preferences.ChatLayouts is null || preferences.CategoryLanguages is null ||
            preferences.Volumes.Count > 512 || preferences.ChatLayouts.Count > 512)
            throw new InvalidDataException("Saved web preferences are invalid.");
        foreach (var (key, volume) in preferences.Volumes)
            if (!ValidStreamKey(key) || !double.IsFinite(volume) || volume is < 0 or > 1)
                throw new InvalidDataException("Saved web preferences are invalid.");
        foreach (var (key, layout) in preferences.ChatLayouts)
            if (!ValidStreamKey(key) || !WebChatOverlayLayout.IsValid(layout))
                throw new InvalidDataException("Saved web preferences are invalid.");
        var categoryLanguages = CategoryLanguagePreferences.Normalize(preferences.CategoryLanguages);
        if (categoryLanguages.Count != preferences.CategoryLanguages.Count ||
            preferences.CategoryLanguages.Any(entry => !categoryLanguages.TryGetValue(entry.Key, out var language) || language != entry.Value))
            throw new InvalidDataException("Saved category languages are invalid.");
        try { ReadHotkeys(JsonSerializer.SerializeToElement(preferences.Hotkeys, LocalWebUserStore.JsonOptions)); }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Saved web preferences are invalid.", error); }
    }

    internal static bool ValidStreamKey(string key)
    {
        var prefixLength = key.StartsWith("Twitch:", StringComparison.OrdinalIgnoreCase) ? 7 :
            key.StartsWith("Kick:", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
        if (prefixLength == 0) return false;
        var channel = key[prefixLength..];
        return channel.Length is > 0 and <= 100 && channel == channel.Trim() && !channel.Any(char.IsControl);
    }
}

internal sealed record LocalWebUserRecord(string Id, string Username, string Salt, string Hash, int Iterations,
    string CredentialVersion, LocalWebPreferences Preferences);

internal sealed record LocalWebUsersFile(int Version, List<LocalWebUserRecord> Users);

/// <summary>Host-owned accounts and browser preferences. Writes are atomic and a damaged file fails closed.</summary>
public sealed class LocalWebUserStore
{
    private const int Iterations = 600_000;
    private const int MaximumBytes = 1024 * 1024;
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(16);
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowDuplicateProperties = false
    };
    private readonly string path;
    private readonly object stateGate = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly SemaphoreSlim verifications = new(2, 2);
    private List<LocalWebUserRecord> users;

    private LocalWebUserStore(string path, List<LocalWebUserRecord> users)
    { this.path = path; this.users = users; }

    public static async Task<LocalWebUserStore> OpenAsync(string? path = null, CancellationToken token = default)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.ProductDirectoryName, "web-users.json");
        if (!File.Exists(path)) return new(path, []);
        var file = new FileInfo(path);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Saved web accounts exceed the size limit.");
        await using var stream = await AtomicFile.OpenReadAsync(path, token).ConfigureAwait(false);
        var saved = await JsonSerializer.DeserializeAsync<LocalWebUsersFile>(stream, JsonOptions, token).ConfigureAwait(false);
        if (saved is null || saved.Version != 1 || saved.Users is null || saved.Users.Count > 32)
            throw new InvalidDataException("Saved web accounts are invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in saved.Users)
        {
            if (user is null || !ValidUsername(user.Username) || !names.Add(user.Username) ||
                !Guid.TryParseExact(user.Id, "N", out _) || !ids.Add(user.Id) ||
                !Guid.TryParseExact(user.CredentialVersion, "N", out _) ||
                user.Iterations is < Iterations or > 2_000_000 ||
                !ValidBase64(user.Salt, 16) || !ValidBase64(user.Hash, 32) || user.Preferences is null)
                throw new InvalidDataException("Saved web accounts are invalid.");
            LocalWebPreferences.Validate(user.Preferences);
        }
        return new(path, saved.Users);
    }

    public IReadOnlyList<LocalWebUserSummary> ListUsers()
    { lock (stateGate) return users.Select(u => new LocalWebUserSummary(u.Id, u.Username)).OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToArray(); }

    internal bool IsCurrent(string id, string credentialVersion)
    { lock (stateGate) return users.Any(u => u.Id == id && u.CredentialVersion == credentialVersion); }

    public async Task<LocalWebUserSummary> AddAsync(string username, string password, CancellationToken token = default)
    {
        if (!ValidUsername(username)) throw new ArgumentException("Username must be 3 to 32 letters, numbers, underscores or hyphens.");
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = await HashAsync(password, salt, Iterations, token).ConfigureAwait(false);
        var record = new LocalWebUserRecord(Guid.NewGuid().ToString("N"), username, Convert.ToBase64String(salt),
            Convert.ToBase64String(hash), Iterations, Guid.NewGuid().ToString("N"), new());
        await ChangeAsync(list =>
        {
            if (list.Count >= 32) throw new InvalidOperationException("The web account limit has been reached.");
            if (list.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("That username is already in use.");
            list.Add(record);
        }, token).ConfigureAwait(false);
        return new(record.Id, record.Username);
    }

    public async Task ChangePasswordAsync(string id, string password, CancellationToken token = default)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = await HashAsync(password, salt, Iterations, token).ConfigureAwait(false);
        await ChangeAsync(list =>
        {
            var index = list.FindIndex(u => u.Id == id);
            if (index < 0) throw new ArgumentException("Choose an existing web account.");
            list[index] = list[index] with
            {
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(hash),
                Iterations = Iterations,
                CredentialVersion = Guid.NewGuid().ToString("N")
            };
        }, token).ConfigureAwait(false);
    }

    public Task RemoveAsync(string id, CancellationToken token = default) => ChangeAsync(list =>
    {
        if (list.Count <= 1) throw new InvalidOperationException("Create another account before removing the last one.");
        if (list.RemoveAll(u => u.Id == id) != 1) throw new ArgumentException("Choose an existing web account.");
    }, token);

    internal async Task<LocalWebUserRecord?> VerifyAsync(string username, string password, CancellationToken token)
    {
        if (!ValidUsername(username) || password.Length is < 1 or > 256) return null;
        await verifications.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LocalWebUserRecord? user;
            lock (stateGate) user = users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            var salt = user is null ? DummySalt : Convert.FromBase64String(user.Salt);
            var hash = await HashAsync(password, salt, user?.Iterations ?? Iterations, token).ConfigureAwait(false);
            if (user is null || !CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(user.Hash))) return null;
            lock (stateGate) return users.FirstOrDefault(u => u.Id == user.Id && u.CredentialVersion == user.CredentialVersion);
        }
        finally { verifications.Release(); }
    }

    internal LocalWebPreferences PreferencesFor(string id)
    { lock (stateGate) return Find(id).Preferences.Copy(); }

    internal Task SavePreferencesAsync(string id, JsonElement patch, CancellationToken token) =>
        UpdatePreferencesAsync(id, current => LocalWebPreferences.Apply(current, patch), token);

    internal Task SaveVolumeAsync(string id, string key, double volume, CancellationToken token)
    {
        if (!LocalWebPreferences.ValidStreamKey(key) || !double.IsFinite(volume) || volume is < 0 or > 1)
            throw new ArgumentException("Invalid volume.");
        return UpdatePreferencesAsync(id, current =>
        {
            var preferences = current.Copy();
            if (!preferences.Volumes.ContainsKey(key) && preferences.Volumes.Count >= 512) throw new ArgumentException("Volume limit reached.");
            preferences.Volumes[key] = volume;
            return preferences;
        }, token);
    }

    internal Task SaveChatLayoutAsync(string id, string key, WebChatOverlayLayout layout, CancellationToken token)
    {
        if (!LocalWebPreferences.ValidStreamKey(key) || !WebChatOverlayLayout.IsValid(layout))
            throw new ArgumentException("Invalid chat layout.");
        return UpdatePreferencesAsync(id, current =>
        {
            var preferences = current.Copy();
            if (!preferences.ChatLayouts.ContainsKey(key) && preferences.ChatLayouts.Count >= 512) throw new ArgumentException("Chat layout limit reached.");
            preferences.ChatLayouts[key] = layout;
            return preferences;
        }, token);
    }

    internal Task SaveCategoryLanguageAsync(string id, string key, string language, CancellationToken token)
    {
        return UpdatePreferencesAsync(id, current =>
        {
            // Compare after earlier writes finish. A choice that matches the last
            // committed value can still supersede a different queued choice.
            var normalized = BrowseLanguages.Normalize(language);
            if (current.CategoryLanguages.TryGetValue(key, out var saved) && saved == normalized) return current;
            var preferences = current.Copy();
            preferences.CategoryLanguages[key] = normalized;
            return preferences;
        }, token);
    }

    private Task UpdatePreferencesAsync(string id, Func<LocalWebPreferences, LocalWebPreferences> update,
        CancellationToken token) => ChangeAsync(list =>
    {
        var index = list.FindIndex(u => u.Id == id);
        if (index < 0) throw new UnauthorizedAccessException();
        list[index] = list[index] with { Preferences = update(list[index].Preferences) };
    }, token);

    private LocalWebUserRecord Find(string id) => users.FirstOrDefault(u => u.Id == id) ?? throw new UnauthorizedAccessException();

    private async Task ChangeAsync(Action<List<LocalWebUserRecord>> change, CancellationToken token)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            List<LocalWebUserRecord> current;
            lock (stateGate) current = users;
            List<LocalWebUserRecord> next = [.. current];
            change(next);
            if (next.SequenceEqual(current)) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new LocalWebUsersFile(1, next), JsonOptions);
            if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Saved web accounts exceed the size limit.");
            await AtomicFile.WriteAsync(path, (stream, cancellation) => stream.WriteAsync(bytes, cancellation).AsTask(),
                token, flushToDisk: true).ConfigureAwait(false);
            lock (stateGate) users = next;
        }
        finally { writes.Release(); }
    }

    private static Task<byte[]> HashAsync(string password, byte[] salt, int iterations, CancellationToken token) =>
        Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32), token);

    private static bool ValidBase64(string? text, int byteCount)
    {
        if (text is null) return false;
        try { return Convert.FromBase64String(text).Length == byteCount; }
        catch (FormatException) { return false; }
    }

    private static bool ValidUsername(string? username) => username is { Length: >= 3 and <= 32 } &&
        username.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 12 or > 128 || password.Any(char.IsControl))
            throw new ArgumentException("Password must be 12 to 128 characters without control characters.");
    }
}
