using System.Net.Sockets;

internal static partial class CredentialSecurityTestCatalog
{
    private static HttpListener StartOAuthListener(out Uri address)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        address = new Uri($"http://127.0.0.1:{port}/");
        var listener = new HttpListener();
        listener.Prefixes.Add(address.AbsoluteUri);
        listener.Start();
        return listener;
    }

    private static Task<string> ReceiveOAuthPostAsync(HttpListener listener) =>
        LoopbackOAuthReceiver.WaitForResultAsync(listener, "Twitch", "/twitch-oauth-token", "expected-state",
            TimeSpan.FromSeconds(15), values => values["access_token"], CancellationToken.None,
            TwitchOAuthService.TryHandleFragmentCaptureRequestAsync, useFormPost: true);

    private static FormUrlEncodedContent OAuthForm(string state = "expected-state") => new(new Dictionary<string, string>
    {
        ["state"] = state,
        ["access_token"] = "private-browser-token",
        ["token_type"] = "bearer"
    });

    private static async Task OAuthRelayAsync()
    {
        using var listener = StartOAuthListener(out var address);
        var receiver = ReceiveOAuthPostAsync(listener);
        using var client = new HttpClient { BaseAddress = address };
        string? previousPolicy = null;
        for (var i = 0; i < 2; i++)
        {
            using var page = await client.GetAsync("");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
            Assert.True(page.Headers.CacheControl!.NoStore);
            var policy = page.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("default-src 'none'", policy);
            Assert.Contains("frame-ancestors 'none'", policy);
            Assert.Contains("connect-src 'self'", policy);
            if (previousPolicy is not null) Assert.True(previousPolicy != policy);
            previousPolicy = policy;
            var html = await page.Content.ReadAsStringAsync();
            var nonce = policy.Split("'nonce-", StringSplitOptions.None)[1].Split('\'')[0];
            Assert.Contains($"<script nonce=\"{nonce}\">", html);
            Assert.Contains("fetch('/twitch-oauth-token',", html);
            Assert.Contains("method: 'POST'", html);
            Assert.Contains("body: parameters", html);
            Assert.DoesNotContain("twitch-oauth-token?", html);
            Assert.DoesNotContain("document.write", html);
        }

        using (var oldCallback = await client.GetAsync("twitch-oauth-token?state=expected-state&access_token=private-browser-token"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, oldCallback.StatusCode);
            Assert.Equal("POST", oldCallback.Content.Headers.Allow.Single());
        }
        Assert.Equal(false, receiver.IsCompleted);
        using var form = OAuthForm();
        using var response = await client.PostAsync("twitch-oauth-token", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", response.RequestMessage!.RequestUri!.Query);
        Assert.Equal("private-browser-token", await receiver);
        Assert.Contains("script-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("private-browser-token", await response.Content.ReadAsStringAsync());
    }

    private static async Task OAuthPostValidationAsync()
    {
        using var listener = StartOAuthListener(out var address);
        var receiver = ReceiveOAuthPostAsync(listener);
        using var client = new HttpClient { BaseAddress = address };
        (string Path, string Body, string ContentType)[] invalid =
        [
            ("twitch-oauth-token", "{}", "application/json"),
            ("twitch-oauth-token?access_token=leaked", "state=expected-state&access_token=token", "application/x-www-form-urlencoded"),
            ("twitch-oauth-token", "state=expected-state&state=expected-state&access_token=token", "application/x-www-form-urlencoded"),
            ("twitch-oauth-token", "state=expected-state&access_token=%ZZ", "application/x-www-form-urlencoded"),
            ("twitch-oauth-token", "state=wrong&error=access_denied", "application/x-www-form-urlencoded"),
            ("twitch-oauth-token", "state=expected-state&access_token=" + new string('x', 16_384), "application/x-www-form-urlencoded")
        ];
        foreach (var (path, body, contentType) in invalid)
        {
            using var content = new StringContent(body, Encoding.UTF8, contentType);
            using var response = await client.PostAsync(path, content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(false, receiver.IsCompleted);
        }

        // Chunked requests must be limited by bytes read, even without a declared length.
        using (var content = new StringContent(new string('x', 16_385), Encoding.UTF8, "application/x-www-form-urlencoded"))
        using (var request = new HttpRequestMessage(HttpMethod.Post, "twitch-oauth-token") { Content = content })
        {
            request.Headers.TransferEncodingChunked = true;
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var form = OAuthForm();
        using var success = await client.PostAsync("twitch-oauth-token", form);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal("private-browser-token", await receiver);
    }

    private static async Task OAuthPostTimeoutAsync()
    {
        using var listener = StartOAuthListener(out var address);
        var receiver = ReceiveOAuthPostAsync(listener);
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(IPAddress.Loopback, address.Port);
        var headers = Encoding.ASCII.GetBytes($"POST /twitch-oauth-token HTTP/1.1\r\nHost: {address.Authority}\r\n" +
            "Content-Type: application/x-www-form-urlencoded\r\nContent-Length: 100\r\n\r\ns");
        await stalled.GetStream().WriteAsync(headers);
        await Task.Delay(100);
        var deadline = Stopwatch.StartNew();
        using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(8) };
        using var form = OAuthForm();
        using var response = await client.PostAsync("twitch-oauth-token", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("private-browser-token", await receiver);
        Assert.True(deadline.Elapsed >= TimeSpan.FromSeconds(2));
    }

    private static async Task OAuthFormBodyAsync()
    {
        const string contentType = "application/x-www-form-urlencoded; charset=UTF-8";
        const string body = "state=expected-state&access_token=private-browser-token";
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Assert.Equal(body, await LoopbackOAuthReceiver.ReadFormBodyAsync(valid, valid.Length, contentType, "", default));
        Assert.True(OAuthTokenHelpers.TryParseQueryString(body, out var values));
        Assert.Equal("private-browser-token", values["access_token"]);
        foreach (var length in new long[] { 16_385, -1 })
        {
            using var oversized = new MemoryStream(new byte[16_385]);
            Assert.True(await LoopbackOAuthReceiver.ReadFormBodyAsync(oversized, length, contentType, "", default) is null);
            if (length > 0) Assert.Equal(0L, oversized.Position);
        }
        foreach (var (type, query) in new[] { ("application/json", ""), (contentType, "?access_token=leaked"), ("", "") })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
            Assert.True(await LoopbackOAuthReceiver.ReadFormBodyAsync(input, input.Length, type, query, default) is null);
            Assert.Equal(0L, input.Position);
        }
        using var invalidUtf8 = new MemoryStream([0xC3, 0x28]);
        Assert.True(await LoopbackOAuthReceiver.ReadFormBodyAsync(invalidUtf8, -1, contentType, "", default) is null);
        using var boundary = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 16_384)));
        Assert.Equal(16_384, (await LoopbackOAuthReceiver.ReadFormBodyAsync(boundary, -1, contentType, "", default))!.Length);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var stalled = new StalledOAuthBody();
        Assert.True(await LoopbackOAuthReceiver.ReadFormBodyAsync(stalled, -1, contentType, "", cancellation.Token) is null);
        Assert.True(cancellation.IsCancellationRequested);
    }

    private sealed class StalledOAuthBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static async Task SpecialAddressRangesAsync()
    {
        string[] denied =
        [
            "::", "::1", "::127.0.0.1", "::10.0.0.1", "::ffff:127.0.0.1", "::ffff:192.168.1.1",
            "64:ff9b::127.0.0.1", "64:ff9b::10.0.0.1", "64:ff9b::169.254.169.254", "64:ff9b:1::1",
            "100::1", "100:0:0:1::1", "2001::1", "2001:2::1", "2001:1ff:ffff::1", "2001:db8::1",
            "2002:7f00:1::1", "2002:c0a8:101::1", "3fff::1", "3fff:fff:ffff::1", "5f00::1",
            "fc00::1", "fdff::1", "fe80::1", "fec0::1", "ff02::1", "2606:4700::1111%12", "::ffff:93.184.216.34%12"
        ];
        var uri = new Uri("https://stream.kick.com/replay/index.m3u8");
        foreach (var value in denied)
        {
            var address = IPAddress.Parse(value);
            Assert.Equal(false, ReplayUrlSecurityValidator.IsPublicAddress(address));
            var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { address }));
            await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(uri, PlatformKind.Kick));
        }
        foreach (var value in new[] { "93.184.216.34", "::ffff:93.184.216.34", "64:ff9b::93.184.216.34",
            "2606:4700::1111", "2001:4860:4860::8888", "2a00:1450::1", "2001:200::1", "3fff:1000::1" })
        {
            var address = IPAddress.Parse(value);
            Assert.True(ReplayUrlSecurityValidator.IsPublicAddress(address));
            var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { address }));
            Assert.Equal(uri, await validator.ValidateAsync(uri, PlatformKind.Kick));
        }
    }
}
