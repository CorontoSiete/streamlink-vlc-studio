internal static class NotificationIdentityTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("notification identity: punctuation distinguishes Kick channels", PunctuationIsPreserved),
        ("notification identity: long channel names remain distinct", LongChannelsAreDistinct),
        ("notification identity: channel casing is normalized and platforms remain separate", CanonicalIdentity)
    ];

    private static Task PunctuationIsPreserved()
    {
        var tags = new[] { "channel.name", "channel-name", "channel_name" }
            .Select(channel => Tag(PlatformKind.Kick, channel))
            .ToArray();
        Assert.Equal(tags.Length, tags.Distinct(StringComparer.Ordinal).Count());
        return Task.CompletedTask;
    }

    private static Task LongChannelsAreDistinct()
    {
        var prefix = new string('a', 79);
        var first = Tag(PlatformKind.Kick, prefix + "1");
        var second = Tag(PlatformKind.Kick, prefix + "2");
        Assert.True(first.Length is > 0 and <= 16);
        Assert.True(second.Length is > 0 and <= 16);
        Assert.True(first != second);
        return Task.CompletedTask;
    }

    private static Task CanonicalIdentity()
    {
        var tag = Tag(PlatformKind.Kick, "channel");
        Assert.Equal(tag, Tag(PlatformKind.Kick, " CHANNEL "));
        Assert.True(tag != Tag(PlatformKind.Twitch, "channel"));
        return Task.CompletedTask;
    }

    private static string Tag(PlatformKind platform, string channel) =>
        ToastLiveNotificationService.BuildTag(new(platform, channel, "Display name", "Title", "Category", 1, ""));
}
