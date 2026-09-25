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

public interface ITwitchPredictionClient
{
    event EventHandler<TwitchPrediction>? PredictionReceived;
    event EventHandler<TwitchPredictionAccessState>? PredictionAccessChanged;
    TwitchPredictionAccessState PredictionAccess { get; }
    Task<TwitchPrediction> CreatePredictionAsync(TwitchPredictionCreateRequest request, CancellationToken cancellationToken = default);
    Task<TwitchPrediction> LockPredictionAsync(string predictionId, CancellationToken cancellationToken = default);
    Task<TwitchPrediction> CancelPredictionAsync(string predictionId, CancellationToken cancellationToken = default);
    Task<TwitchPrediction> ResolvePredictionAsync(string predictionId, string winningOutcomeId, CancellationToken cancellationToken = default);
}

public interface IChatClientFactory
{
    IChatClient Create(PlatformKind platform);
}
