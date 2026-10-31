using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;

namespace StreamlinkVlcStudio.App.Wpf.Follows;

/// <summary>Checks the website contracts inspected in docs/channel-follow.md. Never issues a mutation.</summary>
internal sealed class ChannelFollowResponseTracker
{
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const int MaximumRequestBytes = 128 * 1024;
    private bool armed;
    private bool mutationDispatched;
    private bool twitchIntegrityRetryUsed;
    private bool twitchUserRetryAuthorized;
    private readonly string expectedBroadcasterId;

    internal ChannelFollowResponseTracker(StreamTarget target, bool? desiredFollowing = true)
    {
        if (!TryGetChannel(target, out var channel))
            throw new InvalidOperationException("Select a Twitch or Kick broadcaster before following.");
        Target = channel;
        DesiredFollowing = desiredFollowing;
        expectedBroadcasterId = target.BroadcasterId;
        ChannelUri = new Uri(channel.Url);
    }

    internal StreamTarget Target { get; }
    internal bool? DesiredFollowing { get; }
    internal bool? IsFollowing { get; private set; }
    private string MutationName => DesiredFollowing == false ? "unfollowUser" : "followUser";
    private string OperationName => DesiredFollowing == false ? "FollowButton_UnfollowUser" : "FollowButton_FollowUser";
    internal Uri ChannelUri { get; }
    internal string TwitchUserId { get; private set; } = "";
    internal string TwitchDisplayName { get; private set; } = "";
    internal bool HasTwitchFollowState { get; private set; }
    internal bool? TwitchCanFollow { get; private set; }
    internal bool Confirmed { get; private set; }
    internal bool RequiresSignIn { get; private set; }
    internal bool KickRequestAcknowledged { get; private set; }
    internal bool TwitchIntegrityRetryPending { get; private set; }
    internal bool TwitchUserRetryPending { get; private set; }
    internal string? Error { get; private set; }

    internal static bool TryGetChannel(StreamTarget? target, out StreamTarget channel)
    {
        channel = null!;
        // A pasted Twitch VOD initially uses its numeric video ID as Channel. It is not a login.
        if (target is null || target.Platform is not (PlatformKind.Twitch or PlatformKind.Kick) ||
            target.IsExplicitTwitchVod && target.Channel == target.MediaId && string.IsNullOrEmpty(target.BroadcasterId))
            return false;
        if (!StreamInputParser.TryFromChannel(target.Platform, target.Channel, out var parsed)) return false;
        channel = parsed;
        return true;
    }

    internal bool IsChannelPage(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host == ChannelUri.Host &&
        string.Equals(uri.AbsolutePath.TrimEnd('/'), ChannelUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    internal void Arm() => armed = true;

    internal void Reset()
    {
        // A document change must not authorize a second write in the same operation.
        armed = Confirmed = HasTwitchFollowState = KickRequestAcknowledged = RequiresSignIn = false;
        TwitchCanFollow = null;
        TwitchIntegrityRetryPending = TwitchUserRetryPending = false;
        twitchUserRetryAuthorized = false;
        IsFollowing = null;
        TwitchUserId = "";
        TwitchDisplayName = "";
        Error = null;
    }

    internal bool BlocksUnexpectedFollow(string method, string address, string requestBody)
    {
        if (Target.Platform == PlatformKind.Kick)
        {
            if (method is not ("POST" or "DELETE") || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                uri.Host != "kick.com" || !uri.AbsolutePath.StartsWith("/api/v2/channels/", StringComparison.Ordinal) ||
                !uri.AbsolutePath.EndsWith("/follow", StringComparison.Ordinal)) return false;
            if (DesiredFollowing is not null && armed && !mutationDispatched && IsResponseCandidate(method, address))
            {
                mutationDispatched = true;
                return false;
            }
        }
        else
        {
            if (!IsResponseCandidate(method, address)) return false;
            try
            {
                using var request = JsonDocument.Parse(requestBody);
                var follows = Items(request.RootElement)
                    .Where(item => Text(item, "operationName") is "FollowButton_FollowUser" or "FollowButton_UnfollowUser").ToArray();
                if (follows.Length == 0) return false;
                if (DesiredFollowing is not null && Error is null && armed && HasTwitchFollowState && follows.Length == 1 && IsMatchingMutation(follows[0]) &&
                    (!mutationDispatched || TwitchIntegrityRetryPending))
                {
                    twitchIntegrityRetryUsed = mutationDispatched && !twitchUserRetryAuthorized;
                    twitchUserRetryAuthorized = false;
                    TwitchIntegrityRetryPending = TwitchUserRetryPending = false;
                    mutationDispatched = true;
                    return false;
                }
            }
            catch (JsonException)
            {
                // A request whose body cannot be inspected must not leave this follow browser.
            }
        }
        Error = $"{Target.Platform}'s follow request did not match the selected channel or was repeated. The unexpected request was blocked; check the channel before retrying.";
        return true;
    }

    internal bool IsResponseCandidate(string method, string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        return Target.Platform == PlatformKind.Twitch
            ? method == "POST" && uri.Host == "gql.twitch.tv" && uri.AbsolutePath == "/gql"
            : method == (DesiredFollowing == false ? "DELETE" : "POST") && uri.Host == "kick.com" && uri.AbsolutePath == $"/api/v2/channels/{Target.Channel}/follow";
    }

    internal void Observe(string method, string address, int status, string requestBody, string responseBody)
    {
        if (!IsResponseCandidate(method, address) || Error is not null || Confirmed) return;
        if (Target.Platform == PlatformKind.Kick)
        {
            if (!armed) return;
            if (status is >= 200 and < 300) KickRequestAcknowledged = true;
            else Error = HttpError(status);
            return;
        }

        if (responseBody.Length > MaximumResponseBytes || requestBody.Length > MaximumRequestBytes)
        {
            if (armed) Error = "Twitch's follow response was too large to verify. Check the channel before retrying.";
            return;
        }
        try
        {
            using var request = string.IsNullOrEmpty(requestBody) ? null : JsonDocument.Parse(requestBody);
            var requests = request is null ? [] : Items(request.RootElement);
            if (status is < 200 or >= 300)
            {
                if (status == 401 && !mutationDispatched && !requests.Any(IsMatchingMutation))
                    RequiresSignIn = true;
                if (armed && requests.Any(IsMatchingMutation)) Error = HttpError(status);
                return;
            }
            using var response = JsonDocument.Parse(responseBody);
            var replies = Items(response.RootElement);
            for (var index = 0; index < replies.Length; index++)
            {
                var reply = replies[index];
                if (reply.ValueKind != JsonValueKind.Object) continue;
                var matchingMutation = armed && index < requests.Length && IsMatchingMutation(requests[index]);
                if (matchingMutation && HasErrors(reply))
                {
                    // Twitch's own challenge link refreshes Client-Integrity and replays this operation.
                    // A challenge is a rejected write, not a completed follow. Permit only that one
                    // website retry, after its response has been inspected, against the same target.
                    if (mutationDispatched && !twitchIntegrityRetryUsed && IsIntegrityChallenge(reply))
                    {
                        TwitchIntegrityRetryPending = true;
                        continue;
                    }
                    if (mutationDispatched && IsWebsiteChallenge(reply))
                    {
                        TwitchUserRetryPending = true;
                        // Challenge gates replay the rejected operation after the user completes
                        // Twitch's own verification dialog. A second integrity rejection instead
                        // needs a fresh, trusted click on the selected channel's Follow control.
                        if (!twitchIntegrityRetryUsed) TwitchIntegrityRetryPending = true;
                        continue;
                    }
                    Error = "Twitch could not confirm the follow. Check your sign-in and the channel before retrying.";
                    return;
                }
                if (HasErrors(reply) || !Object(reply, "data", out var data)) continue;
                if (matchingMutation)
                {
                    if (!Object(data, MutationName, out var payload) ||
                        DesiredFollowing == true && !payload.TryGetProperty("error", out _))
                    {
                        Error = "Twitch returned an unrecognized follow result. Check the channel before retrying.";
                        return;
                    }
                    if (payload.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    {
                        Error = TwitchError(error);
                        return;
                    }
                    if (!Object(payload, "follow", out var follow) || !Object(follow, "user", out var user) ||
                        !IsTargetUser(user) || Text(user, "id") != TwitchUserId ||
                        ReadFollowing(user) is not { } following || following != DesiredFollowing)
                    {
                        Error = "Twitch did not confirm following the selected channel. Check the channel before retrying.";
                        return;
                    }
                    IsFollowing = following;
                    Confirmed = true;
                }
                else if (Object(data, "user", out var user) && IsTargetUser(user))
                {
                    if (Text(user, "displayName") is { Length: > 0 and <= 256 } displayName)
                        TwitchDisplayName = displayName;
                    if (user.TryGetProperty("self", out var connection) && connection.ValueKind == JsonValueKind.Null)
                    {
                        RequiresSignIn = true;
                        continue;
                    }
                    if (!Object(user, "self", out var self)) continue;
                    // Player/emote queries can include follower without requesting canFollow.
                    // Omitted fields are unknown, not a restriction, and cannot erase a
                    // capability established by another response for this broadcaster.
                    if (self.TryGetProperty("canFollow", out var canFollow) &&
                        canFollow.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        TwitchCanFollow = canFollow.GetBoolean();
                    if (ReadFollowing(user) is not { } following) continue;
                    TwitchUserId = Text(user, "id");
                    IsFollowing = following;
                    HasTwitchFollowState = true;
                    // Once a write leaves the browser, only its correlated response may confirm it.
                    if (!mutationDispatched && (DesiredFollowing is null || DesiredFollowing == following)) Confirmed = true;
                }
            }
        }
        catch (JsonException)
        {
            // Do not return or log private response bodies, including GraphQL error text.
            if (armed) Error = "Twitch's follow result could not be verified. Check the channel before retrying.";
        }
    }

    private bool IsMatchingMutation(JsonElement request) => request.ValueKind == JsonValueKind.Object &&
        Text(request, "operationName") == OperationName &&
        Object(request, "variables", out var variables) && Object(variables, "input", out var input) &&
        TwitchUserId.Length != 0 && Text(input, "targetID") == TwitchUserId;

    private bool IsTargetUser(JsonElement user)
    {
        var id = Text(user, "id");
        return string.Equals(Text(user, "login"), Target.Channel, StringComparison.OrdinalIgnoreCase) &&
            id.Length is > 0 and <= 20 && id.All(c => c is >= '0' and <= '9') && id.Any(c => c != '0') &&
            (expectedBroadcasterId.Length == 0 || expectedBroadcasterId == id);
    }

    private static bool? ReadFollowing(JsonElement user)
    {
        if (!Object(user, "self", out var self) || !self.TryGetProperty("follower", out var follower)) return null;
        if (follower.ValueKind == JsonValueKind.Null) return false;
        return Object(self, "follower", out _) && Text(follower, "followedAt") is { Length: > 10 and <= 48 } timestamp &&
            timestamp.Contains('T') && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _) ? true : null;
    }

    private string HttpError(int status) => status switch
    {
        401 => $"Sign in to {Target.Platform} again, then retry Follow.",
        403 => $"{Target.Platform} refused the follow request. Complete any website check or account verification, then retry.",
        429 => $"{Target.Platform} is limiting follow requests. Wait before trying again.",
        _ => $"{Target.Platform} returned HTTP {status}. The follow was not confirmed; check the channel before retrying."
    };

    private static string TwitchError(JsonElement error) => Text(error, "code") switch
    {
        "UNVERIFIED_EMAIL" or "UNVERIFIED_PHONE" or "UNVERIFIED_PHONE_AND_EMAIL" =>
            "Twitch requires account verification before following this channel. Complete it on Twitch, then retry Follow.",
        "TOO_MANY_FOLLOWS" => "Twitch's follow limit was reached. Manage your followed channels on Twitch before trying again.",
        "FORBIDDEN" => "Twitch does not allow this account to follow the selected channel.",
        _ => "Twitch refused the follow. Check your account and the channel before retrying."
    };

    private static bool HasErrors(JsonElement value) => value.TryGetProperty("errors", out var errors) &&
        errors.ValueKind != JsonValueKind.Null && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0);

    private bool IsIntegrityChallenge(JsonElement reply) =>
        IsWebsiteChallenge(reply) && Object(reply, "extensions", out var extensions) &&
        Object(extensions, "challenge", out var challenge) && Text(challenge, "type") == "integrity";

    private bool IsWebsiteChallenge(JsonElement reply) =>
        Object(reply, "extensions", out var extensions) && Object(extensions, "challenge", out var challenge) &&
        Text(challenge, "type") is "integrity" or "challenge-gates" && Object(reply, "data", out var data) &&
        data.TryGetProperty(MutationName, out var follow) && follow.ValueKind == JsonValueKind.Null;

    internal void AuthorizeTwitchUserRetry()
    {
        if (!TwitchUserRetryPending || Error is not null) return;
        TwitchUserRetryPending = false;
        TwitchIntegrityRetryPending = true;
        twitchUserRetryAuthorized = true;
    }

    internal void RejectRequestVerification() => Error ??=
        $"The {Target.Platform} website session or follow request could not be verified. Check the channel before retrying.";

    private static JsonElement[] Items(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().ToArray() : [value];

    private static bool Object(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object;
    }

    private static string Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
