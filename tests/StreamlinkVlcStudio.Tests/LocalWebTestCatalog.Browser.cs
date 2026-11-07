using System.Security.Cryptography.X509Certificates;
using System.Windows.Interop;
using Microsoft.AspNetCore.Http;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task BrowserAsync() => BrowserAsync(false);
    private static Task BrowserExperienceAsync() => BrowserAsync(true);
    private static Task BrowserOverlayAsync() => BrowserAsync(false, true);
    private static Task BrowserDesktopSignInAsync() => BrowserAsync(false, desktopSignIn: true);
    private static Task BrowserVolumeAsync() => BrowserAsync(false, volume: true);
    private static Task BrowserVodChatAsync() => BrowserAsync(false, vodChat: true);
    private static Task BrowserPublicVodChatAsync() => BrowserAsync(false, publicVodChat: true);
    private static Task BrowserCategoryArtworkAsync() => BrowserAsync(false, categoryArtwork: true);
    private static Task BrowserFollowingLastLiveAsync() => BrowserAsync(false, lastLive: true);
    private static Task BrowserAsync(bool experience, bool overlay = false, bool desktopSignIn = false, bool volume = false, bool vodChat = false, bool publicVodChat = false,
        bool categoryArtwork = false, bool lastLive = false, bool prefetch = false) => TestSta.RunOffscreenAsync(async () =>
    {
        using var certificate = Certificate();
        var workspace = new BrowserWorkspace { Experience = experience, Overlay = overlay, CategoryArtwork = categoryArtwork, LastLive = lastLive, Prefetch = prefetch, PlayerPlatform = prefetch ? "Kick" : "Twitch" };
        await using var server = await Server(certificate, () => workspace);
        if (volume) await server.Users.AddAsync("otherviewer", "Other viewer password 123!");
        workspace.Address = server.Address;
        var profile = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "localweb-browser-" + Guid.NewGuid().ToString("N"));
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile,
            options: new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-background-media-suspend"));
        // MediaSource needs an active presentation host, even though this test window stays offscreen.
        var window = new Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 1440,
            Height = 1000,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).EnsureHandle());
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
        controller.IsVisible = true;
        var core = controller.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync("window.testErrors=[];window.addEventListener('error',e=>window.testErrors.push(e.message));window.addEventListener('securitypolicyviolation',e=>window.testErrors.push(e.violatedDirective+' '+e.blockedURI));");
        if (overlay)
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.chatLayoutRequests=[];window.fetch=new Proxy(window.fetch,{apply(target,self,args){if(args[0]==='/api/chat/layout')window.chatLayoutRequests.push(JSON.parse(args[1].body));return Reflect.apply(target,self,args)}})");
        core.ServerCertificateErrorDetected += (_, args) =>
        {
            using var presented = X509Certificate2.CreateFromPem(args.ServerCertificate.ToPemEncoding());
            if (new Uri(args.RequestUri).Authority == server.Address.Authority && LocalWebCertificate.Fingerprint(presented) == LocalWebCertificate.Fingerprint(certificate))
                args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };
        try
        {
            if (desktopSignIn)
            {
                await CheckDesktopBrowserSignIn(core, server);
                return;
            }
            core.Navigate(server.Address.AbsoluteUri);
            await WaitBrowser(core, "document.readyState==='complete' && !!document.querySelector('#password')");
            await core.ExecuteScriptAsync($"document.querySelector('#username').value={JsonSerializer.Serialize(TestUsername)}");
            if (lastLive)
            {
                await CheckBrowserFollowingLastLive(core, controller, workspace, TestPassword);
                return;
            }
            if (categoryArtwork)
            {
                await CheckBrowserCategoryArtwork(core, controller, workspace, TestPassword);
                return;
            }
            if (publicVodChat)
            {
                await CheckPublicBrowserVodChat(core, workspace, TestPassword);
                return;
            }
            if (vodChat)
            {
                await CheckBrowserVodChat(core, workspace, TestPassword);
                return;
            }
            if (volume)
            {
                await CheckBrowserVolume(core, workspace, server, TestPassword);
                return;
            }
            if (experience)
            {
                await CheckBrowserExperience(core, controller, workspace, server, TestPassword);
                return;
            }
            if (overlay)
            {
                await CheckBrowserOverlay(core, controller, workspace, server, TestPassword);
                return;
            }
            await core.ExecuteScriptAsync("window.testEvents=[];window.Hls = new Proxy(window.Hls,{construct(target,args){const h=Reflect.construct(target,args);window.testHls=h;h.on(target.Events.ERROR,(_,data)=>window.testErrors.push(data.details+' '+(data.reason||'')));for(const name of ['MEDIA_ATTACHED','MANIFEST_LOADING','MANIFEST_LOADED','MANIFEST_PARSED','FRAG_LOADING','FRAG_LOADED','BUFFER_APPENDED'])h.on(target.Events[name],()=>window.testEvents.push(name));return h;}})");
            await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(TestPassword)};document.querySelector('#login-form').requestSubmit();");
            await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===1");
            Assert.Equal("false", await core.ExecuteScriptAsync("window.injected===true"));
            Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('.card script,.card img[onerror]').length"));
            await core.ExecuteScriptAsync("document.querySelector('.card-main').click()");
            await WaitBrowser(core, "document.querySelector('#players video')?.currentTime>0.1");
            await WaitBrowser(core, "document.querySelector('#players video').readyState>=2 && document.querySelector('#players video').videoWidth>0");
            Assert.Equal("true", await core.ExecuteScriptAsync("window.testEvents.includes('MEDIA_ATTACHED')"));
            var pixel = JsonSerializer.Deserialize<int[]>(await core.ExecuteScriptAsync("(()=>{const c=document.createElement('canvas');c.width=c.height=1;const x=c.getContext('2d');x.drawImage(document.querySelector('#players video'),0,0,1,1);return Array.from(x.getImageData(0,0,1,1).data)})()"))!;
            Assert.True(pixel.Take(3).Max() > 100 && pixel.Take(3).Min() < 50);
            var decodedFrame = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("(()=>{const c=document.createElement('canvas');c.width=c.height=64;c.getContext('2d').drawImage(document.querySelector('#players video'),0,0,64,64);return c.toDataURL('image/png')})()"))!;
            Assert.Equal("true", await core.ExecuteScriptAsync("!!window.Hls && Hls.version==='1.7.3'"));
            await core.ExecuteScriptAsync("document.querySelector('.chat-form input').value='<script>window.injected=true</script>';document.querySelector('.chat-form').requestSubmit()");
            await WaitBrowser(core, "document.querySelector('.chat-messages').textContent.includes('<script>')");
            Assert.Equal("false", await core.ExecuteScriptAsync("window.injected===true"));
            await core.ExecuteScriptAsync("document.querySelector('.multistream-toggle').click();document.querySelector('.rename').value='Living room';document.querySelector('.rename').dispatchEvent(new Event('change'))");
            Assert.Contains("Living room", await core.ExecuteScriptAsync("document.querySelector('.player:not([hidden]) .rename').value"));
            await core.ExecuteScriptAsync("document.querySelector('[data-action=reload]').click()");
            await WaitBrowser(core, "document.querySelectorAll('#players video').length===1 && document.querySelector('#players video').currentTime>0.1");
            Assert.Contains("Living room", await core.ExecuteScriptAsync("document.querySelector('.player:not([hidden]) .rename').value"));
            var repository = Directory.GetCurrentDirectory();
            while (!File.Exists(Path.Combine(repository, "StreamlinkVlcStudio.sln")))
                repository = Directory.GetParent(repository)?.FullName ?? throw new DirectoryNotFoundException("Repository root is required for browser evidence.");
            var artifacts = Path.Combine(repository, ".artifacts", "local-web");
            Directory.CreateDirectory(artifacts);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "decoded-frame.png"), Convert.FromBase64String(decodedFrame[(decodedFrame.IndexOf(',') + 1)..]));
            await using (var screenshot = File.Create(Path.Combine(artifacts, "browser-player.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
            await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
            await WaitBrowser(core, "!document.querySelector('#preferences').hidden");
            Assert.Equal("true", await core.ExecuteScriptAsync("!document.querySelector('#default-quality').disabled"));
            await core.ExecuteScriptAsync("document.querySelector('#logout').click()");
            await WaitBrowser(core, "!document.querySelector('#login').hidden");
            Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('#players video').length"));
        }
        finally { controller.Close(); window.Close(); }
    });

    private static async Task WaitBrowser(CoreWebView2 browser, string expression)
    {
        var time = Stopwatch.StartNew();
        while (await browser.ExecuteScriptAsync(expression) != "true")
        {
            if (time.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException($"Browser condition failed: {expression}. URL: {browser.Source}. Details: {await browser.ExecuteScriptAsync("JSON.stringify({notice:document.querySelector('#notice')?.textContent,login:document.querySelector('#login-error')?.textContent,status:document.querySelector('.player-status')?.textContent,ready:document.readyState,visibility:document.visibilityState,events:window.testEvents,errors:window.testErrors,media:(()=>{const v=document.querySelector('video');return v&&{ready:v.readyState,network:v.networkState,paused:v.paused,time:v.currentTime,src:v.currentSrc,error:v.error?.message,native:v.canPlayType('application/vnd.apple.mpegurl'),width:v.getBoundingClientRect().width}})()})")}");
            await Task.Delay(100);
        }
    }

    private sealed partial class BrowserWorkspace : ILocalWebWorkspace
    {
        private readonly Dictionary<string, List<string>> messages = new(StringComparer.Ordinal);
        private readonly HashSet<string> vodPlayers = new(StringComparer.Ordinal);
        private int opened;
        public bool Experience { get; init; }
        public Uri Address { get; set; } = null!;
        public bool FailFollowing { get; set; }
        public TaskCompletionSource? HoldFollowing { get; set; }
        public int DownloadCompletedSegments { get; set; } = 47;
        public string ChatStatus { get; set; } = "Connected";
        public bool Overlay { get; init; }
        public string PlayerChannel { get; set; } = "streamer";
        public string PlayerPlatform { get; set; } = "Twitch";
        public bool DecorationsReady { get; set; }
        public Func<JsonElement, CancellationToken, Task<object>>? VodChatRead { get; set; }
        public StreamTarget? PublicVodTarget { get; set; }
        private LocalWebMedia? publicVodMedia;
        public async Task<object?> InvokeAsync(string action, JsonElement input, CancellationToken token)
        {
            if (action == "chat" && VodChatRead is { } read) return await read(input, token);
            if (action == "following" && HoldFollowing is { } hold) await hold.Task.WaitAsync(token);
            if (action == "following" && FailFollowing) throw new IOException("Test-only provider failure.");
            if (Experience && ExperienceResult(action, input) is { } experienceResult) return experienceResult;
            if (LastLive && action is "following" or "following/history") return await LastLiveResult(action, token);
            if (CategoryArtwork && CategoryArtworkResult(action, input) is { } artworkResult) return artworkResult;
            object result = action switch
            {
                "settings" => new { defaultQuality = "best", lowLatency = true, qualities = new[] { "best", "720p" }, accounts = new { twitch = true, kick = true } },
                "following" => new { items = new[] { new { key = "fixture", channel = "streamer", displayName = "<script>window.injected=true</script>", title = "Browser HLS playback fixture", platform = "Twitch", state = "Live", thumbnailUrl = "" } } },
                "open" => Open(input),
                "channel/profile" => new
                {
                    displayName = "Northstar",
                    description = "A public streamer bio. <script>window.injected=true</script>",
                    profileImageUrl = "",
                    followers = 6784000,
                    isPartner = true,
                    title = "Live with the community",
                    category = "Adventure games",
                    viewers = 8325,
                    startedAt = "2026-10-22T12:00:00Z",
                    tags = new[] { "English", "Community" },
                    links = new[] { new { title = "YouTube", url = "https://youtube.com/@northstar" } },
                    panels = Experience ? Enumerable.Range(0, 7).Select(index => new
                    {
                        title = "Panel " + index,
                        description = "**Community** [Join us](https://example.com) <script>window.injected=true</script>",
                        imageUrl = Address + "media/artwork/mountain",
                        linkUrl = "https://example.com"
                    }).ToArray() : [],
                    hasExtensions = true
                },
                "chat" => ReadChat(input),
                "chat/presentation" => new { version = DecorationsReady ? 1 : 0, messages = Overlay && input.GetProperty("version").GetInt64() != (DecorationsReady ? 1 : 0) ? DecoratedMessages() : [] },
                "chat/send" => MarkSent(input),
                _ => new { ok = true }
            };
            return result;
        }
        private object MarkSent(JsonElement input)
        {
            var id = input.GetProperty("player").GetString()!;
            if (!messages.TryGetValue(id, out var rows)) messages[id] = rows = [];
            rows.Add(input.GetProperty("message").GetString()!);
            return new { sent = true };
        }
        private object ReadChat(JsonElement input)
        {
            var id = input.GetProperty("player").GetString()!;
            if (vodPlayers.Contains(id))
                return new
                {
                    messages = new[] { 3, 12, 32 }.Where(offset => offset >= input.GetProperty("from").GetDouble())
                        .Select(offset => new { offset, message = new { username = "Archive viewer", message = "Replay at " + offset } }).ToArray(),
                    through = 60,
                    outcome = "Completed",
                    status = "Completed"
                };
            var after = input.GetProperty("after").GetInt64();
            return new
            {
                messages = (Overlay ? DecoratedMessages().Select((message, index) => new { sequence = index + 1, message }) : [])
                    .Concat((messages.GetValueOrDefault(id) ?? []).Select((text, index) => new
                    {
                        sequence = index + (Overlay ? 4 : 1),
                        message = new ChatMessage(PlatformKind.Twitch, "streamer", "Test viewer", text, DateTimeOffset.UnixEpoch, MessageId: $"sent-{index}")
                    }))
                    .Where(entry => entry.sequence > after).ToArray(),
                status = ChatStatus
            };
        }
        private object Open(JsonElement input)
        {
            var id = "fixture" + ++opened;
            if (PublicVodTarget is { } target)
            {
                publicVodMedia = new LocalWebMedia(id, new Uri(target.Url));
                return new
                {
                    id,
                    key = "public-vod",
                    title = target.DisplayTitle,
                    channel = target.Channel,
                    platform = target.Platform.ToString(),
                    url = publicVodMedia.PathFor(publicVodMedia.Root),
                    isVod = true,
                    isMp4 = false,
                    resume = 600,
                    duration = target.MediaDuration.TotalSeconds,
                    quality = input.GetProperty("quality").GetString()
                };
            }
            var isVod = input.TryGetProperty("input", out var source) && source.GetString()!.Contains("/videos/", StringComparison.Ordinal);
            if (isVod) vodPlayers.Add(id);
            if (Prefetch)
            {
                publicVodMedia?.Dispose();
                publicVodMedia = CreatePrefetchMedia(id);
            }
            return new
            {
                id,
                key = "fixture",
                title = "Browser HLS playback fixture",
                channel = PlayerChannel,
                platform = PlayerPlatform,
                chatLayout = (WebChatOverlayLayout?)null,
                url = publicVodMedia?.PathFor(publicVodMedia.Root) ?? $"/media/{id}/root",
                isVod,
                isMp4 = false,
                resume = 0,
                duration = 0,
                quality = input.GetProperty("quality").GetString()
            };
        }
        public async Task ServeMediaAsync(HttpContext context, string playbackId, string resourceId)
        {
            if (publicVodMedia is { } media)
            {
                await media.ServeAsync(context, resourceId);
                return;
            }
            if (Experience && playbackId == "artwork")
            {
                context.Response.ContentType = "image/svg+xml";
                await context.Response.WriteAsync(Artwork(resourceId), context.RequestAborted);
                return;
            }
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
            if (resourceId == "root")
            {
                var playlist = await File.ReadAllTextAsync(Path.Combine(directory, "index.m3u8"), context.RequestAborted);
                playlist = System.Text.RegularExpressions.Regex.Replace(playlist, @"(?m)^(index\d+\.ts)\r?$", $"/media/{playbackId}/$1");
                context.Response.ContentType = "application/vnd.apple.mpegurl";
                await context.Response.WriteAsync(playlist, context.RequestAborted);
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(resourceId, @"^index[0-5]\.ts$"))
                await Results.File(Path.Combine(directory, resourceId), "video/mp2t", enableRangeProcessing: true).ExecuteAsync(context);
            else context.Response.StatusCode = 404;
        }
        public ValueTask DisposeAsync() { publicVodMedia?.Dispose(); artworkHttp?.Dispose(); return ValueTask.CompletedTask; }
    }
}
