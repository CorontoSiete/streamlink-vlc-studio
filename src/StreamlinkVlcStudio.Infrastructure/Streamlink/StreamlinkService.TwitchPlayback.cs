using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

public sealed partial class StreamlinkService
{
    private static readonly TimeSpan TwitchPlaybackSessionTimeout = TimeSpan.FromSeconds(15);
    internal const string TwitchVideoUnavailableMessage =
        "Twitch returned only audio for the requested video quality. Some broadcasts require a Twitch website sign-in. " +
        "In Settings > Accounts > Channel-point bonuses, select Sign in for bonuses, then reload the stream. " +
        "You can also select Audio only to listen. Connect Twitch uses a separate account token.";

    private async Task<string?> ReadTwitchPlaybackTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TwitchPlaybackSessionTimeout);
        try
        {
            var token = await twitchPlaybackTokenProvider!(budget.Token)
                .WaitAsync(TwitchPlaybackSessionTimeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            token = token?.Trim();
            if (!string.IsNullOrEmpty(token) && (token.Length > 4096 || token.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))))
                throw new InvalidDataException("The Twitch website session token has an invalid format.");
            return token;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Browser/provider exception messages may contain account data.
            logger.Write(AppLogLevel.Warning, "Playback",
                $"The saved Twitch website session could not be read ({exception.GetType().Name}).");
            return null;
        }
    }

    private static bool RequiresTwitchVideo(StreamTransportRequest request) =>
        request.Target.Platform == PlatformKind.Twitch && request.Target.Kind == StreamTargetKind.Live &&
        !request.Quality.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(quality => quality.Equals("audio_only", StringComparison.OrdinalIgnoreCase) ||
                quality.Equals("audio", StringComparison.OrdinalIgnoreCase));

    private static bool HasOnlyAudioStreams(string line)
    {
        const string prefix = "Available streams:";
        var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var names = StreamAliasesPattern().Replace(line[(index + prefix.Length)..], "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length > 0 && names.All(name =>
            name.Equals("audio_only", StringComparison.OrdinalIgnoreCase) || name.Equals("audio", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasCustomTwitchPlaybackIdentity(IReadOnlyList<string> arguments)
    {
        const string option = "--twitch-api-header";
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var header = argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)
                ? argument[(option.Length + 1)..]
                : argument.Equals(option, StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count
                    ? arguments[index + 1] : "";
            if (header.StartsWith("Authorization=", StringComparison.OrdinalIgnoreCase) ||
                header.StartsWith("Client-ID=", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<string> WithTwitchPlaybackToken(IEnumerable<string> arguments, string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            yield return "--twitch-api-header";
            yield return "Authorization=OAuth " + token;
        }
        foreach (var argument in arguments) yield return argument;
    }

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex StreamAliasesPattern();

    private sealed class TwitchVideoUnavailableException() : InvalidOperationException(TwitchVideoUnavailableMessage);
}
