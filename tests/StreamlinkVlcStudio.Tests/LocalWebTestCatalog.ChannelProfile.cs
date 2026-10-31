using System.Net;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task PanelOnlyChannelProfileAsync()
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "channel-profiles", "imperialhal-2026-10-22.json"));
        var requests = 0;
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            requests++;
            Assert.True(request.Headers.Authorization is null);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.Contains("imperialhal__", body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.Contains("socialMedias", StringComparison.Ordinal)
                    ? """{"errors":[{"message":"failed integrity check"}]}""" : fixture)
            };
        }));
        var profiles = new LocalWebChannelProfileClient(client);
        var target = StreamInputParser.FromChannel(PlatformKind.Twitch, "imperialhal__");
        var profile = await profiles.GetAsync(target, CancellationToken.None);
        Assert.Equal("ImperialHal__", profile.DisplayName);
        Assert.Equal("", profile.Description);
        Assert.Equal(10, profile.Panels.Count);
        Assert.True(profile.HasExtensions);
        Assert.Contains("Coupon code", profile.Panels[4].Description);
        Assert.Equal("", profile.Panels[6].Title);
        Assert.Contains("Hi! My name is Phillip", profile.Panels[6].Description);
        Assert.Equal("https://twitter.com/ImperialHal", profile.Panels[6].LinkUrl);
        Assert.Equal(profile, await profiles.GetAsync(target, CancellationToken.None));
        Assert.Equal(2, requests);
    }

    private static async Task ChannelProfileAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            requests++;
            Assert.True(request.Headers.Authorization is null);
            if (request.RequestUri!.Host == "kick.com")
                return Json("""{"user":{"username":"Kick creator","bio":"Kick bio","profile_pic":"https://images.example/kick.png","twitter":"creator"},"followers_count":1234,"livestream":{"session_title":"Kick title","viewer_count":42,"categories":[{"name":"Music"}],"tags":["English"]}}""");
            Assert.Equal("gql.twitch.tv", request.RequestUri.Host);
            var body = await request.Content!.ReadAsStringAsync(token);
            if (body.Contains("socialMedias", StringComparison.Ordinal))
                return Json(body.Contains("corrupt", StringComparison.Ordinal)
                    ? "{" : """{"errors":[{"message":"failed integrity check"}],"data":{"user":{"channel":{"socialMedias":null}}}}""");
            return Json("""
                {"data":{"user":{"displayName":"Creator","description":"Actual public bio <script>text only</script>",
                "profileImageURL":"https://images.example/avatar.png","followers":{"totalCount":6784000},"roles":{"isPartner":true},
                "broadcastSettings":{"title":"Last title"},"stream":{"title":"Live title","viewersCount":8325,"createdAt":"2026-10-22T12:00:00Z","game":{"name":"Example game"},"freeformTags":[{"name":"English"}]},
                "panels":[{"type":"DEFAULT","title":"Social","description":"**Hello** [social](https://twitter.com/creator)","imageURL":"https://panels.twitch.tv/real-image","linkURL":"https://twitter.com/creator"},
                {"type":"DEFAULT","title":"Unsafe","imageURL":"data:image/svg+xml,unsafe","linkURL":"javascript:alert(1)"},
                {"type":"DEFAULT","title":"HTTP sponsor","linkURL":"http://sponsor.example/creator"},{"type":"EXTENSION"}]}}}
                """);
        }));
        var profiles = new LocalWebChannelProfileClient(client);
        var target = StreamInputParser.FromChannel(PlatformKind.Twitch, "creator");
        var profile = await profiles.GetAsync(target, CancellationToken.None);
        Assert.Equal("Creator", profile.DisplayName); Assert.Contains("Actual public bio", profile.Description);
        Assert.Equal(6784000L, profile.Followers!.Value); Assert.Equal(8325L, profile.Viewers!.Value);
        Assert.Equal("Live title", profile.Title); Assert.Equal("Example game", profile.Category);
        Assert.True(profile.IsPartner); Assert.True(profile.HasExtensions);
        Assert.Equal("English", profile.Tags.Single()); Assert.Equal(3, profile.Panels.Count);
        Assert.Equal("https://panels.twitch.tv/real-image", profile.Panels[0].ImageUrl);
        Assert.Equal("", profile.Panels[1].ImageUrl); Assert.Equal("", profile.Panels[1].LinkUrl);
        Assert.Equal("http://sponsor.example/creator", profile.Panels[2].LinkUrl);
        Assert.Equal("Twitter", profile.Links.Single().Title);
        Assert.Equal(profile, await profiles.GetAsync(target, CancellationToken.None)); Assert.Equal(2, requests);
        var corruptSocial = await profiles.GetAsync(StreamInputParser.FromChannel(PlatformKind.Twitch, "corrupt"), CancellationToken.None);
        Assert.Equal("Creator", corruptSocial.DisplayName);
        Assert.Equal("Twitter", corruptSocial.Links.Single().Title);
        var kick = await profiles.GetAsync(StreamInputParser.FromChannel(PlatformKind.Kick, "creator"), CancellationToken.None);
        Assert.Equal("Kick creator", kick.DisplayName); Assert.Equal("Kick bio", kick.Description);
        Assert.Equal("Music", kick.Category); Assert.Equal(42L, kick.Viewers!.Value);
        Assert.Equal("https://twitter.com/creator", kick.Links.Single().Url);

        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
