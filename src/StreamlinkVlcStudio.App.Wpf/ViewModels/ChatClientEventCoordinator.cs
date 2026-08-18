using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Keeps chat and Twitch prediction event wiring symmetrical across reconnects. It does not own
/// the client lifetime; that remains with the tab's connection coordinator.
/// </summary>
internal sealed class ChatClientEventCoordinator
{
    private readonly EventHandler<ChatMessage> messageReceived;
    private readonly EventHandler<string> statusChanged;
    private readonly EventHandler<TwitchPrediction> predictionReceived;
    private readonly EventHandler<TwitchPredictionAccessState> predictionAccessChanged;
    private readonly Action<TwitchPredictionAccessState> applyPredictionAccess;

    public ChatClientEventCoordinator(
        EventHandler<ChatMessage> messageReceived,
        EventHandler<string> statusChanged,
        EventHandler<TwitchPrediction> predictionReceived,
        EventHandler<TwitchPredictionAccessState> predictionAccessChanged,
        Action<TwitchPredictionAccessState> applyPredictionAccess)
    {
        this.messageReceived = messageReceived ?? throw new ArgumentNullException(nameof(messageReceived));
        this.statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
        this.predictionReceived = predictionReceived ?? throw new ArgumentNullException(nameof(predictionReceived));
        this.predictionAccessChanged = predictionAccessChanged ?? throw new ArgumentNullException(nameof(predictionAccessChanged));
        this.applyPredictionAccess = applyPredictionAccess ?? throw new ArgumentNullException(nameof(applyPredictionAccess));
    }

    public ITwitchPredictionClient? PredictionClient { get; private set; }

    public void Attach(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.MessageReceived += messageReceived;
        client.StatusChanged += statusChanged;
        if (client is not ITwitchPredictionClient predictions)
        {
            return;
        }

        PredictionClient = predictions;
        predictions.PredictionReceived += predictionReceived;
        predictions.PredictionAccessChanged += predictionAccessChanged;
        applyPredictionAccess(predictions.PredictionAccess);
    }

    public void Detach(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.MessageReceived -= messageReceived;
        client.StatusChanged -= statusChanged;
        if (client is not ITwitchPredictionClient predictions)
        {
            return;
        }

        predictions.PredictionReceived -= predictionReceived;
        predictions.PredictionAccessChanged -= predictionAccessChanged;
        if (ReferenceEquals(PredictionClient, predictions))
        {
            PredictionClient = null;
            applyPredictionAccess(TwitchPredictionAccessState.Pending);
        }
    }
}
