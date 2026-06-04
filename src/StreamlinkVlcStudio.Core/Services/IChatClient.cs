using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IChatClient : IAsyncDisposable
{
    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<string>? StatusChanged;
    string? CurrentUsername { get; }
    Task ConnectAsync(StreamTarget target, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendMessageAsync(string message, CancellationToken cancellationToken = default);
}

public interface IChatHistoryBackfillClient
{
    Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default);
}

public interface IKickChatHistoryProvider
{
    Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        StreamTarget target,
        ChatSettings settings,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default);
}

public readonly record struct ChatHistoryBackfillResult
{
    public ChatHistoryBackfillResult(
        bool Attempted,
        int LoadedMessageCount,
        bool CoveredRequestedRange,
        DateTimeOffset? CoveredFromTimestampUtc,
        DateTimeOffset? CoveredThroughTimestampUtc,
        IReadOnlyList<ChatMessage>? Messages = null)
    {
        this.Attempted = Attempted;
        this.LoadedMessageCount = LoadedMessageCount;
        this.CoveredRequestedRange = CoveredRequestedRange;
        this.CoveredFromTimestampUtc = CoveredFromTimestampUtc;
        this.CoveredThroughTimestampUtc = CoveredThroughTimestampUtc;
        this.Messages = Messages ?? [];
    }

    public bool Attempted { get; init; }
    public int LoadedMessageCount { get; init; }
    public bool CoveredRequestedRange { get; init; }
    public DateTimeOffset? CoveredFromTimestampUtc { get; init; }
    public DateTimeOffset? CoveredThroughTimestampUtc { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; }
}

public interface ITwitchPredictionClient
{
    event EventHandler<TwitchPrediction>? PredictionReceived;
    event EventHandler<TwitchPredictionAccessState>? PredictionAccessChanged;
    TwitchPredictionAccessState PredictionAccess { get; }
    Task<TwitchPrediction?> RefreshPredictionAsync(CancellationToken cancellationToken = default);
    Task<TwitchPrediction> CreatePredictionAsync(TwitchPredictionCreateRequest request, CancellationToken cancellationToken = default);
    Task<TwitchPrediction> LockPredictionAsync(string predictionId, CancellationToken cancellationToken = default);
    Task<TwitchPrediction> CancelPredictionAsync(string predictionId, CancellationToken cancellationToken = default);
    Task<TwitchPrediction> ResolvePredictionAsync(string predictionId, string winningOutcomeId, CancellationToken cancellationToken = default);
}

public interface IChatClientFactory
{
    IChatClient Create(PlatformKind platform);
}
