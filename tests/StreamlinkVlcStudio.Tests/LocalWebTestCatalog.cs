using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("local web: LAN policy rejects public, other-subnet, mapped and special addresses", NetworkAsync),
        ("local web: password attempts have per-device and global budgets", LoginBudgetAsync),
        ("local web: device sessions expire and cannot move to another IP", SessionsAsync),
        ("local web: account storage persists hashes and isolates preferences", UserAccountsAsync),
        ("local web: login pipeline enforces origin, host, peer, cookies and CSRF", LoginPipelineAsync),
        ("local web: desktop browser launch opens the sign-in page without credentials", DesktopLaunchAsync),
        ("local web: server window follows every app theme while open", ServerWindowThemeAsync),
        ("local web: LAN CA signs an IP certificate trusted over real HTTPS", CertificateChainAsync),
        ("local web: real HTTPS rejects unauthenticated media and hostile origins", HttpsAsync),
        ("local web: disconnect all revokes media and commands", DisconnectAsync),
        ("local web: concurrent disposal waits for the same session and server cleanup", SharedDisposalAsync),
        ("local web: network changes fail closed and stop releases the listener", NetworkChangeAsync),
        ("local web: HLS rewrites variants, segments, keys and initialization URLs", PlaylistAsync),
        ("local web: media refuses private destinations and unsafe redirects", MediaSecurityAsync),
        ("local web: media proxy rewrites remote manifests and preserves byte ranges without upstream cookies", MediaProxyAsync),
        ("local web: native HLS range probe never turns an extensionless playlist into a partial manifest", MediaPartialPlaylistAsync),
        ("local web: offline media IDs cannot escape the validated file inventory", OfflineMediaAsync),
        ("local web: real workspace shares VOD history and isolates players", WorkspaceAsync),
        ("local web: offline last-live history preserves verified timestamps without blocking commands", FollowingLastLiveAsync),
        ("local web: offline last-live browser updates cards and sidebar without losing focus", BrowserFollowingLastLiveAsync),
        ("local web: category viewer totals are validated, bounded, cancellable and independent of commands", CategoryViewersAsync),
        ("local web: category artwork rejects Twitch missing covers and recovers after refresh", CategoryArtworkAsync),
        ("local web: category artwork validates bytes when Twitch mislabels content types", CategoryArtworkFormatsAsync),
        ("local web: category artwork bounds caching and cancels queued downloads", CategoryArtworkResourcesAsync),
        ("local web: category artwork browser distinguishes pending missing valid and recovered covers", BrowserCategoryArtworkAsync),
        .. LiveCategoryArtworkTests,
        ("local web: follow status is verified, read-only, nonblocking and survives late responses", FollowStatusAsync),
        ("local web: profile pictures survive library, playback, history and cached image responses", ProfileImagesAsync),
        ("local web: channel profiles include real metadata and panels with safe public links", ChannelProfileAsync),
        ("local web: panel-only profiles preserve the actual introduction after sponsor panels", PanelOnlyChannelProfileAsync),
        ("local web: provider badges and animated emotes resolve without exposing host paths", ChatPresentationAsync),
        ("local web: failed Twitch badge catalogs recover in quiet live chat without a version change", TwitchBadgeRecoveryAsync),
        ("local web: Kick third-party animations use broadcaster identity and stay scoped", KickWebEmotesAsync),
        ("local web: real browser authenticates, plays video and renders hostile metadata safely", BrowserAsync),
        ("local web: Twitch and Kick VOD chat follows playback across pages, pauses and seeks", BrowserVodChatAsync),
        ("local web: pasted Kick VOD metadata preserves the broadcast clock for chat", KickVodChatMetadataAsync),
        .. LiveVodChatTests,
        (BrowserVolumeTestName, BrowserVolumeAsync),
        ("local web: real browser signs in, resumes its session and signs out", BrowserDesktopSignInAsync),
        ("local web: overlay chat stays usable over video in small players and fullscreen", BrowserOverlayAsync),
        ("local web: per-stream overlay layouts survive settings and workspace restarts", OverlayLayoutAsync),
        ("local web: browser filters, navigation, settings and mobile layouts remain usable", BrowserExperienceAsync)
    ];

    private static Task NetworkAsync()
    {
        var network = new LocalWebInterface("test", "LAN", IPAddress.Parse("192.168.10.5"), 24);
        foreach (var address in new[] { "192.168.10.6", "::ffff:192.168.10.6" }) Assert.True(network.Allows(IPAddress.Parse(address)));
        foreach (var address in new[] { "192.168.11.5", "10.0.0.5", "172.16.0.4", "8.8.8.8", "127.0.0.1", "::1", "fe80::1", "192.168.10.0", "192.168.10.255", "169.254.0.5", "::ffff:8.8.8.8" })
            Assert.Equal(false, network.Allows(IPAddress.Parse(address)));
        Assert.Equal(false, new LocalWebInterface("test", "public", IPAddress.Parse("8.8.8.8"), 24).Allows(IPAddress.Parse("8.8.8.9")));
        Assert.Equal(false, (network with { PrefixLength = 0 }).Allows(IPAddress.Parse("192.168.10.6")));
        Assert.Equal(false, network.Allows(null));
        return Task.CompletedTask;
    }

    private static Task LoginBudgetAsync()
    {
        var time = DateTimeOffset.UtcNow;
        var auth = new LocalWebAuthentication(() => time);
        var peer = IPAddress.Parse("192.168.1.2");
        for (var attempt = 0; attempt < 5; attempt++) Assert.True(auth.TryConsumeLoginAttempt(peer));
        Assert.Equal(false, auth.TryConsumeLoginAttempt(peer));
        time += TimeSpan.FromMinutes(5);
        Assert.True(auth.TryConsumeLoginAttempt(peer));
        for (var i = 3; i <= 31; i++) Assert.True(auth.TryConsumeLoginAttempt(IPAddress.Parse($"192.168.1.{i}")));
        Assert.Equal(false, auth.TryConsumeLoginAttempt(IPAddress.Parse("192.168.1.100")));
        return Task.CompletedTask;
    }

    private static async Task SessionsAsync()
    {
        var time = DateTimeOffset.UtcNow;
        var auth = new LocalWebAuthentication(() => time);
        var peer = IPAddress.Parse("192.168.1.2");
        var workspace = new TestWorkspace();
        await using var session = new LocalWebSession(peer, "user", "tester", "revision", workspace, time);
        auth.Sessions.Add(session.Token, session);
        Assert.True(auth.Find(session.Token, peer) == session);
        Assert.True(auth.Find(session.Token, IPAddress.Parse("192.168.1.3")) is null);
        time += TimeSpan.FromMinutes(31);
        Assert.True(auth.Find(session.Token, peer) is null);
        session.LastSeen = time;
        time += TimeSpan.FromHours(8);
        session.LastSeen = time;
        Assert.True(auth.Find(session.Token, peer) is null);
        await session.DisposeAsync();
        Assert.True(workspace.Disposed);
    }

    internal static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=StreamStudio Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel needs an imported key's provider information. Default key lifetime deletes it on disposal.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }

    private static HttpClient Client(X509Certificate2 certificate, Uri address) => new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        UseProxy = false,
        ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented is not null &&
            presented.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256)
    })
    { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };

    private const string TestUsername = "webtester";
    private const string TestPassword = "Test web password 123!";
    private static async Task<LocalWebServer> Server(X509Certificate2 certificate, Func<ILocalWebWorkspace>? factory = null, Func<bool>? current = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "web-users-" + Guid.NewGuid().ToString("N") + ".json");
        var users = await LocalWebUserStore.OpenAsync(path);
        await users.AddAsync(TestUsername, TestPassword);
        return await LocalWebServer.StartCoreAsync(new("test", "loopback fixture", IPAddress.Loopback, 8), certificate, 0,
            factory ?? (() => new TestWorkspace()), users, current ?? (() => true));
    }

    private static async Task CertificateChainAsync()
    {
        using var pair = LocalWebCertificate.CreateTemporary(IPAddress.Loopback);
        Assert.Equal(false, pair.Authority.HasPrivateKey);
        Assert.True(pair.Server.HasPrivateKey);
        Assert.Equal(pair.Authority.Subject, pair.Server.Issuer);
        Assert.True(pair.Authority.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal(false, pair.Server.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.True(pair.Server.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single().KeyIdentifier is not null);
        Assert.True(pair.Server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
            .EnumerateIPAddresses().Contains(IPAddress.Loopback));

        await using var server = await Server(pair.Server);
        using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(pair.Authority);
        using var client = new HttpClient(handler) { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string action, object body, string? cookie = null, string? csrf = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/" + action) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", origin ?? client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-StreamStudio", "1");
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<(string Cookie, string Csrf)> Login(HttpClient client, string username = TestUsername, string password = TestPassword)
    {
        using var login = await Post(client, "login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", cookie.ToLowerInvariant()); Assert.Contains("httponly", cookie.ToLowerInvariant()); Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(false, json.ToString().Contains(password, StringComparison.Ordinal));
        return (cookie.Split(';')[0], json.GetProperty("csrf").GetString()!);
    }

    private static async Task HttpsAsync()
    {
        using var certificate = Certificate();
        await using var server = await Server(certificate);
        using var http = Client(certificate, server.Address);
        using var page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(false, page.Headers.Contains("Server"));
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        foreach (var path in new[] { "/api/session", "/media/private/key", "/chat-assets/private", "/category-artwork/private" })
        { using var denied = await http.GetAsync(path); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        using (var wrongOrigin = await Post(http, "login", new { username = TestUsername, password = TestPassword }, origin: "https://attacker.example")) Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        using (var host = new HttpRequestMessage(HttpMethod.Get, "/"))
        { host.Headers.Host = "attacker.example"; using var denied = await http.SendAsync(host); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var query = await http.GetAsync("/?password=" + TestPassword)) Assert.Equal(HttpStatusCode.Forbidden, query.StatusCode);
        var auth = await Login(http);
        using (var missingCsrf = await Post(http, "settings", new { }, auth.Cookie)) Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        using (var valid = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf)) Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using (var origin = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf, "https://attacker.example")) Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        using (var tooLarge = await Post(http, "settings", new { large = new string('x', 20_000) }, auth.Cookie, auth.Csrf))
            Assert.True(tooLarge.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
        using (var media = new HttpRequestMessage(HttpMethod.Get, "/media/private/key"))
        {
            media.Headers.Add("Cookie", auth.Cookie);
            using var response = await http.SendAsync(media);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("protected-media", await response.Content.ReadAsStringAsync());
        }
        using (var logout = await Post(http, "logout", new { }, auth.Cookie, auth.Csrf)) Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using (var denied = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    private static async Task DisconnectAsync()
    {
        using var certificate = Certificate();
        var workspaces = new List<TestWorkspace>();
        await using var server = await Server(certificate, () => { var workspace = new TestWorkspace(); workspaces.Add(workspace); return workspace; });
        using var http = Client(certificate, server.Address);
        var auth = await Login(http);
        await server.DisconnectAllAsync();
        Assert.True(workspaces.Single().Disposed);
        using (var old = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        await Login(http);
    }

    private static async Task NetworkChangeAsync()
    {
        using var certificate = Certificate();
        var current = true;
        var server = await Server(certificate, current: () => current);
        using var http = Client(certificate, server.Address);
        try
        {
            var auth = await Login(http);
            current = false;
            using var denied = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        finally { await server.DisposeAsync(); }
        var failed = false;
        try { using var response = await http.GetAsync("/"); } catch (HttpRequestException) { failed = true; }
        Assert.True(failed);
    }

    private static Task PlaylistAsync()
    {
        using var media = new LocalWebMedia("playback", new Uri("https://cdn.example/manifest.m3u8?secret=signed"));
        var rewritten = media.RewritePlaylist("""
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",URI="audio.m3u8?secret=key"
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example/secret.key"
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="100@0"
            #EXT-X-STREAM-INF:BANDWIDTH=1000
            variant.m3u8
            #EXTINF:6,
            segment.ts?token=private
            #EXT-X-TWITCH-PREFETCH:https://cdn.example/upcoming.ts?secret=prefetch
            #EXT-X-DATERANGE:ID="ad-1",CLASS="twitch",X-TV-TWITCH-TRIGGER-URL="https://cdn.example/trigger/secret"
            """, new Uri("https://cdn.example/master.m3u8"));
        Assert.Equal(false, rewritten.Contains("https://", StringComparison.Ordinal));
        Assert.Equal(false, rewritten.Contains("secret", StringComparison.Ordinal));
        Assert.Equal(false, rewritten.Contains("token=", StringComparison.Ordinal));
        Assert.Equal(6, rewritten.Split("/media/playback/").Length - 1);
        Assert.Contains("#EXT-X-TWITCH-PREFETCH:/media/playback/", rewritten);
        Assert.Equal(false, rewritten.Contains("X-TV-TWITCH-TRIGGER-URL", StringComparison.Ordinal));
        Assert.Contains("BYTERANGE=\"100@0\"", rewritten);
        var rejected = false;
        try
        {
            media.RewritePlaylist("#EXTM3U\n#EXT-X-SESSION-DATA:DATA-ID=\"unhandled\",VALUE=\"https://cdn.example/secret\"\n",
                new Uri("https://cdn.example/master.m3u8"));
        }
        catch (InvalidDataException) { rejected = true; }
        Assert.True(rejected);
        return Task.CompletedTask;
    }

    private static async Task MediaSecurityAsync()
    {
        foreach (var destination in new[] { "http://cdn.example/a", "https://127.0.0.1/a", "https://192.168.1.1/a", "https://169.254.169.254/a", "file:///C:/Windows/win.ini", "data:text/plain,secret", "https://[::ffff:127.0.0.1]/a", "https://example.com:8443/a", "https://user:pass@example.com/a" })
        {
            Assert.Equal(false, LocalWebMedia.IsSafeUri(new Uri(destination)));
            using var media = new LocalWebMedia("id", new Uri("https://cdn.example/master.m3u8"));
            var rejected = false;
            try { media.RewritePlaylist("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"" + destination + "\"\n", new Uri("https://cdn.example/master.m3u8")); }
            catch (InvalidDataException) { rejected = true; }
            Assert.True(rejected);
        }
        var calls = 0;
        using var handler = new AsyncHttpMessageHandler((request, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri("https://127.0.0.1/private");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var redirected = new LocalWebMedia("id", new Uri("https://cdn.example/master.m3u8"), http);
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        var blocked = false;
        try { await redirected.ServeAsync(context, redirected.Root); } catch (InvalidDataException) { blocked = true; }
        Assert.True(blocked); Assert.Equal(1, calls);
        using var dns = new LocalWebMedia("id", new Uri("https://localhost/private.m3u8"));
        blocked = false;
        try { await dns.ServeAsync(context, dns.Root); } catch (HttpRequestException) { blocked = true; }
        Assert.True(blocked);
    }

    private static async Task OfflineMediaAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "localweb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = Path.Combine(directory, "index.m3u8");
            var asset = Path.Combine(directory, "asset-000001.ts");
            await File.WriteAllTextAsync(root, "#EXTM3U\n#EXTINF:6,\nasset-000001.ts\n#EXT-X-ENDLIST\n");
            await File.WriteAllTextAsync(asset, "media");
            using var media = new LocalWebMedia("id", root, [asset]);
            var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Response.Body = new MemoryStream();
            await media.ServeAsync(context, media.Root);
            context.Response.Body.Position = 0;
            var playlist = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Contains("/media/id/", playlist); Assert.Equal(false, playlist.Contains(directory, StringComparison.Ordinal));
            foreach (var reference in new[] { "../private.ts", "asset-000002.ts", "https://cdn.example/asset-000001.ts", "file:///C:/Windows/win.ini" })
            {
                var blocked = false;
                try { media.RewritePlaylist("#EXTM3U\n" + reference, new Uri(root)); } catch (InvalidDataException) { blocked = true; }
                Assert.True(blocked);
            }
            context = new DefaultHttpContext();
            await media.ServeAsync(context, "guess"); Assert.Equal(404, context.Response.StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task MediaProxyAsync()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new AsyncHttpMessageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var manifest = request.RequestUri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal);
            if (manifest) Assert.True(request.Headers.Range is null);
            var response = new HttpResponseMessage(manifest ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new StringContent(manifest ? "#EXTM3U\n#EXTINF:6,\nsegment.ts?token=private\n#EXT-X-ENDLIST\n" : "123")
            };
            response.Headers.Add("Set-Cookie", "upstream-secret=private");
            if (!manifest)
            {
                Assert.Equal("bytes=1-3", request.Headers.Range!.ToString());
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(1, 3, 6);
            }
            return Task.FromResult(response);
        }));
        using var media = new LocalWebMedia("id", new Uri("https://cdn.example/index.m3u8"), http);
        Assert.True(media.PathFor(media.Root).EndsWith(".m3u8", StringComparison.Ordinal));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=0-1";
        context.Response.Body = new MemoryStream();
        await media.ServeAsync(context, media.Root);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var playlist = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(false, playlist.Contains("private", StringComparison.Ordinal));
        var resource = playlist.Split('\n').Single(line => line.StartsWith("/media/", StringComparison.Ordinal)).Split('/')[^1];
        Assert.True(resource.EndsWith(".ts", StringComparison.Ordinal));
        context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=1-3";
        context.Response.Body = new MemoryStream();
        await media.ServeAsync(context, resource);
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 1-3/6", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(false, context.Response.Headers.ContainsKey("Set-Cookie"));
        context.Response.Body.Position = 0;
        Assert.Equal("123", await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.SequenceEqual(new[] { "/index.m3u8", "/segment.ts" }, requests);
    }

    private static async Task MediaPartialPlaylistAsync()
    {
        var ranges = new List<string>();
        using var http = new HttpClient(new AsyncHttpMessageHandler((request, _) =>
        {
            var range = request.Headers.Range?.ToString() ?? "";
            ranges.Add(range);
            var response = new HttpResponseMessage(range.Length == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new StringContent(range.Length == 0 ? "#EXTM3U\n#EXTINF:6,\nsegment.ts\n" : "#E")
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.apple.mpegurl");
            return Task.FromResult(response);
        }));
        using var media = new LocalWebMedia("id", new Uri("https://cdn.example/index.m3u8"), http);
        var alias = media.RegisterRemote(new Uri("https://cdn.example/playlist"));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=0-1";
        context.Response.Body = new MemoryStream();
        await media.ServeAsync(context, alias);
        Assert.SequenceEqual(new[] { "bytes=0-1", "" }, ranges);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var playlist = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("/media/id/", playlist);
    }

    private sealed class TestWorkspace : ILocalWebWorkspace
    {
        internal bool Disposed { get; private set; }
        public Task<object?> InvokeAsync(string action, JsonElement input, CancellationToken cancellationToken) => Task.FromResult<object?>(new { ok = true });
        public Task ServeMediaAsync(HttpContext context, string playbackId, string resourceId) => context.Response.WriteAsync("protected-media", context.RequestAborted);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
