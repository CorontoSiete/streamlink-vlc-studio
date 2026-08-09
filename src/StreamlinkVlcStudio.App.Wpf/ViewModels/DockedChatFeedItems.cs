using System.Collections.ObjectModel;
using System.Globalization;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class DockedChatMessageFeedItem
{
    public DockedChatMessageFeedItem(ChatMessage message)
    {
        Message = message;
    }

    public ChatMessage Message { get; }
}

public sealed class TwitchPredictionOutcomeInputViewModel : ObservableObject
{
    private readonly Action<TwitchPredictionOutcomeInputViewModel> remove;
    private string title;
    private bool canRemove;

    public TwitchPredictionOutcomeInputViewModel(string title, Action<TwitchPredictionOutcomeInputViewModel> remove)
    {
        this.title = title;
        this.remove = remove;
        RemoveCommand = new RelayCommand(() => this.remove(this), () => CanRemove);
    }

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value ?? "");
    }

    public bool CanRemove
    {
        get => canRemove;
        set
        {
            if (SetProperty(ref canRemove, value))
            {
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand RemoveCommand { get; }
}

public sealed class TwitchPredictionFeedItemViewModel : ObservableObject
{
    private readonly Func<TwitchPredictionFeedItemViewModel, Task> lockAsync;
    private readonly Func<TwitchPredictionFeedItemViewModel, Task> cancelAsync;
    private readonly Func<TwitchPredictionFeedItemViewModel, Task> resolveAsync;
    private TwitchPrediction prediction;
    private TwitchPredictionOutcomeViewModel? selectedWinningOutcome;
    private bool canManage;
    private bool isRequestInFlight;
    private string timingText = "";

    public TwitchPredictionFeedItemViewModel(
        TwitchPrediction prediction,
        bool canManage,
        Func<TwitchPredictionFeedItemViewModel, Task> lockAsync,
        Func<TwitchPredictionFeedItemViewModel, Task> cancelAsync,
        Func<TwitchPredictionFeedItemViewModel, Task> resolveAsync)
    {
        this.prediction = prediction;
        this.canManage = canManage;
        this.lockAsync = lockAsync;
        this.cancelAsync = cancelAsync;
        this.resolveAsync = resolveAsync;
        LockCommand = new AsyncRelayCommand(() => this.lockAsync(this), () => CanLock);
        CancelCommand = new AsyncRelayCommand(() => this.cancelAsync(this), () => CanCancel);
        ResolveCommand = new AsyncRelayCommand(() => this.resolveAsync(this), () => CanResolve);
        Update(prediction, canManage);
    }

    public string PredictionId => prediction.Id;
    public string Title => prediction.Title;
    public string StatusText => FormatStatus(prediction.Status);
    public bool IsOpen => prediction.IsOpen;
    public string TimingText
    {
        get => timingText;
        private set => SetProperty(ref timingText, value);
    }

    public string TotalText => FormatTotal(prediction.Outcomes.Sum(outcome => outcome.ChannelPoints), prediction.Outcomes.Sum(outcome => outcome.Users));
    public bool IsManageVisible => canManage && prediction.Status is TwitchPredictionStatus.Active or TwitchPredictionStatus.Locked;
    public bool IsResolveSelectorVisible => canManage && prediction.Status == TwitchPredictionStatus.Locked && !isRequestInFlight;
    public bool CanLock => canManage && !isRequestInFlight && prediction.Status == TwitchPredictionStatus.Active;
    public bool CanCancel => canManage && !isRequestInFlight && prediction.Status is TwitchPredictionStatus.Active or TwitchPredictionStatus.Locked;
    public bool CanResolve => canManage && !isRequestInFlight && prediction.Status == TwitchPredictionStatus.Locked && SelectedWinningOutcome is not null;
    public ObservableCollection<TwitchPredictionOutcomeViewModel> Outcomes { get; } = [];
    public AsyncRelayCommand LockCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand ResolveCommand { get; }

    public TwitchPredictionOutcomeViewModel? SelectedWinningOutcome
    {
        get => selectedWinningOutcome;
        set
        {
            if (SetProperty(ref selectedWinningOutcome, value))
            {
                RaiseCommandState();
            }
        }
    }

    public void Update(TwitchPrediction updatedPrediction, bool updatedCanManage)
    {
        prediction = updatedPrediction;
        canManage = updatedCanManage;
        UpsertOutcomes(updatedPrediction);
        if (SelectedWinningOutcome is null && Outcomes.Count > 0)
        {
            SelectedWinningOutcome = Outcomes[0];
        }
        else if (SelectedWinningOutcome is not null)
        {
            SelectedWinningOutcome = Outcomes.FirstOrDefault(outcome => outcome.Id == SelectedWinningOutcome.Id) ?? Outcomes.FirstOrDefault();
        }

        RefreshTiming();
        OnPropertyChanged(nameof(PredictionId));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(IsManageVisible));
        OnPropertyChanged(nameof(IsResolveSelectorVisible));
        RaiseCommandState();
    }

    public void SetCanManage(bool value)
    {
        if (canManage == value)
        {
            return;
        }

        canManage = value;
        OnPropertyChanged(nameof(IsManageVisible));
        OnPropertyChanged(nameof(IsResolveSelectorVisible));
        RaiseCommandState();
    }

    public void SetRequestInFlight(bool value)
    {
        if (isRequestInFlight == value)
        {
            return;
        }

        isRequestInFlight = value;
        OnPropertyChanged(nameof(IsResolveSelectorVisible));
        RaiseCommandState();
    }

    public void RefreshTiming()
    {
        TimingText = FormatTiming(prediction);
    }

    private void UpsertOutcomes(TwitchPrediction updatedPrediction)
    {
        Outcomes.Clear();
        foreach (var outcome in updatedPrediction.Outcomes)
        {
            Outcomes.Add(new TwitchPredictionOutcomeViewModel(outcome, updatedPrediction.WinningOutcomeId));
        }
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanResolve));
        LockCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ResolveCommand.RaiseCanExecuteChanged();
    }

    private static string FormatStatus(TwitchPredictionStatus status)
    {
        return status switch
        {
            TwitchPredictionStatus.Active => "Active",
            TwitchPredictionStatus.Locked => "Locked",
            TwitchPredictionStatus.Resolved => "Resolved",
            TwitchPredictionStatus.Canceled => "Canceled",
            _ => "Prediction"
        };
    }

    private static string FormatTotal(int channelPoints, int users)
    {
        return $"{FormatNumber(channelPoints)} points | {FormatNumber(users)} users";
    }

    private static string FormatTiming(TwitchPrediction prediction)
    {
        var now = DateTimeOffset.UtcNow;
        if (prediction.Status == TwitchPredictionStatus.Active && prediction.LocksAtUtc is { } locksAt)
        {
            var remaining = locksAt.ToUniversalTime() - now;
            return remaining > TimeSpan.Zero
                ? $"Locks in {FormatDuration(remaining)}"
                : "Locking soon";
        }

        if (prediction.Status == TwitchPredictionStatus.Locked && prediction.LocksAtUtc is { } lockedAt)
        {
            return $"Locked {lockedAt.ToLocalTime():g}";
        }

        if (prediction.EndedAtUtc is { } endedAt)
        {
            return $"Ended {endedAt.ToLocalTime():g}";
        }

        return prediction.StartedAtUtc is { } startedAt
            ? $"Started {startedAt.ToLocalTime():g}"
            : "";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{(int)value.TotalMinutes}m {value.Seconds}s";
        }

        return $"{Math.Max(0, value.Seconds)}s";
    }

    private static string FormatNumber(int value)
    {
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }
}

public sealed class TwitchPredictionOutcomeViewModel
{
    public TwitchPredictionOutcomeViewModel(TwitchPredictionOutcome outcome, string? winningOutcomeId)
    {
        Id = outcome.Id;
        Title = outcome.Title;
        ColorBrush = outcome.Color.Equals("pink", StringComparison.OrdinalIgnoreCase)
            ? "#D946EF"
            : "#3B82F6";
        UsersText = outcome.Users.ToString("N0", CultureInfo.CurrentCulture);
        PointsText = outcome.ChannelPoints.ToString("N0", CultureInfo.CurrentCulture);
        IsWinner = !string.IsNullOrWhiteSpace(winningOutcomeId) &&
            string.Equals(outcome.Id, winningOutcomeId, StringComparison.Ordinal);
        TopPredictorsText = FormatTopPredictors(outcome.TopPredictors);
    }

    public string Id { get; }
    public string Title { get; }
    public string ColorBrush { get; }
    public string UsersText { get; }
    public string PointsText { get; }
    public bool IsWinner { get; }
    public string WinnerText => IsWinner ? "Winner" : "";
    public string TopPredictorsText { get; }
    public bool HasTopPredictors => !string.IsNullOrWhiteSpace(TopPredictorsText);

    private static string FormatTopPredictors(IReadOnlyList<TwitchPredictionTopPredictor> predictors)
    {
        if (predictors.Count == 0)
        {
            return "";
        }

        return string.Join(
            ", ",
            predictors.Take(3).Select(predictor =>
                $"{FirstNonEmpty(predictor.UserName, predictor.UserLogin, "viewer")} {predictor.ChannelPointsUsed:N0}"));
    }
}
