using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamlinkVlcStudio.AccountService;

internal static class AccountConnectionTestCatalog
{
    private static readonly AccountConnectionConfiguration Config = new("app-twitch", "app-kick", "https://accounts.example.com/v1/kick/token");

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("account connection: fresh setup needs no credential input", FreshWizardAsync),
        ("account connection: missing service is explained and can be skipped", MissingServiceAsync),
        ("account connection: defaults preserve personal registrations", DefaultsAsync),
        ("account connection: token service requires a trusted HTTPS URL", UnsafeUrlsAsync),
        ("account connection: Kick code exchange and refresh send no client secret", TokenRequestsAsync),
        ("account connection: mismatched registrations cannot send tokens to the service", MismatchedClientAsync),
        ("account connection: Twitch validates the issuing client before saving", TwitchClientAsync),
        ("account connection: managed Kick tokens rotate without a desktop secret", ManagedRefreshAsync),
        ("account connection: incomplete managed tokens are rejected", IncompleteTokensAsync),
        ("account connection: managed Kick settings round trip with protected tokens", PersistenceAsync),
        ("account connection: service exchanges codes and refreshes with a server secret", ServiceExchangeAsync),
        ("account connection: service rejects malformed requests before contacting Kick", ServiceValidationAsync),
        ("account connection: service suppresses provider errors and incomplete scopes", ServiceErrorsAsync),
        ("account connection: service rate limits token requests", ServiceRateLimitAsync),
        ("account connection: service bounds upstream responses", ServiceResponseLimitAsync),
        ("account connection: service cancels upstream work on disconnect", ServiceCancellationAsync),
        ("account connection: Kick DNS failures explain how to reconnect", DnsFailureAsync)
    ];

    private static Task FreshWizardAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var settings = new AppSettings();
        var wizard = new SetupWizardWindow(settings, new FakeSettingsService(settings), new MemoryLogger(), Config);
        try
        {
            Assert.True(wizard.CanConnectTwitch);
            Assert.True(wizard.CanConnectKick);
            Assert.Equal("Skip for now", wizard.TwitchFooterActionText);
            Assert.Equal("Skip for now", wizard.KickFooterActionText);
            Assert.Equal("", settings.Chat.TwitchClientId);
            Assert.Equal("", settings.Chat.KickClientId);
            Assert.Equal("", settings.Chat.KickClientSecret);
            var expanders = Descendants<Expander>((DependencyObject)wizard.Content).ToArray();
            Assert.Equal(2, expanders.Length);
            Assert.True(expanders.All(expander => !expander.IsExpanded));
            var next = Descendants<Button>((DependencyObject)wizard.Content).Single(button => Equals(button.Content, "Next"));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(wizard.IsTwitchVisible);
            Capture(wizard, "twitch");
            var skip = Descendants<Button>((DependencyObject)wizard.Content).Single(button =>
                BindingOperations.GetBinding(button, ContentControl.ContentProperty)?.Path.Path == nameof(SetupWizardWindow.TwitchFooterActionText));
            skip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(wizard.IsKickVisible);
            Capture(wizard, "kick");
            Assert.Equal("", settings.Chat.TwitchOAuthToken);
        }
        finally { wizard.Close(); }
        return Task.CompletedTask;
    });

    private static Task MissingServiceAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var settings = new AppSettings();
        var wizard = new SetupWizardWindow(settings, new FakeSettingsService(settings), new MemoryLogger(), new("app-twitch", "app-kick"));
        try
        {
            Assert.True(wizard.CanConnectTwitch);
            Assert.Equal(false, wizard.CanConnectKick);
            Assert.Equal(AccountConnectionConfiguration.KickUnavailableMessage, wizard.KickConnectionHelp);
            Assert.Equal("Skip for now", wizard.KickFooterActionText);
            var next = Descendants<Button>((DependencyObject)wizard.Content).Single(button => Equals(button.Content, "Next"));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Capture(wizard, "kick-unavailable");
        }
        finally { wizard.Close(); }
        return Task.CompletedTask;
    });

    private static Task DefaultsAsync()
    {
        var settings = new ChatSettings();
        Assert.Equal("app-twitch", Config.ResolveTwitchClientId(settings));
        Assert.True(Config.CanConnectKick(settings));
        settings.TwitchClientId = "personal-twitch";
        settings.KickClientId = "personal-kick";
        Assert.Equal("personal-twitch", Config.ResolveTwitchClientId(settings));
        Assert.Equal(false, Config.CanConnectKick(settings));
        settings.KickClientSecret = "personal-secret";
        Assert.True(Config.CanConnectKick(settings));
        return Task.CompletedTask;
    }

    private static Task UnsafeUrlsAsync()
    {
        foreach (var url in new[] { "", "http://accounts.example.com/token", "https://localhost/token", "https://127.0.0.1/token", "https://user:password@accounts.example.com/token", "https://accounts.example.com/token?target=other", "https://accounts.example.com/token#fragment", "file:///C:/tokens" })
            Assert.Equal(false, (Config with { KickTokenServiceUrl = url }).TryGetKickTokenServiceUri(out _));
        Assert.True(Config.TryGetKickTokenServiceUri(out _));
        return Task.CompletedTask;
    }

    private static async Task TokenRequestsAsync()
    {
        var settings = new ChatSettings { KickClientId = "app-kick", KickUseTokenService = true };
        using var code = KickOAuthService.CreateTokenRequest(settings, "authorization_code", "code", new string('x', 43), Config);
        Assert.Equal(Config.KickTokenServiceUrl, code.RequestUri!.AbsoluteUri);
        var codeForm = await ReadFormAsync(code);
        Assert.Equal(false, codeForm.ContainsKey("client_secret"));
        Assert.Equal("http://localhost:39177", codeForm["redirect_uri"]);
        Assert.Equal(new string('x', 43), codeForm["code_verifier"]);
        using var refresh = KickOAuthService.CreateTokenRequest(settings, "refresh_token", "refresh", null, Config);
        var refreshForm = await ReadFormAsync(refresh);
        Assert.Equal(3, refreshForm.Count);
        Assert.Equal("refresh", refreshForm["refresh_token"]);
        settings.KickUseTokenService = false;
        settings.KickClientSecret = "personal-secret";
        using var personal = KickOAuthService.CreateTokenRequest(settings, "refresh_token", "refresh", null, Config);
        Assert.Equal("https://id.kick.com/oauth/token", personal.RequestUri!.AbsoluteUri);
        Assert.Equal("personal-secret", (await ReadFormAsync(personal))["client_secret"]);
    }

    private static Task MismatchedClientAsync()
    {
        var settings = new ChatSettings { KickClientId = "different-client", KickUseTokenService = true };
        Assert.Throws<InvalidOperationException>(() => KickOAuthService.CreateTokenRequest(settings, "refresh_token", "private-refresh", null, Config));
        return Task.CompletedTask;
    }

    private static Task TwitchClientAsync()
    {
        var info = new TwitchTokenInfo("viewer", "42", "other", null, [], true, true, true, true, true);
        Assert.Throws<InvalidOperationException>(() => TwitchOAuthService.RequireMatchingClientId("app-twitch", info));
        var settings = new ChatSettings();
        TwitchOAuthService.ApplyTokenResult(settings, new("token", "viewer", null, "Bearer", [], "app-twitch"));
        Assert.Equal("app-twitch", settings.TwitchClientId);
        Assert.Equal("viewer", settings.TwitchUsername);
        return Task.CompletedTask;
    }

    private static async Task ManagedRefreshAsync()
    {
        var settings = new ChatSettings();
        KickOAuthService.ApplyTokenResult(settings, new("expired", "refresh", DateTimeOffset.UnixEpoch, "Bearer", ["chat:write"], "app-kick", true));
        var coordinator = new KickUserTokenCoordinator((snapshot, _) =>
        {
            Assert.True(snapshot.KickUseTokenService);
            Assert.Equal("", snapshot.KickClientSecret);
            return Task.FromResult(new KickOAuthTokenResult("fresh", "rotated", DateTimeOffset.UtcNow.AddHours(1), "Bearer", ["chat:write"], "app-kick", true));
        });
        Assert.Equal("fresh", await coordinator.ResolveAsync(settings, (target, result, _) =>
        { KickOAuthService.ApplyTokenResult(target, result); return Task.CompletedTask; }, null, default));
        Assert.Equal("rotated", settings.KickRefreshToken);
        Assert.True(settings.KickUseTokenService);
        KickOAuthService.ClearToken(settings);
        Assert.Equal(false, settings.KickUseTokenService);
        Assert.Equal("", settings.KickRefreshToken);
    }

    private static async Task PersistenceAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-accounts-{Guid.NewGuid():N}", "settings.json");
        try
        {
            var settings = new AppSettings();
            KickOAuthService.ApplyTokenResult(settings.Chat, new("private-access", "private-refresh", null, "Bearer", ["chat:write"], "app-kick", true));
            var service = new JsonSettingsService(path);
            await service.SaveAsync(settings);
            var json = await File.ReadAllTextAsync(path);
            Assert.Equal(false, json.Contains("private-access", StringComparison.Ordinal));
            Assert.Equal(false, json.Contains("private-refresh", StringComparison.Ordinal));
            var loaded = await service.LoadAsync();
            Assert.True(loaded.Chat.KickUseTokenService);
            Assert.Equal("private-refresh", loaded.Chat.KickRefreshToken);
            Assert.Equal("", loaded.Chat.KickClientSecret);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!); }
    }

    private static Task IncompleteTokensAsync()
    {
        foreach (var body in new[]
        {
            """{"access_token":"token","refresh_token":"refresh","expires_in":3600,"scope":"chat:write"}""",
            """{"access_token":"token","expires_in":3600,"scope":"user:read channel:read chat:write"}""",
            """{"access_token":"token","refresh_token":"refresh","scope":"user:read channel:read chat:write"}"""
        }) Assert.Throws<InvalidOperationException>(() => KickOAuthService.ParseTokenResponse(body, null, true));
        return Task.CompletedTask;
    }

    private static async Task DnsFailureAsync()
    {
        using var http = new HttpClient(new AsyncHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known"))));
        using var request = new HttpRequestMessage(HttpMethod.Post, Config.KickTokenServiceUrl);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KickOAuthService.SendTokenRequestAsync(http, request, null, default, true));
        Assert.Contains("DNS", exception.Message);
        Assert.Contains("accounts.example.com", exception.Message);
        Assert.Contains("try Connect Kick again", exception.Message);
    }

    private static void Capture(SetupWizardWindow wizard, string platform)
    {
        if (Environment.GetEnvironmentVariable("SVS_ACCOUNT_SETUP_CAPTURE") is not { Length: > 0 } directory) return;
        var root = (FrameworkElement)wizard.Content;
        root.Measure(new Size(wizard.Width, wizard.Height));
        root.Arrange(new Rect(0, 0, wizard.Width, wizard.Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)wizard.Width, (int)wizard.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, $"setup-{platform}.png"));
        encoder.Save(output);
    }

    private static async Task ServiceExchangeAsync()
    {
        var calls = 0;
        await using var fixture = await ServiceFixture.CreateAsync(async (request, _) =>
        {
            calls++;
            Assert.Equal("https://id.kick.com/oauth/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal("server-secret", (await ReadFormAsync(request))["client_secret"]);
            return ProviderToken();
        });
        using var code = await fixture.Client.PostAsync("/v1/kick/token", Form("authorization_code"));
        Assert.Equal(HttpStatusCode.OK, code.StatusCode);
        Assert.True(code.Headers.CacheControl!.NoStore);
        Assert.Equal(false, (await code.Content.ReadAsStringAsync()).Contains("server-secret", StringComparison.Ordinal));
        using var refresh = await fixture.Client.PostAsync("/v1/kick/token", Form("refresh_token"));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(2, calls);
    }

    private static async Task ServiceValidationAsync()
    {
        var calls = 0;
        await using var fixture = await ServiceFixture.CreateAsync((_, _) => { calls++; return Task.FromResult(ProviderToken()); });
        var forms = new List<Dictionary<string, string>>
        {
            new() { ["client_id"] = "other", ["grant_type"] = "refresh_token", ["refresh_token"] = "refresh" },
            new() { ["client_id"] = "app-kick", ["grant_type"] = "client_credentials" },
            new() { ["client_id"] = "app-kick", ["grant_type"] = "refresh_token", ["refresh_token"] = "refresh", ["client_secret"] = "injected" },
            new() { ["client_id"] = "app-kick", ["grant_type"] = "authorization_code", ["code"] = "code", ["code_verifier"] = "short", ["redirect_uri"] = "http://localhost:39177" },
            new() { ["client_id"] = "app-kick", ["grant_type"] = "authorization_code", ["code"] = "code", ["code_verifier"] = new string('x', 43), ["redirect_uri"] = "https://attacker.example" }
        };
        foreach (var form in forms)
        {
            using var response = await fixture.Client.PostAsync("/v1/kick/token", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var duplicate = await fixture.Client.PostAsync("/v1/kick/token", new StringContent("client_id=app-kick&grant_type=refresh_token&refresh_token=one&refresh_token=two", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var unsupported = await fixture.Client.PostAsync("/v1/kick/token", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal(0, calls);
    }

    private static async Task ServiceErrorsAsync()
    {
        await using var fixture = await ServiceFixture.CreateAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("provider error echoes server-secret and private-refresh") }));
        using var response = await fixture.Client.PostAsync("/v1/kick/token", Form("refresh_token"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(false, body.Contains("server-secret", StringComparison.Ordinal));
        Assert.Equal(false, body.Contains("private-refresh", StringComparison.Ordinal));
        await using var incomplete = await ServiceFixture.CreateAsync((_, _) => Task.FromResult(ProviderToken("chat:write")));
        using var missing = await incomplete.Client.PostAsync("/v1/kick/token", Form("authorization_code"));
        Assert.Equal(HttpStatusCode.BadGateway, missing.StatusCode);
    }

    private static async Task ServiceRateLimitAsync()
    {
        await using var fixture = await ServiceFixture.CreateAsync((_, _) => Task.FromResult(ProviderToken()));
        for (var i = 0; i < 30; i++)
        {
            using var response = await fixture.Client.PostAsync("/v1/kick/token", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var throttled = await fixture.Client.PostAsync("/v1/kick/token", Form("refresh_token"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    private static HttpResponseMessage ProviderToken(string scopes = "user:read channel:read chat:write") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "access", refresh_token = "rotated", expires_in = 3600, scope = scopes, client_secret = "server-secret" }), Encoding.UTF8, "application/json") };

    private static async Task ServiceResponseLimitAsync()
    {
        await using var fixture = await ServiceFixture.CreateAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 65_537)) }));
        using var response = await fixture.Client.PostAsync("/v1/kick/token", Form("refresh_token"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private static async Task ServiceCancellationAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await ServiceFixture.CreateAsync(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { if (cancellationToken.IsCancellationRequested) canceled.TrySetResult(); }
            return ProviderToken();
        });
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Client.PostAsync("/v1/kick/token", Form("refresh_token"), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static FormUrlEncodedContent Form(string grant) => new(grant == "refresh_token"
        ? new Dictionary<string, string> { ["client_id"] = "app-kick", ["grant_type"] = grant, ["refresh_token"] = "private-refresh" }
        : new Dictionary<string, string> { ["client_id"] = "app-kick", ["grant_type"] = grant, ["code"] = "code", ["code_verifier"] = new string('x', 43), ["redirect_uri"] = "http://localhost:39177" });

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpRequestMessage request)
    {
        Assert.True(OAuthTokenHelpers.TryParseQueryString(await request.Content!.ReadAsStringAsync(), out var form));
        return form;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class ServiceFixture(WebApplication app, HttpClient provider, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public static async Task<ServiceFixture> CreateAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            var provider = new HttpClient(new AsyncHttpMessageHandler(handler));
            var app = AccountServiceHost.Build([], builder =>
            {
                builder.Configuration["Kick:ClientId"] = "app-kick";
                builder.Configuration["Kick:ClientSecret"] = "server-secret";
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton<IHttpClientFactory>(new FixedClientFactory(provider));
            });
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            return new(app, provider, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            provider.Dispose();
        }
    }

    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
