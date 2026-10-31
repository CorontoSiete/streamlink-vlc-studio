using System.Net;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal sealed partial class KickBrowseProvider
{
    /// <summary>Owns and deduplicates bounded detail requests for one category page.</summary>
    private sealed class KickCategoryDetailRequests : IAsyncDisposable
    {
        private readonly KickBrowseProvider owner;
        private readonly string accessToken;
        private readonly CancellationTokenSource cancellation;
        private readonly SemaphoreSlim throttle = new(KickCategoryDetailConcurrency);
        private readonly Dictionary<string, Task<KickCategoryDetailResponse>> requests = new(StringComparer.Ordinal);

        internal KickCategoryDetailRequests(KickBrowseProvider owner, string accessToken, CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.accessToken = accessToken;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        internal void Start(IReadOnlyList<BrowseCategory> categories)
        {
            foreach (var category in categories)
            {
                if (category.ViewerCount is null) _ = GetAsync(category);
            }
        }

        // Scheduling runs on the single page-loading continuation. HTTP completions
        // never mutate this dictionary, and all requests share one four-slot throttle.
        internal Task<KickCategoryDetailResponse> GetAsync(BrowseCategory category)
        {
            if (!requests.TryGetValue(category.Id, out var request))
            {
                request = LoadAsync(category);
                requests.Add(category.Id, request);
            }
            return request;
        }

        private async Task<KickCategoryDetailResponse> LoadAsync(BrowseCategory category)
        {
            var token = cancellation.Token;
            await throttle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var url = $"https://api.kick.com/public/v1/categories/{Uri.EscapeDataString(category.Id)}";
                using var request = CreateKickRequest(url, accessToken);
                using var response = await BoundedHttpResponseSender.SendAsync(owner.httpClient, request, token).ConfigureAwait(false);
                var body = await BoundedHttpContentReader.ReadJsonAsync(response.Content, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        return new(null, owner.HandleBrowseHttpFailure<BrowseCategory>(response, body,
                            "Kick category viewer counts unavailable. Check Kick API credentials."));
                    }
                    owner.logger.Write(AppLogLevel.Warning, "Browse",
                        $"Kick category '{category.Name}' viewer count lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(body)}");
                    return new(null, null);
                }

                using var document = JsonDocument.Parse(body);
                return new(document.RootElement.Clone(), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                owner.logger.Write(AppLogLevel.Warning, "Browse", $"Kick category '{category.Name}' viewer count lookup failed.", ex);
                return new(null, null);
            }
            finally { throttle.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(requests.Values).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                owner.logger.WriteSafely(AppLogLevel.Warning, "Browse", "Kick category detail requests did not complete.", ex);
            }
            finally
            {
                throttle.Dispose();
                cancellation.Dispose();
            }
        }
    }

    private sealed record KickCategoryDetailResponse(JsonElement? Payload, BrowseResult<BrowseCategory>? Failure);
}
