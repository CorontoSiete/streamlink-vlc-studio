using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Twitch;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal sealed class TwitchCategoryViewerCountClient(HttpClient httpClient)
{
    internal const int MaximumBatchSize = 20;
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);
    // Twitch's directory cards use Game.viewersCount. Request that reported total
    // instead of rebuilding it from hundreds of independently changing stream pages.
    private const string ViewerCountQuery = """
        query StreamStudioCategoryViewerCounts($id: ID!) {
          game(id: $id) { id viewersCount }
        }
        """;

    internal async Task<IReadOnlyList<BrowseCategoryViewerCount>> GetAsync(
        IReadOnlyList<string> categoryIds,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(categoryIds.Select(id => new
        {
            query = ViewerCountQuery,
            variables = new { id }
        }).ToArray());
        // Keep a stalled website request from consuming the browse client's full
        // timeout. This deadline does not cancel the caller or the Helix fallback.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LookupTimeout);
        using var document = await new TwitchGraphQlTransport(httpClient).SendAsync(
            payload,
            TwitchGraphQlTransport.PublicClientId,
            TwitchGraphQlTransport.CreateDeviceId(),
            deadline.Token).ConfigureAwait(false);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != categoryIds.Count)
        {
            throw new JsonException("Twitch category totals did not include every requested category.");
        }

        var counts = new List<BrowseCategoryViewerCount>(categoryIds.Count);
        for (var index = 0; index < categoryIds.Count; index++)
        {
            var operation = root[index];
            if (operation.ValueKind == JsonValueKind.Object &&
                operation.TryGetProperty("errors", out var errors) &&
                (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
            {
                throw new JsonException("Twitch category totals included an unsuccessful result.");
            }

            if (operation.ValueKind != JsonValueKind.Object ||
                !operation.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.Object ||
                !game.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), categoryIds[index], StringComparison.Ordinal) ||
                !game.TryGetProperty("viewersCount", out var countValue) || countValue.ValueKind != JsonValueKind.Number ||
                !countValue.TryGetInt32(out var count) || count < 0)
            {
                throw new JsonException($"Twitch did not return a valid viewer total for category '{categoryIds[index]}'.");
            }

            counts.Add(new BrowseCategoryViewerCount(categoryIds[index], count));
        }

        return counts;
    }
}
