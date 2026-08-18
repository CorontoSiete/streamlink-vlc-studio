internal static class TestCatalog
{
    private const int CharacterizedTestCount = 713;

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

        (string Name, Func<Task> Run)[] all = [.. characterized, .. RegressionTestCatalog.All];
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
