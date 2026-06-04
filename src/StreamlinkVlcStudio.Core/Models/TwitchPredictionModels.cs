namespace StreamlinkVlcStudio.Core.Models;

public enum TwitchPredictionStatus
{
    Unknown,
    Active,
    Locked,
    Resolved,
    Canceled
}

public sealed record TwitchPrediction(
    string Id,
    string BroadcasterId,
    string BroadcasterLogin,
    string BroadcasterName,
    string Title,
    string? WinningOutcomeId,
    IReadOnlyList<TwitchPredictionOutcome> Outcomes,
    int PredictionWindowSeconds,
    TwitchPredictionStatus Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LocksAtUtc,
    DateTimeOffset? EndedAtUtc)
{
    public bool IsOpen => Status is TwitchPredictionStatus.Active or TwitchPredictionStatus.Locked;
}

public sealed record TwitchPredictionOutcome(
    string Id,
    string Title,
    string Color,
    int Users,
    int ChannelPoints,
    IReadOnlyList<TwitchPredictionTopPredictor> TopPredictors);

public sealed record TwitchPredictionTopPredictor(
    string UserId,
    string UserLogin,
    string UserName,
    int ChannelPointsUsed,
    int? ChannelPointsWon);

public sealed record TwitchPredictionAccessState(
    bool IsTwitchChannel,
    bool CanManage,
    string Message,
    string? BroadcasterId = null,
    string? BroadcasterLogin = null,
    string? TokenUserId = null)
{
    public static TwitchPredictionAccessState NotTwitch =>
        new(false, false, "Predictions are available for Twitch channels only.");

    public static TwitchPredictionAccessState Pending =>
        new(true, false, "Prediction controls will enable after Twitch chat connects.");
}

public sealed record TwitchPredictionCreateRequest(
    string Title,
    IReadOnlyList<string> Outcomes,
    int PredictionWindowSeconds);
