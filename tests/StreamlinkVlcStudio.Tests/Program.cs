if (args.Contains("--owned-process-fixture", StringComparer.Ordinal))
    return await OwnedProcessTestHost.RunAsync(args);
if (args.Contains("--twitch-playback-fixture", StringComparer.Ordinal))
    return await TwitchPlaybackAuthenticationTestCatalog.RunFixtureAsync(args);
return await DependencyFreeTestRunner.RunAsync(TestCatalog.All);
