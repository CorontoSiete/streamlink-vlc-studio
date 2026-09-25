using System.Text;
using System.Text.Json;

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>Best-effort catalog reads with one timeout, payload limit, and decoding policy.</summary>
internal static class OptionalHttpJsonReader
{
    internal static async Task<JsonDocument?> SendAsync(
        HttpClient client, HttpRequestMessage request, int maximumBytes)
    {
        try
        {
            using var response = await BoundedHttpResponseSender.SendAsync(client, request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await BoundedHttpContentReader.ReadStringAsync(response.Content, maximumBytes).ConfigureAwait(false);
            return JsonDocument.Parse(text);
        }
        catch (Exception ex) when (IsRecoverableFailure(ex))
        {
            return null;
        }
    }

    internal static bool IsRecoverableFailure(Exception exception) => exception is
        HttpRequestException or OperationCanceledException or JsonException or IOException or
        InvalidDataException or InvalidOperationException or DecoderFallbackException;
}
