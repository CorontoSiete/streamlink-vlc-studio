# Sub-only Twitch VOD Playback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a Twitch VOD is subscriber-only and streamlink cannot resolve it, play it anyway by deriving the public CloudFront HLS playlists from the VOD's storyboard metadata (the TwitchNoSub technique), and let users paste `twitch.tv/videos/{id}` URLs into the search box.

**Architecture:** Fallback resolver. Normal VODs keep the existing streamlink path; only a streamlink failure on a `StreamTargetKind.TwitchVod` target triggers the bypass. Pure URL/playlist logic lives in Core (unit-testable), HTTP + temp-file work lives in Infrastructure, and `StreamTabViewModel.StartAsync` wires it into playback. Spec: `docs/superpowers/specs/2026-08-05-sub-only-twitch-vod-design.md`.

**Tech Stack:** .NET 9 (SDK pinned to 9.0.302 via `global.json`), C# (`Nullable`+`ImplicitUsings` on), WPF, `System.Net.Http` + `System.Text.Json` (no new NuGet packages), dependency-free test harness in `tests/StreamlinkVlcStudio.Tests/Program.cs`.

## Global Constraints

- **No git mutations.** Do NOT run `git add`/`git commit`/`git push` or any other git-mutating command. Leave all changes in the working tree. (End each task with a test run instead of a commit.)
- No new NuGet package dependencies. Core and Infrastructure must stay dependency-free.
- Follow the existing patterns: static shared `HttpClient` with ctor-injectable override, `IAppLogger` logging, `JsonElementReader` helpers for JSON.
- Tests are registry entries (`("name", () => {...})` or `async () => {...}`) in the `tests` array at `tests/StreamlinkVlcStudio.Tests/Program.cs:40`; fakes live at the bottom of the same file. Assert API: `Assert.Equal<T>`, `Assert.True(bool)`, `Assert.NotNull`, `Assert.Contains`, `Assert.DoesNotContain`, `Assert.Throws<T>`, `Assert.ThrowsAsync<T>`, `Assert.SequenceEqual`.
- Run focused tests with the `SVS_TEST_FILTER` env var (substring match on test name): `SVS_TEST_FILTER="sub-only" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`. Full suite: `dotnet test`. Build: `dotnet build StreamlinkVlcStudio.sln`.
- The Infrastructure project errors at build time if bundled native overlay DLLs are missing from `src/StreamlinkVlcStudio.Infrastructure/Vlc/BundledOverlay/build`; they are present in a normal checkout — do not delete them.
- VOD ID query interpolation is only safe because the ID is validated as digits-only first; keep that validation.
- Reference implementation parity: TwitchNoSub compares `createdAt` against a frozen cutoff of **2023-02-10** (not "now") when choosing the upload URL shape. Keep this exact behavior — it is deliberate (newer uploads do not expose the `index-dvr` layout, and probing then simply yields no variants).

---

### Task 1: Core pure helpers — `TwitchSubOnlyVodPlaylist`

**Files:**
- Create: `src/StreamlinkVlcStudio.Core/Twitch/TwitchSubOnlyVodPlaylist.cs`
- Test: `tests/StreamlinkVlcStudio.Tests/Program.cs` (add registry entries after the entry ending at line 137)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `TwitchSubOnlyVodPlaylist.QualityKeys` — `IReadOnlyList<string>`, ordered best-first: `chunked, 1080p60, 720p60, 480p30, 360p30, 160p30`.
  - `bool TryParseStoryboardLocation(string? seekPreviewsUrl, out string host, out string specialId)`
  - `string BuildVariantPlaylistUrl(string broadcastType, DateTimeOffset createdAtUtc, DateTimeOffset nowUtc, string host, string specialId, string ownerLogin, string vodId, string qualityKey)`
  - `string SelectQualityKey(IReadOnlyList<string> availableKeys, string? requestedQuality)` — `availableKeys` must be ordered best-first (Task 2 collects them in `QualityKeys` order).
  - `string RewriteMediaPlaylist(string playlistContent, Uri playlistUri)`

- [ ] **Step 1: Write the failing tests**

Add `using StreamlinkVlcStudio.Core.Twitch;` to the top of `tests/StreamlinkVlcStudio.Tests/Program.cs`, then add these registry entries to the `tests` array (right after the `("ignores browser @-prefixed Twitch non-channel URL", ...)` entry):

```csharp
    ("sub-only VOD storyboard location parses host and special id", () =>
    {
        Assert.True(TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(
            "https://d2e2de1etea730.cloudfront.net/abc123_def456_789/storyboards/0.jpg",
            out var host,
            out var specialId));
        Assert.Equal("d2e2de1etea730.cloudfront.net", host);
        Assert.Equal("abc123_def456_789", specialId);
        return Task.CompletedTask;
    }),
    ("sub-only VOD storyboard location rejects invalid input", () =>
    {
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation("", out _, out _));
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(null, out _, out _));
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation("not a url", out _, out _));
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation("https://cdn.example.com/storyboards/0.jpg", out _, out _));
        return Task.CompletedTask;
    }),
    ("sub-only VOD variant URL shapes follow broadcast type", () =>
    {
        var created = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cutoff = new DateTimeOffset(2023, 2, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            "https://cdn.example.com/special/chunked/index-dvr.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("ARCHIVE", created, cutoff, "cdn.example.com", "special", "streamer", "123", "chunked"));
        Assert.Equal(
            "https://cdn.example.com/special/720p60/highlight-123.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("highlight", created, cutoff, "cdn.example.com", "special", "streamer", "123", "720p60"));
        Assert.Equal(
            "https://cdn.example.com/streamer/123/special/480p30/index-dvr.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("upload", created, cutoff, "cdn.example.com", "special", "streamer", "123", "480p30"));
        var recentUpload = new DateTimeOffset(2023, 2, 9, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            "https://cdn.example.com/special/480p30/index-dvr.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("upload", recentUpload, cutoff, "cdn.example.com", "special", "streamer", "123", "480p30"));
        return Task.CompletedTask;
    }),
    ("sub-only VOD quality selection maps app qualities", () =>
    {
        var all = new[] { "chunked", "1080p60", "720p60", "480p30", "360p30", "160p30" };
        Assert.Equal("chunked", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "best"));
        Assert.Equal("chunked", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "source"));
        Assert.Equal("1080p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "1080p"));
        Assert.Equal("1080p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "1080p60"));
        Assert.Equal("720p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "720p60"));
        Assert.Equal("720p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "720p"));
        Assert.Equal("480p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "480p"));
        Assert.Equal("160p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "worst"));
        Assert.Equal("160p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "audio_only"));

        var sparse = new[] { "chunked", "720p60", "360p30" };
        Assert.Equal("720p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "1080p"));
        Assert.Equal("360p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "480p"));
        Assert.Equal("chunked", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "best"));
        Assert.Equal("360p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "worst"));
        return Task.CompletedTask;
    }),
    ("sub-only VOD playlist rewrite mutes and absolutizes", () =>
    {
        var playlist = "#EXTM3U\n" +
            "#EXT-X-TARGETDURATION:10\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n" +
            "#EXTINF:10.000,\n" +
            "0-unmuted.ts\n" +
            "#EXTINF:10.000,\n" +
            "https://cdn.other.com/already/absolute.ts\n";
        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(
            playlist,
            new Uri("https://cdn.example.com/special/chunked/index-dvr.m3u8"));
        Assert.Contains("https://cdn.example.com/special/chunked/0-muted.ts", rewritten);
        Assert.DoesNotContain("-unmuted", rewritten);
        Assert.Contains("URI=\"https://cdn.example.com/special/chunked/key.bin\"", rewritten);
        Assert.Contains("https://cdn.other.com/already/absolute.ts", rewritten);
        return Task.CompletedTask;
    }),
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `SVS_TEST_FILTER="sub-only" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: build error (`TwitchSubOnlyVodPlaylist` does not exist) or FAIL entries for the five new tests.

- [ ] **Step 3: Implement `TwitchSubOnlyVodPlaylist`**

Create `src/StreamlinkVlcStudio.Core/Twitch/TwitchSubOnlyVodPlaylist.cs`:

```csharp
using System.Text;

namespace StreamlinkVlcStudio.Core.Twitch;

/// <summary>
/// Pure helpers that build direct CloudFront playlist URLs for subscriber-only Twitch VODs
/// from the VOD's public storyboard (seek preview) metadata. This reimplements the technique
/// used by the TwitchNoSub browser extension (https://github.com/besuper/TwitchNoSub):
/// usher.ttvnw.net refuses sub-only VODs, but the segments on CloudFront need no token.
/// </summary>
public static class TwitchSubOnlyVodPlaylist
{
    // Ordered best-first; the same renditions TwitchNoSub probes for.
    public static IReadOnlyList<string> QualityKeys { get; } =
        ["chunked", "1080p60", "720p60", "480p30", "360p30", "160p30"];

    public static bool TryParseStoryboardLocation(
        string? seekPreviewsUrl,
        out string host,
        out string specialId)
    {
        host = "";
        specialId = "";

        if (string.IsNullOrWhiteSpace(seekPreviewsUrl) ||
            !Uri.TryCreate(seekPreviewsUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var storyboardIndex = Array.FindIndex(
            segments,
            segment => segment.Contains("storyboards", StringComparison.OrdinalIgnoreCase));
        if (storyboardIndex < 1)
        {
            return false;
        }

        host = uri.Host;
        specialId = segments[storyboardIndex - 1];
        return specialId.Length > 0;
    }

    public static string BuildVariantPlaylistUrl(
        string broadcastType,
        DateTimeOffset createdAtUtc,
        DateTimeOffset nowUtc,
        string host,
        string specialId,
        string ownerLogin,
        string vodId,
        string qualityKey)
    {
        var type = (broadcastType ?? "").Trim().ToLowerInvariant();
        if (type == "highlight")
        {
            return $"https://{host}/{specialId}/{qualityKey}/highlight-{vodId}.m3u8";
        }

        if (type == "upload" && nowUtc - createdAtUtc > TimeSpan.FromDays(7))
        {
            return $"https://{host}/{ownerLogin}/{vodId}/{specialId}/{qualityKey}/index-dvr.m3u8";
        }

        return $"https://{host}/{specialId}/{qualityKey}/index-dvr.m3u8";
    }

    public static string SelectQualityKey(IReadOnlyList<string> availableKeys, string? requestedQuality)
    {
        if (availableKeys.Count == 0)
        {
            throw new ArgumentException("At least one available quality key is required.", nameof(availableKeys));
        }

        var requested = (requestedQuality ?? "").Trim().ToLowerInvariant();
        if (requested is "worst" or "audio_only")
        {
            return availableKeys[^1];
        }

        var preferred = requested switch
        {
            "1080p60" or "1080p" => "1080p60",
            "720p60" or "720p" => "720p60",
            "480p" => "480p30",
            _ => "chunked"
        };

        var preferredIndex = IndexOfQualityKey(preferred);
        string? bestKey = null;
        var bestDistance = int.MaxValue;
        var bestIndex = int.MaxValue;
        foreach (var key in availableKeys)
        {
            var index = IndexOfQualityKey(key);
            if (index < 0)
            {
                continue;
            }

            var distance = Math.Abs(index - preferredIndex);
            if (distance < bestDistance || (distance == bestDistance && index > bestIndex))
            {
                bestDistance = distance;
                bestIndex = index;
                bestKey = key;
            }
        }

        return bestKey ?? availableKeys[0];
    }

    public static string RewriteMediaPlaylist(string playlistContent, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(playlistContent);
        ArgumentNullException.ThrowIfNull(playlistUri);

        // Sub-only VODs 404 on the "-unmuted" segment names; "-muted" always exists.
        var mutedContent = playlistContent.Replace("-unmuted", "-muted", StringComparison.Ordinal);
        var lines = mutedContent.Split('\n');
        var builder = new StringBuilder(mutedContent.Length + 256);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            // Skip the artificial empty entry produced by a trailing newline.
            if (line.Length == 0 && i == lines.Length - 1)
            {
                break;
            }

            if (line.Length > 0)
            {
                builder.Append(line[0] == '#'
                    ? RewriteTagLine(line, playlistUri)
                    : AbsolutizeUri(line.Trim(), playlistUri));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static int IndexOfQualityKey(string key)
    {
        for (var i = 0; i < QualityKeys.Count; i++)
        {
            if (string.Equals(QualityKeys[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string RewriteTagLine(string line, Uri playlistUri)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return line;
        }

        start += marker.Length;
        var end = line.IndexOf('"', start);
        if (end < 0)
        {
            return line;
        }

        var uri = line[start..end];
        return string.Concat(line[..start], AbsolutizeUri(uri, playlistUri), line[end..]);
    }

    private static string AbsolutizeUri(string uri, Uri playlistUri)
    {
        if (uri.Length == 0 ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return new Uri(playlistUri, uri).ToString();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `SVS_TEST_FILTER="sub-only" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: all five new tests PASS, no regressions.

---

### Task 2: `ITwitchSubOnlyVodResolver` interface + Infrastructure `TwitchSubOnlyVodResolver`

**Files:**
- Create: `src/StreamlinkVlcStudio.Core/Services/ITwitchSubOnlyVodResolver.cs`
- Create: `src/StreamlinkVlcStudio.Infrastructure/Twitch/TwitchSubOnlyVodResolver.cs`
- Test: `tests/StreamlinkVlcStudio.Tests/Program.cs` (registry entries + nothing else; reuses `FakeHttpMessageHandler` at line 31020 and `MemoryLogger` at line 30947)

**Interfaces:**
- Consumes: `TwitchSubOnlyVodPlaylist` (Task 1), `IAppLogger` (`StreamlinkVlcStudio.Core.Logging`), `JsonElementReader` static helpers (`GetOptionalString`, `TryGetDateTimeOffset`, `TryReadNestedString`).
- Produces:
  - `ITwitchSubOnlyVodResolver.ResolveAsync(TwitchSubOnlyVodRequest request, CancellationToken cancellationToken = default)` → `Task<TwitchSubOnlyVodResolution>`
  - `TwitchSubOnlyVodRequest(string VodId, string Quality)`
  - `TwitchSubOnlyVodResolution(Uri PlaybackUri, string QualityKey, string Message)` — `PlaybackUri` is a `file:///` URI of a rewritten local playlist.
  - `TwitchSubOnlyVodResolver(IAppLogger logger)` and `TwitchSubOnlyVodResolver(IAppLogger logger, HttpClient httpClient, string playlistDirectory)` — Task 3 and tests use these.

- [ ] **Step 1: Write the failing tests**

Add `using StreamlinkVlcStudio.Infrastructure.Twitch;` to the top of `tests/StreamlinkVlcStudio.Tests/Program.cs`, then add these registry entries:

```csharp
    ("sub-only VOD resolver builds direct playlist from storyboard metadata", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"ARCHIVE\",\"createdAt\":\"2023-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url == "https://gql.twitch.tv/gql")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) };
            }

            if (url.Contains("/chunked/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:10.0,\n0-unmuted.ts\n") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(handler);
            var resolver = new TwitchSubOnlyVodResolver(new MemoryLogger(), httpClient, tempDir);

            var resolution = await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best"));

            Assert.Equal("chunked", resolution.QualityKey);
            Assert.Equal(new Uri(Path.Combine(tempDir, "123456-chunked.m3u8")), resolution.PlaybackUri);
            var playlist = await File.ReadAllTextAsync(resolution.PlaybackUri.LocalPath);
            Assert.Contains("https://d2e2de1etea730.cloudfront.net/abc_def/chunked/0-muted.ts", playlist);
            Assert.DoesNotContain("-unmuted", playlist);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }),
    ("sub-only VOD resolver selects the requested quality among valid variants", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"ARCHIVE\",\"createdAt\":\"2023-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url == "https://gql.twitch.tv/gql")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) };
            }

            if (url.Contains("/720p60/", StringComparison.Ordinal) || url.Contains("/360p30/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:10.0,\n0.ts\n") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(handler);
            var resolver = new TwitchSubOnlyVodResolver(new MemoryLogger(), httpClient, tempDir);

            var resolution = await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best"));

            Assert.Equal("720p60", resolution.QualityKey);
            Assert.True(File.Exists(resolution.PlaybackUri.LocalPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }),
    ("sub-only VOD resolver uses the upload URL shape for old uploads", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"UPLOAD\",\"createdAt\":\"2020-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            requestedUrls.Add(url);
            if (url == "https://gql.twitch.tv/gql")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:10.0,\n0.ts\n") };
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(handler);
            var resolver = new TwitchSubOnlyVodResolver(new MemoryLogger(), httpClient, tempDir);

            await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "worst"));

            Assert.True(requestedUrls.Contains("https://d2e2de1etea730.cloudfront.net/streamer/123456/abc_def/chunked/index-dvr.m3u8"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }),
    ("sub-only VOD resolver reports a missing video", async () =>
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"video\":null}}") });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(handler);
        var resolver = new TwitchSubOnlyVodResolver(new MemoryLogger(), httpClient, tempDir);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best")));
        Assert.Contains("not found", error.Message);
    }),
    ("sub-only VOD resolver errors when no variants exist", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"ARCHIVE\",\"createdAt\":\"2023-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            return url == "https://gql.twitch.tv/gql"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(handler);
        var resolver = new TwitchSubOnlyVodResolver(new MemoryLogger(), httpClient, tempDir);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best")));
        Assert.Contains("No playable qualities", error.Message);
    }),
    ("sub-only VOD resolver rejects a non-numeric VOD id", async () =>
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("HTTP must not be called."));
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(handler);
        var resolver = new TwitchSubOnlyVodResolver(new MemoryLogger(), httpClient, tempDir);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new TwitchSubOnlyVodRequest("not-a-vod", "best")));
    }),
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `SVS_TEST_FILTER="sub-only VOD resolver" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: build error (`ITwitchSubOnlyVodResolver` / `TwitchSubOnlyVodResolver` do not exist).

- [ ] **Step 3: Create the Core interface**

Create `src/StreamlinkVlcStudio.Core/Services/ITwitchSubOnlyVodResolver.cs`:

```csharp
namespace StreamlinkVlcStudio.Core.Services;

public interface ITwitchSubOnlyVodResolver
{
    Task<TwitchSubOnlyVodResolution> ResolveAsync(
        TwitchSubOnlyVodRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TwitchSubOnlyVodRequest(string VodId, string Quality);

public sealed record TwitchSubOnlyVodResolution(Uri PlaybackUri, string QualityKey, string Message);
```

- [ ] **Step 4: Create the Infrastructure resolver**

Create `src/StreamlinkVlcStudio.Infrastructure/Twitch/TwitchSubOnlyVodResolver.cs`:

```csharp
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>
/// Resolves subscriber-only Twitch VODs to local HLS playlists by deriving the public
/// CloudFront segment URLs from the VOD's storyboard metadata — the same technique as the
/// TwitchNoSub browser extension (https://github.com/besuper/TwitchNoSub), reimplemented
/// for desktop playback. Used only as a fallback when Streamlink cannot resolve the VOD.
/// </summary>
public sealed partial class TwitchSubOnlyVodResolver : ITwitchSubOnlyVodResolver
{
    private const string TwitchGraphQlEndpoint = "https://gql.twitch.tv/gql";
    // Public Twitch web Client-ID, the same one ReplayResolver uses for archive lookups.
    private const string TwitchPublicClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const int PlaylistProbeByteLimit = 65535;
    private static readonly TimeSpan StalePlaylistAge = TimeSpan.FromHours(24);
    // TwitchNoSub compares createdAt against this frozen cutoff instead of "now":
    // newer uploads do not expose the index-dvr layout, so they use the archive URL
    // shape (which simply yields no variants and a clean error).
    private static readonly DateTimeOffset TwitchUploadLayoutCutoff = new(2023, 2, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly string DefaultPlaylistDirectory = Path.Combine(
        Path.GetTempPath(),
        "StreamlinkVlcStudio",
        "sub-only-vods");

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly string playlistDirectory;

    public TwitchSubOnlyVodResolver(IAppLogger logger)
        : this(logger, SharedHttpClient, DefaultPlaylistDirectory)
    {
    }

    public TwitchSubOnlyVodResolver(IAppLogger logger, HttpClient httpClient, string playlistDirectory)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.playlistDirectory = playlistDirectory;
        SweepStalePlaylists();
    }

    public async Task<TwitchSubOnlyVodResolution> ResolveAsync(
        TwitchSubOnlyVodRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vodId = (request.VodId ?? "").Trim();
        if (!TwitchVodIdPattern().IsMatch(vodId))
        {
            throw new InvalidOperationException($"'{request.VodId}' is not a valid Twitch VOD id.");
        }

        var metadata = await FetchVideoMetadataAsync(vodId, cancellationToken).ConfigureAwait(false);
        if (!TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(metadata.SeekPreviewsUrl, out var host, out var specialId))
        {
            throw new InvalidOperationException(
                $"Could not derive the direct playlist location for VOD {vodId} from its storyboard URL.");
        }

        var candidates = new List<(string Key, string Url)>();
        foreach (var qualityKey in TwitchSubOnlyVodPlaylist.QualityKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl(
                metadata.BroadcastType,
                metadata.CreatedAtUtc,
                TwitchUploadLayoutCutoff,
                host,
                specialId,
                metadata.OwnerLogin,
                vodId,
                qualityKey);
            if (await ProbeVariantAsync(url, cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((qualityKey, url));
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No playable qualities were found for VOD {vodId}. Recently uploaded VODs are not supported by the sub-only fallback.");
        }

        var selectedKey = TwitchSubOnlyVodPlaylist.SelectQualityKey(
            candidates.Select(candidate => candidate.Key).ToArray(),
            request.Quality);
        var playlistUrl = candidates.First(candidate => candidate.Key == selectedKey).Url;
        var playlistContent = await FetchStringAsync(playlistUrl, ranged: false, cancellationToken).ConfigureAwait(false);
        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(playlistContent, new Uri(playlistUrl));

        Directory.CreateDirectory(playlistDirectory);
        var playlistPath = Path.Combine(playlistDirectory, $"{vodId}-{selectedKey}.m3u8");
        await File.WriteAllTextAsync(playlistPath, rewritten, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        logger.Write(
            AppLogLevel.Info,
            "SubOnlyVod",
            $"Resolved sub-only VOD {vodId} via direct CloudFront playlist ({selectedKey}): {playlistUrl}");
        return new TwitchSubOnlyVodResolution(
            new Uri(playlistPath),
            selectedKey,
            $"Resolved sub-only VOD via direct CloudFront playlist ({selectedKey}).");
    }

    private async Task<TwitchVideoMetadata> FetchVideoMetadataAsync(string vodId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TwitchGraphQlEndpoint);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("Client-Id", TwitchPublicClientId);
        request.Headers.TryAddWithoutValidation("X-Device-Id", CreateDeviceId());
        request.Content = new StringContent(BuildVideoQueryPayload(vodId), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Twitch GraphQL returned {(int)response.StatusCode} {response.ReasonPhrase} for VOD {vodId}.");
        }

        using var document = JsonDocument.Parse(body);
        var graphQlError = ExtractGraphQlError(document.RootElement);
        if (!string.IsNullOrWhiteSpace(graphQlError))
        {
            throw new InvalidOperationException($"Twitch GraphQL rejected the VOD lookup: {graphQlError}");
        }

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("video", out var video) ||
            video.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"VOD {vodId} was not found or is not public.");
        }

        return new TwitchVideoMetadata(
            GetOptionalString(video, "broadcastType"),
            TryGetDateTimeOffset(video, "createdAt") ?? DateTimeOffset.MinValue,
            GetOptionalString(video, "seekPreviewsURL"),
            TryReadNestedString(video, "owner", "login"));
    }

    private async Task<bool> ProbeVariantAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var content = await FetchStringAsync(url, ranged: true, cancellationToken).ConfigureAwait(false);
            return content.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<string> FetchStringAsync(string url, bool ranged, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ranged)
        {
            request.Headers.Range = new RangeHeaderValue(0, PlaylistProbeByteLimit);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {url} returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SweepStalePlaylists()
    {
        try
        {
            if (!Directory.Exists(playlistDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - StalePlaylistAge;
            foreach (var path in Directory.EnumerateFiles(playlistDirectory, "*.m3u8"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Write(AppLogLevel.Warning, "SubOnlyVod", $"Could not delete stale sub-only VOD playlist '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Write(AppLogLevel.Warning, "SubOnlyVod", $"Could not sweep stale sub-only VOD playlists: {ex.Message}");
        }
    }

    private static string BuildVideoQueryPayload(string vodId)
    {
        var payload = new
        {
            query = $"query {{ video(id: \"{vodId}\") {{ broadcastType, createdAt, seekPreviewsURL, owner {{ login }} }} }}"
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string ExtractGraphQlError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array)
        {
            return errors
                .EnumerateArray()
                .Select(error => GetOptionalString(error, "message"))
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "";
        }

        return "";
    }

    private static string CreateDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchVodIdPattern();

    private sealed record TwitchVideoMetadata(
        string BroadcastType,
        DateTimeOffset CreatedAtUtc,
        string SeekPreviewsUrl,
        string OwnerLogin);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `SVS_TEST_FILTER="sub-only" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: all Task 1 + Task 2 tests PASS.

---

### Task 3: Playback fallback in `StreamTabViewModel` + DI wiring

**Files:**
- Modify: `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamTabViewModel.cs` (ctor at 271-302, `StartAsync` VOD branch at 1152-1162, plus one new helper method)
- Modify: `src/StreamlinkVlcStudio.App.Wpf/ViewModels/MainViewModel.cs` (ctor at 165-205, `CreateTab` at 3944-3962)
- Modify: `src/StreamlinkVlcStudio.App.Wpf/MainWindow.xaml.cs` (composition root at 793-812)
- Test: `tests/StreamlinkVlcStudio.Tests/Program.cs` (registry entries + new fake at the bottom)

**Interfaces:**
- Consumes: `ITwitchSubOnlyVodResolver`, `TwitchSubOnlyVodRequest`, `TwitchSubOnlyVodResolution` (Task 2); `TwitchSubOnlyVodResolver` (Task 2); `StreamTargetKind` (`StreamlinkVlcStudio.Core.Models`).
- Produces: `StreamTabViewModel` ctor gains trailing optional param `ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver = null`; `MainViewModel` ctor gains trailing optional param of the same type. Existing call sites are unaffected (optional params).

- [ ] **Step 1: Write the failing tests**

Add these registry entries:

```csharp
    ("Twitch VOD playback falls back to the sub-only resolver when Streamlink fails", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("error: This video is only available to subscribers")
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var bypassUri = new Uri(@"C:\fake\sub-only-123.m3u8");
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
        {
            Override = (_, _) => Task.FromResult(new TwitchSubOnlyVodResolution(bypassUri, "720p60", "Resolved."))
        };
        var tab = new StreamTabViewModel(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", StreamTargetKind.TwitchVod, "123"),
            "720p60",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(1, subOnlyResolver.Requests.Count);
        Assert.Equal("123", subOnlyResolver.Requests[0].VodId);
        Assert.Equal("720p60", subOnlyResolver.Requests[0].Quality);
        Assert.Equal(bypassUri, playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        await tab.DisposeAsync();
    }),
    ("Kick VOD playback does not use the sub-only resolver", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("streamlink failed")
        };
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver();
        var tab = new StreamTabViewModel(
            new StreamTarget(PlatformKind.Kick, "streamer", "https://kick.com/streamer/videos/abc", StreamTargetKind.KickVod, "abc"),
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(0, subOnlyResolver.Requests.Count);
        Assert.Equal(PlaybackStatus.Error, tab.Status);
        Assert.Contains("streamlink failed", tab.ErrorMessage);
        await tab.DisposeAsync();
    }),
    ("sub-only VOD fallback error includes Streamlink and fallback messages", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("streamlink says no")
        };
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
        {
            Override = (_, _) => throw new InvalidOperationException("no qualities")
        };
        var tab = new StreamTabViewModel(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", StreamTargetKind.TwitchVod, "123"),
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(PlaybackStatus.Error, tab.Status);
        Assert.Contains("streamlink says no", tab.ErrorMessage);
        Assert.Contains("no qualities", tab.ErrorMessage);
        await tab.DisposeAsync();
    }),
    ("sub-only VOD fallback is not used for cancellations", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new OperationCanceledException()
        };
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver();
        var tab = new StreamTabViewModel(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", StreamTargetKind.TwitchVod, "123"),
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(0, subOnlyResolver.Requests.Count);
        Assert.Equal(PlaybackStatus.Error, tab.Status);
        await tab.DisposeAsync();
    }),
```

Add this fake at the bottom of `tests/StreamlinkVlcStudio.Tests/Program.cs`, next to the other fakes (e.g. after `FakeStreamlinkService` ends at line 31733):

```csharp
internal sealed class FakeTwitchSubOnlyVodResolver : ITwitchSubOnlyVodResolver
{
    public List<TwitchSubOnlyVodRequest> Requests { get; } = [];
    public Func<TwitchSubOnlyVodRequest, CancellationToken, Task<TwitchSubOnlyVodResolution>>? Override { get; set; }

    public Task<TwitchSubOnlyVodResolution> ResolveAsync(
        TwitchSubOnlyVodRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Override?.Invoke(request, cancellationToken) ??
            Task.FromResult(new TwitchSubOnlyVodResolution(new Uri(@"C:\fake\sub-only.m3u8"), "chunked", "Resolved."));
    }
}
```

Note on the cancellation test: `StartAsync`'s `catch (OperationCanceledException)` filter requires the start token to be cancelled; an uncancelled `OperationCanceledException` from streamlink falls into the generic `catch (Exception)` and sets `PlaybackStatus.Error`. The important assertion is that the resolver was NOT called.

- [ ] **Step 2: Run tests to verify they fail**

Run: `SVS_TEST_FILTER="sub-only" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: build error — `twitchSubOnlyVodResolver` is not a parameter of `StreamTabViewModel`.

- [ ] **Step 3: Modify `StreamTabViewModel`**

In `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamTabViewModel.cs`:

1. Add the field near the other service fields (e.g. next to `kickEventSubscriptionService`):

```csharp
    private readonly ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver;
```

2. Add the ctor parameter at the very end of the parameter list (after `IKickEventSubscriptionService? kickEventSubscriptionService = null` at line 285):

```csharp
        IKickEventSubscriptionService? kickEventSubscriptionService = null,
        ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver = null)
```

and the assignment in the ctor body (after `this.kickEventSubscriptionService = kickEventSubscriptionService;` at line 298):

```csharp
        this.twitchSubOnlyVodResolver = twitchSubOnlyVodResolver;
```

3. Replace the VOD branch in `StartAsync` (lines 1152-1162):

```csharp
                case StreamTargetKind.TwitchVod:
                    var twitchVodRequest = new StreamTransportRequest(
                        Target,
                        Quality,
                        settings.StreamlinkPath!,
                        false,
                        customArguments);
                    try
                    {
                        var resolved = await streamlinkService.ResolveStreamUrlAsync(twitchVodRequest, startCancellationToken);
                        directPlaybackUri = resolved.StreamUri;
                    }
                    catch (Exception streamlinkError) when (streamlinkError is not OperationCanceledException &&
                        twitchSubOnlyVodResolver is not null)
                    {
                        logger.Write(
                            AppLogLevel.Info,
                            "Playback",
                            $"Streamlink could not resolve {Target.Url} ({streamlinkError.Message}); trying the sub-only VOD fallback.");
                        try
                        {
                            var bypass = await twitchSubOnlyVodResolver.ResolveAsync(
                                new TwitchSubOnlyVodRequest(ResolveTwitchVodId(), Quality),
                                startCancellationToken);
                            directPlaybackUri = bypass.PlaybackUri;
                            AddSystemMessage($"Playing sub-only VOD via direct playlist ({bypass.QualityKey}).");
                        }
                        catch (Exception bypassError) when (bypassError is not OperationCanceledException)
                        {
                            throw new InvalidOperationException(
                                $"Streamlink could not play the VOD: {streamlinkError.Message} Sub-only fallback also failed: {bypassError.Message}");
                        }
                    }

                    break;
                case StreamTargetKind.KickVod:
                    var kickVodRequest = new StreamTransportRequest(
                        Target,
                        Quality,
                        settings.StreamlinkPath!,
                        false,
                        customArguments);
                    var kickResolved = await streamlinkService.ResolveStreamUrlAsync(kickVodRequest, startCancellationToken);
                    directPlaybackUri = kickResolved.StreamUri;
                    break;
```

4. Add the helper method (place it right after `StartAsync`'s closing brace at line 1312):

```csharp
    private string ResolveTwitchVodId()
    {
        if (!string.IsNullOrWhiteSpace(Target.MediaId))
        {
            return Target.MediaId.Trim();
        }

        if (Uri.TryCreate(Target.Url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                return segments[^1];
            }
        }

        return "";
    }
```

(`Core.Services` is already imported in this file for `IStreamlinkService` etc.; verify `ITwitchSubOnlyVodResolver` resolves without adding a using.)

- [ ] **Step 4: Wire DI through `MainViewModel` and `MainWindow`**

In `src/StreamlinkVlcStudio.App.Wpf/ViewModels/MainViewModel.cs`:

1. Add the field (near `private readonly IKickEventSubscriptionService? kickEventSubscriptionService;`):

```csharp
    private readonly ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver;
```

2. Add the ctor parameter at the very end (after `ILiveNotificationService? liveNotificationService = null` at line 189):

```csharp
        ILiveNotificationService? liveNotificationService = null,
        ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver = null)
```

and the assignment in the ctor body (with the other assignments, after `this.kickEventSubscriptionService = kickEventSubscriptionService;`):

```csharp
        this.twitchSubOnlyVodResolver = twitchSubOnlyVodResolver;
```

3. In `CreateTab` (line 3944), pass it to `StreamTabViewModel` after the existing named argument:

```csharp
            kickChatHistoryProvider: kickChatHistoryProvider,
            kickEventSubscriptionService: kickEventSubscriptionService,
            twitchSubOnlyVodResolver: twitchSubOnlyVodResolver);
```

In `src/StreamlinkVlcStudio.App.Wpf/MainWindow.xaml.cs`:

1. Ensure `using StreamlinkVlcStudio.Infrastructure.Twitch;` is present at the top (add it next to the other `StreamlinkVlcStudio.Infrastructure.*` usings).
2. In `InitializeMainWindowAsync`, after `var twitchVodService = new TwitchVodService(logger);` (line 783) add:

```csharp
        var twitchSubOnlyVodResolver = new TwitchSubOnlyVodResolver(logger);
```

3. In the `viewModel = new MainViewModel(...)` call, add a named argument after `liveNotificationService: liveNotificationService` (line 811):

```csharp
            liveNotificationService: liveNotificationService,
            twitchSubOnlyVodResolver: twitchSubOnlyVodResolver);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `SVS_TEST_FILTER="sub-only" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: all new tests PASS. Then run the full suite: `dotnet test` — no regressions.

---

### Task 4: Paste support for `twitch.tv/videos/{id}` (parser + search)

**Files:**
- Modify: `src/StreamlinkVlcStudio.Core/Parsing/StreamInputParser.cs`
- Modify: `src/StreamlinkVlcStudio.App.Wpf/ViewModels/MainViewModel.cs` (`SearchStreamCandidatesAsync` at 3445, `LoadStreamSearchResultMetadataAsync` at 3501, `LoadStreamSearchResultViewerCountsAsync` at 3537)
- Test: `tests/StreamlinkVlcStudio.Tests/Program.cs` (modify entry at 124-131, add new entries)

**Interfaces:**
- Consumes: `StreamTarget`, `StreamTargetKind` (Core.Models); `StreamCandidateProbe` record (MainViewModel.cs:6499).
- Produces: `StreamInputParser.TryParseTwitchVodUrl(string input, out StreamTarget? target)`. `Parse`, `ParseCandidates`, `TryParsePlatformUrl` now return a `StreamTargetKind.TwitchVod` target for Twitch VOD URLs. VOD targets built by the parser have `Channel = vodId`, `MediaId = vodId`, `DisplayTitle = "VOD {vodId}"`.

- [ ] **Step 1: Write the failing tests**

First modify the existing entry at lines 124-131 — `TryParsePlatformUrl("https://www.twitch.tv/videos/123456", ...)` is now `true`, so remove the two videos lines. The entry becomes:

```csharp
    ("ignores browser Twitch non-channel URL", () =>
    {
        Assert.Equal(false, StreamInputParser.TryParsePlatformUrl("https://www.twitch.tv/login", out var target));
        Assert.Equal(false, StreamInputParser.TryParsePlatformUrl("https://kick.com/register", out target));
        return Task.CompletedTask;
    }),
```

(The following entry at lines 132-137 asserting `@videos` rejection stays unchanged — `@videos` must keep being rejected.)

Then add these registry entries:

```csharp
    ("parses Twitch VOD URL as a VOD target", () =>
    {
        Assert.True(StreamInputParser.TryParsePlatformUrl("https://www.twitch.tv/videos/123456?t=1h2m", out var target));
        Assert.NotNull(target);
        Assert.Equal(StreamTargetKind.TwitchVod, target!.Kind);
        Assert.Equal(PlatformKind.Twitch, target.Platform);
        Assert.Equal("123456", target.MediaId);
        Assert.Equal("https://www.twitch.tv/videos/123456", target.Url);

        var parsed = StreamInputParser.Parse("twitch.tv/videos/654321", PlatformKind.Kick);
        Assert.Equal(StreamTargetKind.TwitchVod, parsed.Kind);
        Assert.Equal("654321", parsed.MediaId);

        var candidates = StreamInputParser.ParseCandidates("https://www.twitch.tv/videos/123456");
        Assert.Equal(1, candidates.Count);
        Assert.Equal(StreamTargetKind.TwitchVod, candidates[0].Kind);

        Assert.True(StreamInputParser.TryParseTwitchVodUrl("https://www.twitch.tv/videos/123456", out _));
        Assert.Equal(false, StreamInputParser.TryParseTwitchVodUrl("https://www.twitch.tv/xqc", out _));
        Assert.Equal(false, StreamInputParser.TryParseTwitchVodUrl("https://www.twitch.tv/@videos/123456", out _));
        Assert.Equal(false, StreamInputParser.TryParseTwitchVodUrl("xqc", out _));
        return Task.CompletedTask;
    }),
    ("home search opens a pasted Twitch VOD URL as a playable VOD result", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewerCountService = new FakeViewerCountService();
        var viewModel = new MainViewModel(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            viewerCountService: viewerCountService,
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "https://www.twitch.tv/videos/123456";

        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults.Count == 1,
            TimeSpan.FromMilliseconds(500));

        var result = viewModel.StreamSearchResults[0];
        Assert.Equal(StreamTargetKind.TwitchVod, result.Target.Kind);
        Assert.Equal("123456", result.Target.MediaId);
        Assert.Equal(true, result.CanPlay);
        Assert.Equal("Twitch VOD", result.StatusText);
        Assert.Equal(0, viewerCountService.CallCount);
    }),
```

(`TestWait.UntilAsync`, `FakeViewerCountService`, `FakeSettingsService`, `FakeStreamlinkService`, `FakePlaybackEngineFactory`, `FakeChatClientFactory`, `MemoryLogger` all already exist in the test file — see lines 30947-32517.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `SVS_TEST_FILTER="VOD" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: the two new tests FAIL (and the edited old test fails if the parser is unchanged but the edit was applied — make the test edits first, then confirm failure).

- [ ] **Step 3: Modify `StreamInputParser`**

In `src/StreamlinkVlcStudio.Core/Parsing/StreamInputParser.cs`:

1. In `ParseInput`, insert the VOD branch right before the final `return new ParsedInput(platform, NormalizeChannel(segments[0]));` (line 138):

```csharp
        if (platform == PlatformKind.Twitch &&
            segments.Length == 2 &&
            segments[0].Equals("videos", StringComparison.OrdinalIgnoreCase) &&
            TwitchVodIdPattern().IsMatch(segments[1]))
        {
            return new ParsedInput(platform, "", segments[1]);
        }

```

2. Change the `ParsedInput` record (line 226) to carry the VOD id:

```csharp
    private sealed record ParsedInput(PlatformKind? Platform, string Channel, string? VodId = null);
```

3. Update the three public entry points and add the two helpers:

`ParseCandidates` (line 45) — insert at the top of the method:

```csharp
        var parsed = ParseInput(input);
        if (parsed.VodId is not null)
        {
            return [FromTwitchVod(parsed.VodId)];
        }

```

`Parse` (line 66) — replace the body:

```csharp
    public static StreamTarget Parse(string input, PlatformKind defaultPlatform)
    {
        var parsed = ParseInput(input);
        return parsed.VodId is not null
            ? FromTwitchVod(parsed.VodId)
            : FromChannel(parsed.Platform ?? defaultPlatform, parsed.Channel);
    }
```

`TryParsePlatformUrl` (line 72) — insert right after `var parsed = ParseInput(input);`:

```csharp
            if (parsed.VodId is not null)
            {
                target = FromTwitchVod(parsed.VodId);
                return true;
            }

```

Add the new public helper and the factory + regex (place them right after `TryParsePlatformUrl`):

```csharp
    public static bool TryParseTwitchVodUrl(string input, out StreamTarget? target)
    {
        target = null;

        try
        {
            var parsed = ParseInput(input);
            if (parsed.VodId is null)
            {
                return false;
            }

            target = FromTwitchVod(parsed.VodId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static StreamTarget FromTwitchVod(string vodId)
    {
        return new StreamTarget(
            PlatformKind.Twitch,
            vodId,
            $"https://www.twitch.tv/videos/{vodId}",
            StreamTargetKind.TwitchVod,
            vodId,
            $"VOD {vodId}");
    }
```

and next to the other `GeneratedRegex` declarations at the bottom (line 220-224):

```csharp
    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchVodIdPattern();
```

- [ ] **Step 4: Modify `MainViewModel` search flow**

In `src/StreamlinkVlcStudio.App.Wpf/ViewModels/MainViewModel.cs`:

1. `SearchStreamCandidatesAsync` (line 3445) — insert at the very top of the method, before the `streamSearchService` check:

```csharp
        if (StreamInputParser.TryParseTwitchVodUrl(query, out var vodTarget) && vodTarget is not null)
        {
            return [new StreamCandidateProbe(vodTarget, new StreamlinkProbeResult(true, "Twitch VOD"))];
        }

```

2. `LoadStreamSearchResultMetadataAsync(StreamCandidateProbe probe, ...)` (line 3501) — change the first guard so VOD targets are not sent through the live-metadata lookup:

```csharp
        if (probe.Channel is not null || probe.Target.Kind != StreamTargetKind.Live)
        {
            return probe;
        }
```

3. `LoadStreamSearchResultViewerCountsAsync` (line 3537) — guard both spots so VOD targets are not sent through viewer-count lookups. Change the early-out condition (line 3541-3542) to:

```csharp
        if (viewerCountService is null ||
            !probes.Any(probe => IsLiveStreamSearchProbe(probe) && probe.Target.Kind == StreamTargetKind.Live && probe.ViewerCount is null))
        {
            return probes;
        }
```

and the per-probe check (line 3550) to:

```csharp
            if (!IsLiveStreamSearchProbe(probe) || probe.Target.Kind != StreamTargetKind.Live || probe.ViewerCount is not null)
            {
                return probe;
            }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `SVS_TEST_FILTER="VOD" dotnet test tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj`
Expected: new tests PASS. Then run the full suite: `dotnet test` — no regressions (pay special attention to the parser tests near lines 42-140 and the search tests near lines 21960-22204).

---

### Task 5: README honesty update + full validation

**Files:**
- Modify: `README.md` (line 150 area + feature list)

**Interfaces:**
- Consumes: all previous tasks.
- Produces: updated docs.

- [ ] **Step 1: Update the no-bypass statement**

Read `README.md` around line 150. The sentence currently reads: "The app does not bypass ads, DRM, geo restrictions, age gates, account checks, or platform permissions." That is no longer accurate. Replace it with wording along these lines (match the surrounding doc's voice):

```markdown
The app does not bypass ads, DRM, geo restrictions, or age gates. One exception:
subscriber-only Twitch VODs can be played without a subscription — the app derives
the public CloudFront playlist from the VOD's storyboard metadata, the same
technique as the [TwitchNoSub](https://github.com/besuper/TwitchNoSub) browser
extension (reimplemented in C#, no code copied).
```

Also add a short feature bullet where the other features are listed (near the replay/VOD bullets):

```markdown
- Subscriber-only Twitch VOD playback: if Streamlink cannot resolve a Twitch VOD, the app falls back to a direct CloudFront playlist derived from the VOD's public storyboard metadata (TwitchNoSub technique). Pasting a `https://www.twitch.tv/videos/{id}` URL into the search box opens it directly.
```

- [ ] **Step 2: Full validation**

Run, in order:

1. `dotnet build StreamlinkVlcStudio.sln` — succeeds with no new warnings introduced by these changes.
2. `dotnet test` — the whole suite passes.
3. `node --test browser-extension/tests/content-core.test.js` — unchanged extension tests still pass (nothing in `browser-extension/` was modified; run to confirm).

- [ ] **Step 3: Report**

Summarize to the user: what was implemented, where, test results, and the intentional behavior changes (parser now accepts `/videos/{id}` URLs; README no-bypass statement narrowed). Remind the user that changes are uncommitted in the working tree (no git mutations were performed).
