internal static class TestCatalog
{
    private const int CharacterizedTestCount = 714;

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = Build();

    private static IReadOnlyList<(string Name, Func<Task> Run)> Build()
    {
        (string Name, Func<Task> Run)[] characterized =
        [
            .. ApplicationTestCatalog.CoreAndInput,
            .. ApplicationTestCatalog.PlaybackBrowseAndReplay,
            .. ApplicationTestCatalog.OverlayRendering,
            .. ApplicationTestCatalog.ReplayAndLiveChat,
            .. ApplicationTestCatalog.ChatSettingsAndPredictions,
            .. ApplicationTestCatalog.TabsAndWindowing,
            .. ApplicationTestCatalog.HomeAndBrowseUi,
            .. ApplicationTestCatalog.WpfAndNativeWindows,
            .. TestSubsystemCatalog.All
        ];

        if (characterized.Length != CharacterizedTestCount)
        {
            throw new InvalidOperationException(
                $"The characterized test catalog must contain exactly {CharacterizedTestCount} tests; found {characterized.Length}.");
        }

        (string Name, Func<Task> Run)[] all =
            [
                .. characterized,
                .. UpdateModernizationTestCatalog.RefreshTests,
                .. UpdateModernizationTestCatalog.ReleaseTests,
                .. ApplicationTestCatalog.ReplaySeekOverlayTests,
                .. ApplicationTestCatalog.ResponsiveLayoutTests,
                .. ApplicationTestCatalog.TabSwitchVideoBoundsTests,
                .. ApplicationTestCatalog.ResizeFlashTests,
                .. ApplicationTestCatalog.VolumeOverlaySizingTests,
                .. ApplicationTestCatalog.PictureInPictureResizeTests,
                .. ApplicationTestCatalog.PictureInPictureResizeVlcTests,
                .. ApplicationTestCatalog.PictureInPictureActivationTests,
                .. ApplicationTestCatalog.PictureInPictureContextMenuTests,
                .. ApplicationTestCatalog.PictureInPictureContextMenuSurfaceTests,
                .. ApplicationTestCatalog.PictureInPictureContextMenuForeignForegroundTests,
                .. PictureInPictureContextMenuDispatchTestCatalog.All,
                .. NativePictureInPictureContextMenuTargetTestCatalog.All,
                .. AudioSwitchVlcTestCatalog.All,
                .. NeverMuteTabTestCatalog.All,
                .. BootstrapperTestCatalog.All,
                .. MaintenanceTestCatalog.All,
                .. RegressionTestCatalog.All,
                .. CodeReviewTestCatalog.All,
                .. ReviewFollowupTestCatalog.All,
                .. RepositoryReviewTestCatalog.All,
                .. PagingRegressionTestCatalog.All,
                .. VodChatLifecycleTestCatalog.All,
                .. AppLifecycleTestCatalog.All,
                .. HotkeySettingsExpansionTestCatalog.All,
                .. MouseWheelDispatchTestCatalog.All,
                .. ApplicationTestCatalog.VolumeWheelInputTests,
                .. NativeMouseWheelTargetTestCatalog.All,
                .. PlaybackHotkeyIntegrationTestCatalog.All,
                .. ApplicationTestCatalog.BackHotkeyNativeVlcTests,
                .. ApplicationTestCatalog.WindowSharingVlcTests,
                .. ApplicationTestCatalog.NavigationTests,
                .. ApplicationTestCatalog.BrowseScrollTests,
                .. AnimatedEmoteDecodingTestCatalog.All,
                .. ResourceUsageTestCatalog.All,
                .. WorkflowResourceTestCatalog.All,
                .. RecentRefreshResourceTestCatalog.All,
                .. HomeRefreshResourceTestCatalog.All,
                .. BrowseStreamResourceTestCatalog.All,
                .. PagedRefreshResourceTestCatalog.All,
                .. TwitchMutedVodRepairTestCatalog.All,
                .. TwitchChannelPointsTestCatalog.All,
                .. StreamSearchTestCatalog.All,
                .. LiveChannelPayloadTestCatalog.All,
                .. SettingsRecoveryTestCatalog.All,
                .. TestRunnerValidationTestCatalog.All,
                .. ReviewContinuationTestCatalog.All,
                .. ServiceResilienceTestCatalog.All,
                .. ReplayChatResilienceTestCatalog.All,
                .. ApplicationTestCatalog.PauseSeekbarTests,
                .. ApplicationTestCatalog.ReplaySeekChatTimingTests,
                .. PollingLifecycleTestCatalog.All,
                .. TwitchPayloadValidationTestCatalog.All,
                .. TimeoutRecoveryTestCatalog.All
            ];
        var duplicate = all
            .GroupBy(test => test.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Test '{duplicate.Key}' is registered more than once.");
        }

        return all;
    }
}
