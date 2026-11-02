using System.Net.Sockets;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Logging;

internal static partial class CredentialSecurityTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("credential security: token requests never follow redirects", TokenRedirectsAsync),
        ("credential security: Kick app token errors never log provider text", AppTokenErrorsAsync),
        ("credential security: Kick app tokens preserve caching and cancellation", AppTokenSuccessAsync),
        ("credential security: logs redact complete quoted secrets and PKCE verifiers", QuotedSecretsAsync),
        ("credential security: hardening redacts passwords and complete credential headers", CredentialHeadersAsync),
        ("credential security: hardening keeps curl isolated and HTTPS only", CurlIsolationAsync),
        ("credential security: OAuth relay keeps tokens out of callback URLs", OAuthRelayAsync),
        ("credential security: OAuth POST rejects malformed and oversized callbacks", OAuthPostValidationAsync),
        ("credential security: stalled OAuth uploads do not block sign-in", OAuthPostTimeoutAsync),
        ("credential security: OAuth form bodies enforce size, encoding and cancellation", OAuthFormBodyAsync),
        ("credential security: browser completes the OAuth relay under its CSP", OAuthBrowserRelayAsync),
        ("replay URL security: rejects special IPv6 ranges and translated private addresses", SpecialAddressRangesAsync)
    ];

    private static async Task TokenRedirectsAsync()
    {
        foreach (var status in new[] { 301, 302, 303, 307, 308 })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var source = new TcpListener(IPAddress.Loopback, 0);
            using var destination = new TcpListener(IPAddress.Loopback, 0);
            source.Start();
            destination.Start();
            var sourceUri = new Uri($"http://127.0.0.1:{((IPEndPoint)source.LocalEndpoint).Port}/token");
            var destinationUri = new Uri($"http://127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port}/capture");
            var redirected = RespondAsync(destination, 200, null, deadline.Token);
            var original = RespondAsync(source, status, destinationUri, deadline.Token);
            try
            {
                using var http = KickOAuthService.CreateTokenHttpClient();
                using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_secret"] = "redirect-secret" });
                using var response = await http.PostAsync(sourceUri, body, deadline.Token);
                Assert.Equal((HttpStatusCode)status, response.StatusCode);
                Assert.Equal(sourceUri, response.RequestMessage!.RequestUri);
                Assert.Contains("client_secret=redirect-secret", Encoding.UTF8.GetString((await original).Body));
                Assert.Equal(false, redirected.IsCompletedSuccessfully);
            }
            finally
            {
                await deadline.CancelAsync();
                await Task.WhenAll(ObserveAsync(original), ObserveAsync(redirected));
            }
        }
    }

    private static async Task<LocalHttpRequest> RespondAsync(TcpListener listener, int status, Uri? location, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        using var stream = connection.GetStream();
        var result = await LocalHttpRequestReader.ReadWithStatusAsync(stream, 8192, token);
        var request = result.Request ?? throw new InvalidDataException(result.Message);
        var header = $"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n" +
            (location is null ? "" : $"Location: {location.AbsoluteUri}\r\n") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
        return request;
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static async Task AppTokenErrorsAsync()
    {
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.TemporaryRedirect, HttpStatusCode.InternalServerError })
        {
            var secret = "private-client-secret-" + Guid.NewGuid().ToString("N");
            var settings = new ChatSettings { KickClientId = Guid.NewGuid().ToString("N"), KickClientSecret = secret };
            var logger = new MemoryLogger();
            var calls = 0;
            HttpClient CreateClient() => new(new FakeHttpMessageHandler(request =>
            {
                calls++;
                Assert.Equal("https://id.kick.com/oauth/token", request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(status)
                {
                    ReasonPhrase = secret,
                    Content = new StringContent(JsonSerializer.Serialize(new { error_description = secret }))
                };
            }));
            Assert.True(await KickOAuthService.TryGetAppAccessTokenAsync(settings, CreateClient, logger) is null);
            Assert.True(await KickOAuthService.TryGetAppAccessTokenAsync(settings, CreateClient, logger) is null);
            Assert.Equal(2, calls);
            Assert.Equal(2, logger.Entries.Count);
            foreach (var entry in logger.Entries)
            {
                Assert.Contains($"HTTP {(int)status}", entry.Message);
                Assert.DoesNotContain(secret, entry.Message);
            }
        }
    }

    private static async Task AppTokenSuccessAsync()
    {
        var settings = new ChatSettings { KickClientId = Guid.NewGuid().ToString("N"), KickClientSecret = "app-secret" };
        var calls = 0;
        HttpClient CreateClient() => new(new FakeHttpMessageHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"app-token","expires_in":3600}""")
            };
        }));
        Assert.Equal("app-token", await KickOAuthService.TryGetAppAccessTokenAsync(settings, CreateClient));
        Assert.Equal("app-token", await KickOAuthService.TryGetAppAccessTokenAsync(settings, CreateClient));
        Assert.Equal(1, calls);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            KickOAuthService.TryGetAppAccessTokenAsync(settings, CreateClient, cancellationToken: canceled.Token));
        Assert.Equal(1, calls);
    }

    private static async Task QuotedSecretsAsync()
    {
        string[] inputs =
        [
            """{"client_secret":"prefix exposed-suffix,with&delimiters", "quality":"best"}""",
            """refresh_token='prefix exposed-suffix,with&delimiters' quality=best""",
            """access_token="prefix \"escaped-quote\" exposed-suffix" quality=best""",
            """Authorization: "Bearer prefix exposed-suffix" quality=best""",
            "client_secret=\"prefix exposed-suffix",
            "client_secret='prefix exposed-suffix\\",
            "client_secret=\"prefix\r\nexposed-suffix\" quality=best",
            "code_verifier=private-pkce-verifier quality=best",
            "https://example.com/?code_verifier=private-pkce-verifier&quality=best"
        ];
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "credential-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var logger = new FileAppLogger(directory))
            {
                foreach (var input in inputs)
                {
                    var result = FileAppLogger.Sanitize(input, 4096);
                    Assert.DoesNotContain("exposed-suffix", result);
                    Assert.DoesNotContain("escaped-quote", result);
                    Assert.DoesNotContain("private-pkce-verifier", result);
                    Assert.Contains("[REDACTED]", result);
                    if (input.Contains("quality")) Assert.Contains("quality", result);
                    logger.Write(AppLogLevel.Warning, input, input, new InvalidOperationException(input));
                }
                await logger.FlushAsync();
            }
            var content = string.Join("", Directory.GetFiles(directory, "studio*.log").Select(File.ReadAllText));
            Assert.Contains("[REDACTED]", content);
            Assert.DoesNotContain("exposed-suffix", content);
            Assert.DoesNotContain("escaped-quote", content);
            Assert.DoesNotContain("private-pkce-verifier", content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
