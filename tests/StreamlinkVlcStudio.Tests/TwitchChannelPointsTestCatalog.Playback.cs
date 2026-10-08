internal static partial class TwitchChannelPointsTestCatalog
{
    private static Task BrowserPlaybackTokenAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var fixture = await NativeBrowserFixture.CreateAsync();
        Assert.Equal("fixture", await fixture.Browser.GetPlaybackOAuthTokenAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Controllers.Count);
        var core = fixture.Controllers.Single().CoreWebView2;
        Assert.Equal("about:blank", core.Source);
        Assert.Equal(0, fixture.Owner.OwnedWindows.Count);
        await fixture.Browser.SignOutAsync(CancellationToken.None);
        Assert.True(await fixture.Browser.GetPlaybackOAuthTokenAsync(CancellationToken.None) is null);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Browser.GetPlaybackOAuthTokenAsync(canceled.Token));
        fixture.Browser.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Browser.GetPlaybackOAuthTokenAsync(CancellationToken.None));
    });
}
