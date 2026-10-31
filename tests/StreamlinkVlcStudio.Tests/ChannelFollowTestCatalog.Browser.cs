using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Follows;

internal static partial class ChannelFollowTestCatalog
{
    private static Task BrowserUserVerificationAsync() => BrowserUserVerificationAsync(true);

    private static async Task BrowserUserVerificationAsync(bool shouldFollow)
    {
        foreach (var trusted in new[] { false, true })
            await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
            {
                fixture.Following = !shouldFollow;
                fixture.IntegrityChallengesRemaining = 2;
                var shown = false;
                var closed = false;
                fixture.VerificationFactory = (controller, _, _) =>
                {
                    shown = true;
                    _ = ClickAsync();
                    return new VerificationLease(() => closed = true);

                    async Task ClickAsync()
                    {
                        var selector = shouldFollow ? "button[data-a-target=\"follow-button\"]" : "button[data-a-target=\"modal-unfollow-button\"]";
                        if (!trusted)
                        {
                            await controller.CoreWebView2.ExecuteScriptAsync($"document.querySelector({JsonSerializer.Serialize(selector)}).click()");
                            return;
                        }
                        var json = await controller.CoreWebView2.ExecuteScriptAsync($$"""
                            JSON.stringify((() => {const r=document.querySelector({{JsonSerializer.Serialize(selector)}}).getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})())
                            """);
                        using var point = JsonDocument.Parse(JsonSerializer.Deserialize<string>(json)!);
                        var x = point.RootElement.GetProperty("x").GetDouble();
                        var y = point.RootElement.GetProperty("y").GetDouble();
                        foreach (var type in new[] { "mousePressed", "mouseReleased" })
                            await controller.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                                JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));
                    }
                };
                if (trusted)
                {
                    var result = await ChangeAsync(fixture);
                    Assert.NotNull(result);
                    Assert.Equal(1, fixture.AcceptedMutations);
                    Assert.Equal(shouldFollow, fixture.Following);
                }
                else
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ChangeAsync(fixture));
                    Assert.Contains("unexpected request was blocked", error.Message);
                    Assert.Equal(0, fixture.AcceptedMutations);
                    Assert.Equal(!shouldFollow, fixture.Following);
                }
                Assert.True(shown);
                Assert.True(closed);
            });
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.Following = !shouldFollow;
            fixture.IntegrityChallengesRemaining = 2;
            fixture.VerificationFactory = (_, _, cancel) =>
            {
                cancel();
                return new VerificationLease(() => { });
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() => ChangeAsync(fixture));
            Assert.Equal(0, fixture.AcceptedMutations);
            Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
        });

        Task<ChannelFollowResult?> ChangeAsync(FollowBrowserFixture fixture) => shouldFollow
            ? fixture.Service().FollowAsync(fixture.Target) : fixture.Service().UnfollowAsync(fixture.Target);
    }

    private sealed class VerificationLease(Action close) : IDisposable
    {
        public void Dispose() => close();
    }

    private static async Task BrowserIntegrityRetryAsync()
    {
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.IntegrityChallengesRemaining = 1;
            var result = await fixture.Service().FollowAsync(fixture.Target);
            Assert.NotNull(result);
            Assert.Equal(2, fixture.Mutations);
            Assert.True(fixture.Following);
            Assert.Equal(0, fixture.BlockedMutationRequests);
        });
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.IntegrityChallengesRemaining = 2;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("could not confirm", error.Message);
            Assert.Equal(2, fixture.Mutations);
            Assert.Equal(false, fixture.Following);
        });
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.IntegrityChallengesRemaining = 1;
            fixture.OnIntegrityChallenge = () => fixture.SetCookie("different-fixture-account");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("session", error.Message);
            Assert.Equal(0, fixture.AcceptedMutations);
            Assert.Equal(false, fixture.Following);
        });
    }

    private static Task BrowserSuccessAsync(PlatformKind platform) => WithFixtureAsync(platform, async fixture =>
    {
        fixture.DeferMutation = true;
        var operation = fixture.Service().FollowAsync(fixture.Target);
        await TestWait.UntilAsync(() => fixture.Mutations == 1, TimeSpan.FromSeconds(15));
        Assert.Equal(false, operation.IsCompleted);
        Assert.Equal(false, NativeWindowTest.IsWindowVisible(fixture.Host));
        Assert.Equal(false, fixture.Owner.IsVisible);
        Assert.True(fixture.Core!.IsMuted);
        await Task.Delay(450);
        Assert.Equal(1, fixture.Mutations);
        fixture.ReleaseMutation();
        var result = await operation;
        Assert.NotNull(result);
        Assert.Equal(platform, result!.Platform);
        Assert.Equal("streamer", result.Channel);
        if (platform == PlatformKind.Kick)
        {
            Assert.SequenceEqual(new[] { "offline", "streamer" }, result.KickFollowedChannels!);
            Assert.True(fixture.FollowListPages >= 4);
        }
        Assert.Equal(1, fixture.Mutations);
        Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
        Assert.True(fixture.MediaBlocked);
    });

    private static async Task AlreadyFollowedAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            await WithFixtureAsync(platform, async fixture =>
            {
                fixture.Following = true;
                var result = await fixture.Service().FollowAsync(fixture.Target);
                Assert.NotNull(result);
                Assert.Equal(0, fixture.Mutations);
                Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
            });
    }

    private static async Task BrowserErrorsAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            await WithFixtureAsync(platform, async fixture =>
            {
                fixture.Status = platform == PlatformKind.Twitch ? 403 : 429;
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
                Assert.Contains(platform == PlatformKind.Twitch ? "refused" : "limiting", error.Message);
                Assert.Equal(1, fixture.Mutations);
                Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
            });
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.MutationBody = "{\"errors\":[{\"message\":\"private-session-token\"}],\"data\":{\"followUser\":null}}";
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("could not confirm", error.Message);
            Assert.Equal(false, error.Message.Contains("private-session-token", StringComparison.Ordinal));
        });
        await WithFixtureAsync(PlatformKind.Kick, async fixture =>
        {
            fixture.CommitMutation = false;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("not in your followed list", error.Message);
            Assert.Equal(1, fixture.Mutations);
        });
        await WithFixtureAsync(PlatformKind.Kick, async fixture =>
        {
            fixture.ExpireListAfterMutation = true;
            var prompts = 0;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service((_, _) =>
            {
                prompts++;
                return Task.FromResult(true);
            }).FollowAsync(fixture.Target));
            Assert.Contains("session expired after the follow request", error.Message);
            Assert.Equal(0, prompts);
            Assert.Equal(1, fixture.Mutations);
        });
    }

    private static async Task BrowserSignInAsync()
    {
        foreach (var continueSignIn in new[] { false, true })
            await WithFixtureAsync(PlatformKind.Kick, async fixture =>
            {
                fixture.HasSession = false;
                var prompts = 0;
                var service = fixture.Service((platform, token) =>
                {
                    prompts++;
                    Assert.Equal(PlatformKind.Kick, platform);
                    token.ThrowIfCancellationRequested();
                    if (continueSignIn) fixture.SetCookie("signed-in-fixture-token");
                    return Task.FromResult(continueSignIn);
                });
                var result = await service.FollowAsync(fixture.Target);
                Assert.Equal(1, prompts);
                Assert.Equal(continueSignIn ? 1 : 0, fixture.Mutations);
                Assert.Equal(continueSignIn, result is not null);
                Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
            });
        foreach (var http401 in new[] { false, true })
            await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
            {
                fixture.ExpiredSession = true;
                fixture.ExpireWithHttp401 = http401;
                var prompts = 0;
                var result = await fixture.Service((_, _) =>
                {
                    prompts++;
                    fixture.ExpiredSession = false;
                    fixture.SetCookie("renewed-fixture-token");
                    return Task.FromResult(true);
                }).FollowAsync(fixture.Target);
                Assert.NotNull(result);
                Assert.Equal(1, prompts);
                Assert.Equal(1, fixture.Mutations);
            });
    }

    private static Task BrowserAccountBeforeAsync() => WithFixtureAsync(PlatformKind.Twitch, async fixture =>
    {
        fixture.BeforeState = () => fixture.SetCookie("different-fixture-account");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
        Assert.Contains("session changed", error.Message);
        Assert.Equal(0, fixture.Mutations);
    });

    private static Task BrowserAccountAfterAsync() => WithFixtureAsync(PlatformKind.Twitch, async fixture =>
    {
        fixture.OnMutation = () => fixture.SetCookie("different-fixture-account");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
        Assert.Contains("session changed", error.Message);
        Assert.Equal(1, fixture.Mutations);
    });

    private static async Task BrowserSafetyAsync()
    {
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.WrongControlName = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("identify the selected broadcaster", error.Message);
            Assert.Equal(0, fixture.Mutations);
        });
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.OutsideButton = true;
            var result = await fixture.Service().FollowAsync(fixture.Target);
            Assert.NotNull(result);
            Assert.Equal(1, fixture.Mutations);
        });
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            await WithFixtureAsync(platform, async fixture =>
            {
                fixture.WrongMutationTarget = true;
                fixture.CommitMutation = false;
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
                Assert.Contains("unexpected request was blocked", error.Message);
                Assert.Equal(1, fixture.BlockedMutationRequests);
                Assert.Equal(false, fixture.Following);
            });
        await WithFixtureAsync(PlatformKind.Kick, async fixture =>
        {
            fixture.DuplicateButtons = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("ambiguous", error.Message);
            Assert.Equal(0, fixture.Mutations);
        });
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.ChangeRoute = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("selected channel", error.Message);
            Assert.Equal(0, fixture.Mutations);
        });
        await WithFixtureAsync(PlatformKind.Kick, async fixture =>
        {
            fixture.DeferMutation = true;
            var operation = fixture.Service().FollowAsync(fixture.Target);
            await TestWait.UntilAsync(() => fixture.Mutations == 1, TimeSpan.FromSeconds(15));
            fixture.Core!.Navigate(fixture.Target.Url);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Contains("before retrying", error.Message);
            Assert.Equal(1, fixture.Mutations);
            Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
        });
        await WithFixtureAsync(PlatformKind.Kick, async fixture =>
        {
            fixture.ThrowAfterClick = true;
            fixture.ReloadAfterClick = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
            Assert.Contains("follow control could not be verified", error.Message);
            Assert.True(fixture.Mutations <= 1);
            Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
        });
    }

    private static async Task BrowserCancellationAsync()
    {
        await WithFixtureAsync(PlatformKind.Kick, async fixture =>
        {
            fixture.DeferMutation = true;
            using var cancellation = new CancellationTokenSource();
            var operation = fixture.Service().FollowAsync(fixture.Target, cancellation.Token);
            await TestWait.UntilAsync(() => fixture.Mutations == 1, TimeSpan.FromSeconds(15));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
            Assert.Equal(1, fixture.Mutations);
        });
        await TestSta.RunOffscreenAsync(async () =>
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
                Path.Combine(Path.GetTempPath(), $"channel-follow-late-{Guid.NewGuid():N}"));
            var owner = new Window();
            try
            {
                var pending = new TaskCompletionSource<CoreWebView2Controller>(TaskCreationOptions.RunContinuationsAsynchronously);
                nint host = 0;
                var service = new ChannelFollowService(owner, handle => { host = handle; return pending.Task; });
                using var cancellation = new CancellationTokenSource();
                var operation = service.FollowAsync(Twitch, cancellation.Token);
                Assert.True(host != 0);
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
                Assert.True(NativeWindowTest.IsWindow(host));
                pending.SetResult(await environment.CreateCoreWebView2ControllerAsync(host));
                await TestWait.UntilAsync(() => !NativeWindowTest.IsWindow(host), TimeSpan.FromSeconds(10));
            }
            finally { owner.Close(); }
        });
    }

    private static Task WithFixtureAsync(PlatformKind platform, Func<FollowBrowserFixture, Task> test) =>
        TestSta.RunOffscreenAsync(async () =>
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
                Path.Combine(Path.GetTempPath(), $"channel-follow-browser-{Guid.NewGuid():N}"));
            var fixture = new FollowBrowserFixture(environment, platform);
            try { await test(fixture); }
            finally { fixture.ReleaseMutation(); fixture.Owner.Close(); }
        });

    private sealed class FollowBrowserFixture(CoreWebView2Environment environment, PlatformKind platform)
    {
        internal Window Owner { get; } = new();
        internal StreamTarget Target { get; } = platform == PlatformKind.Twitch ? Twitch : Kick;
        internal CoreWebView2? Core { get; private set; }
        internal nint Host { get; private set; }
        internal int Mutations { get; private set; }
        internal int AcceptedMutations { get; private set; }
        internal int FollowListPages { get; private set; }
        internal bool MediaBlocked { get; private set; }
        internal bool Following { get; set; }
        internal bool PartialFollowStateFirst { get; set; }
        internal bool RestrictFollow { get; set; }
        internal int FollowStateReads { get; private set; }
        internal bool HasSession { get; set; } = true;
        internal bool ExpiredSession { get; set; }
        internal bool ExpireWithHttp401 { get; set; }
        internal bool ExpireListAfterMutation { get; set; }
        internal bool DuplicateButtons { get; set; }
        internal bool UnnamedUnfollowButton { get; set; }
        internal bool DelayNamedUnfollowButton { get; set; }
        internal bool WrongUnfollowDialogName { get; set; }
        internal bool IgnoreFirstUnfollowOpen { get; set; }
        internal bool IgnoreFirstFollowClick { get; set; }
        internal bool WrongControlName { get; set; }
        internal bool OutsideButton { get; set; }
        internal bool WrongMutationTarget { get; set; }
        internal int BlockedMutationRequests { get; private set; }
        internal bool ChangeRoute { get; set; }
        internal bool ReloadAfterClick { get; set; }
        internal bool ThrowAfterClick { get; set; }
        internal bool DeferMutation { get; set; }
        internal bool CommitMutation { get; set; } = true;
        internal int Status { get; set; } = 200;
        internal string? MutationBody { get; set; }
        internal int IntegrityChallengesRemaining { get; set; }
        internal Action? OnIntegrityChallenge { get; set; }
        internal Func<CoreWebView2Controller, StreamTarget, Action, IDisposable?>? VerificationFactory { get; set; }
        internal Action? BeforeState { get; set; }
        internal Action? OnMutation { get; set; }
        private CoreWebView2WebResourceRequestedEventArgs? pendingRequest;
        private CoreWebView2Deferral? pendingDeferral;

        internal ChannelFollowService Service(Func<PlatformKind, CancellationToken, Task<bool>>? signIn = null) =>
            new(Owner, CreateControllerAsync, signIn ?? ((_, _) => Task.FromResult(false)), TimeSpan.FromSeconds(10),
                (controller, target, cancel) => VerificationFactory?.Invoke(controller, target, cancel));

        internal void SetCookie(string value)
        {
            var name = platform == PlatformKind.Twitch ? "auth-token" : "session_token";
            var domain = platform == PlatformKind.Twitch ? "www.twitch.tv" : "kick.com";
            var cookie = Core!.CookieManager.CreateCookie(name, value, domain, "/");
            cookie.IsSecure = true;
            Core.CookieManager.AddOrUpdateCookie(cookie);
        }

        private async Task<CoreWebView2Controller> CreateControllerAsync(nint host)
        {
            Host = host;
            var controller = await environment.CreateCoreWebView2ControllerAsync(host);
            Core = controller.CoreWebView2;
            if (HasSession) SetCookie("fixture-follow-token");
            Core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            Core.WebResourceRequested += Requested;
            Core.NavigationStarting += (_, _) =>
            {
                // Observe after the service's guard, before a denied request can close the controller.
                Core.WebResourceRequested -= ObserveGuard;
                Core.WebResourceRequested += ObserveGuard;
            };
            Core.WebResourceResponseReceived += (_, args) =>
            {
                if (args.Request.Uri == "https://media.test/stream.m3u8" && args.Response.StatusCode == 403) MediaBlocked = true;
                // A held request can be rejected by the production guard after this fixture's
                // request handler returns. Commit only the final response the browser receives.
                if (args.Response.StatusCode is >= 200 and < 300 &&
                    args.Response.Headers.Contains("X-Fixture-Follow-Commit"))
                {
                    AcceptedMutations++;
                    Following = args.Response.Headers.GetHeader("X-Fixture-Follow-State") != "false";
                }
            };
            return controller;
        }

        private void ObserveGuard(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (args.Request.Method == "POST" && (args.Request.Uri == Gql || args.Request.Uri.EndsWith("/follow", StringComparison.Ordinal)) &&
                args.Response is { StatusCode: 403, ReasonPhrase: "Unexpected follow blocked" }) BlockedMutationRequests++;
        }

        private void Requested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var address = args.Request.Uri;
            if (address == Target.Url)
            {
                Respond(args, Page(), 200, "text/html");
                return;
            }
            if (platform == PlatformKind.Twitch && address == Gql && args.Request.Method == "POST")
            {
                var body = ReadRequest(args.Request);
                if ((body.Contains("FollowButton_FollowUser", StringComparison.Ordinal) || body.Contains("FollowButton_UnfollowUser", StringComparison.Ordinal))) { Mutate(args); return; }
                BeforeState?.Invoke();
                FollowStateReads++;
                if (ExpiredSession && ExpireWithHttp401) { Respond(args, "{}", 401); return; }
                var state = PartialFollowStateFirst && body.Contains("PlayerTrackingContextQuery", StringComparison.Ordinal)
                    ? PartialState(Following) : State(Following);
                if (RestrictFollow) state = state.Replace("\"canFollow\":true", "\"canFollow\":false", StringComparison.Ordinal);
                Respond(args, ExpiredSession ? "{\"data\":{\"user\":{\"id\":\"123\",\"login\":\"streamer\",\"self\":null}}}" : state);
                return;
            }
            if (platform == PlatformKind.Kick && address.StartsWith("https://kick.com/api/v2/channels/followed", StringComparison.Ordinal))
            {
                if (Mutations > 0 && ExpireListAfterMutation) { Respond(args, "{}", 401); return; }
                FollowListPages++;
                var second = new Uri(address).Query.Length != 0;
                var channels = second ? Following ? new[] { "streamer" } : [] : new[] { "offline" };
                Respond(args, JsonSerializer.Serialize(new
                {
                    channels = channels.Select(channel => new { channel_slug = channel }),
                    nextCursor = second ? "" : "second+page"
                }));
                return;
            }
            if (platform == PlatformKind.Kick && address == "https://kick.com/api/v2/channels/streamer/follow" && args.Request.Method is "POST" or "DELETE")
            {
                Mutate(args);
                return;
            }
            Respond(args, "{}");
        }

        private void Mutate(CoreWebView2WebResourceRequestedEventArgs args)
        {
            Mutations++;
            OnMutation?.Invoke();
            if (IntegrityChallengesRemaining > 0)
            {
                IntegrityChallengesRemaining--;
                OnIntegrityChallenge?.Invoke();
                Respond(args, IsUnfollow(args.Request) ? IntegrityChallenge.Replace("followUser", "unfollowUser", StringComparison.Ordinal) : IntegrityChallenge);
                return;
            }
            if (DeferMutation)
            {
                pendingRequest = args;
                pendingDeferral = args.GetDeferral();
                return;
            }
            Respond(args, MutationBody ?? (platform == PlatformKind.Twitch ? IsUnfollow(args.Request) ? UnfollowResponse() : MutationResponse() : "{}"), Status,
                commit: CommitMutation);
        }

        internal void ReleaseMutation()
        {
            if (pendingDeferral is null) return;
            try
            {
                Respond(pendingRequest!, MutationBody ?? (platform == PlatformKind.Twitch ? IsUnfollow(pendingRequest!.Request) ? UnfollowResponse() : MutationResponse() : "{}"), Status,
                    commit: CommitMutation);
                pendingDeferral.Complete();
            }
            catch (Exception) { /* A canceled controller may have released its pending request. */ }
            finally { pendingRequest = null; pendingDeferral = null; }
        }

        private static bool IsUnfollow(CoreWebView2WebResourceRequest request) => request.Method == "DELETE" ||
            request.Method == "POST" && request.Uri == Gql && ReadRequest(request).Contains("FollowButton_UnfollowUser", StringComparison.Ordinal);

        private void Respond(CoreWebView2WebResourceRequestedEventArgs args, string body, int status = 200,
            string contentType = "application/json", bool commit = false)
        {
            var origin = platform == PlatformKind.Twitch ? "https://www.twitch.tv" : "https://kick.com";
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status,
                "Fixture", $"Content-Type: {contentType}\r\nAccess-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\nAccess-Control-Allow-Headers: content-type,authorization,x-app-platform" +
                (commit ? "\r\nX-Fixture-Follow-Commit: true\r\nX-Fixture-Follow-State: " + (IsUnfollow(args.Request) ? "false" : "true") : ""));
        }

        private string Page()
        {
            var attributes = platform == PlatformKind.Twitch
                ? Following ? "data-a-target=\"unfollow-button\"" : "data-a-target=\"follow-button\""
                : "data-testid=\"follow-button\"";
            var icon = Following ? "HeartFilled" : "HeartOutline";
            var name = WrongControlName ? "Other" : "Streamer";
            var action = Following ? "Unfollow" : "Follow";
            var handler = platform == PlatformKind.Twitch && Following ? "openUnfollow()" : "submitFollow()";
            var button = $"<button {attributes} aria-label=\"{action} {name}\" onclick=\"{handler}\"><svg data-ds-icon=\"{icon}\"></svg>Follow</button>";
            var controls = button + (DuplicateButtons ? button : "");
            if (UnnamedUnfollowButton)
                controls = button.Replace($"Unfollow {name}", "Unfollow ", StringComparison.Ordinal)
                    .Replace("openUnfollow()", "throw new Error('Unnamed About control must not be clicked')", StringComparison.Ordinal) +
                    (DelayNamedUnfollowButton ? "<div id=\"delayed-channel-control\"></div>" : controls);
            if (platform == PlatformKind.Twitch)
                controls = "<div class=\"channel-info-content\">" + controls + "</div>";
            if (OutsideButton) controls += button.Replace("Follow Streamer", "Follow Other", StringComparison.Ordinal);
            var write = platform == PlatformKind.Twitch ? Gql : $"https://kick.com/api/v2/channels/{(WrongMutationTarget ? "other" : "streamer")}/follow";
            var initialize = platform == PlatformKind.Twitch
                ? PartialFollowStateFirst
                    ? $"const read = operationName => fetch({JsonSerializer.Serialize(Gql)}, {{method:'POST',headers:{{'Content-Type':'application/json'}},body:JSON.stringify({{operationName,variables:{{login:'streamer'}}}})}}).then(r=>r.json()); read('PlayerTrackingContextQuery').then(mount).then(()=>setTimeout(()=>read('FollowButton_User'),1200));"
                    : $"fetch({JsonSerializer.Serialize(Gql)}, {{ method: 'POST', headers: {{'Content-Type':'application/json'}}, body: JSON.stringify({{operationName:'FollowButton_FollowData',variables:{{login:'streamer'}}}}) }}).then(r => r.json()).then(mount);"
                : "mount();";
            return $$"""
                <!doctype html><html><body><script>
                const mount = () => {
                    document.body.insertAdjacentHTML('beforeend', {{JsonSerializer.Serialize(controls)}});
                    {{(DelayNamedUnfollowButton ? $"setTimeout(() => document.getElementById('delayed-channel-control').outerHTML = {JsonSerializer.Serialize(button)}, 600);" : "")}}
                    {{(ChangeRoute ? "history.pushState({}, '', '/other');" : "")}}
                };
                function openUnfollow() {
                    window.unfollowOpens = (window.unfollowOpens || 0) + 1;
                    {{(IgnoreFirstUnfollowOpen ? "if (window.unfollowOpens === 1) return;" : "")}}
                    document.body.insertAdjacentHTML('beforeend', '<div role="alertdialog">Unfollow {{(WrongUnfollowDialogName ? "Other" : "Streamer")}}?<button data-a-target="modal-unfollow-button" onclick="submitFollow()">Yes, unfollow</button></div>');
                }
                function submitFollow() {
                    window.followClicks = (window.followClicks || 0) + 1;
                    {{(IgnoreFirstFollowClick ? "if (window.followClicks === 1) return;" : "")}}
                    const send = () => fetch({{JsonSerializer.Serialize(write)}}, {
                        method:'POST', headers:{'Content-Type':'application/json'}, body:{{JsonSerializer.Serialize(Following ? UnfollowRequest(WrongMutationTarget ? "456" : "123") : MutationRequest(WrongMutationTarget ? "456" : "123"))}}
                    });
                    send().then(r => r.json()).then(reply => {
                        if (reply.extensions?.challenge?.type === 'integrity') return send();
                    });
                    {{(ReloadAfterClick ? "setTimeout(() => location.reload(), 0);" : "")}}
                }
                fetch('https://media.test/stream.m3u8', {mode:'no-cors'});
                {{(ThrowAfterClick ? "HTMLButtonElement.prototype.click = function () { submitFollow(); throw new Error('fixture click error'); };" : "")}}
                {{initialize}}
                </script></body></html>
                """;
        }

        private static string ReadRequest(CoreWebView2WebResourceRequest request)
        {
            var stream = request.Content;
            var position = stream.CanSeek ? stream.Position : 0;
            if (stream.CanSeek) stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var body = reader.ReadToEnd();
            if (stream.CanSeek) stream.Position = position;
            else request.Content = new MemoryStream(Encoding.UTF8.GetBytes(body), writable: false);
            return body;
        }
    }
}
