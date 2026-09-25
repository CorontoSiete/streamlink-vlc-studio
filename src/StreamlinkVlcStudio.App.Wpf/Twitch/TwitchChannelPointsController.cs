using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.Twitch;

/// <summary>One bonus chat page per distinct open live channel, independent of the docked chat and tab selection.</summary>
internal sealed class TwitchChannelPointsController : ObservableObject, IDisposable
{
    private readonly AppSettings settings;
    private readonly ObservableCollection<StreamTabViewModel> tabs;
    private readonly Func<StreamTabViewModel?> selectedTab;
    private readonly ITwitchBonusBrowser browser;
    private readonly IAppLogger logger;
    private readonly ISettingsService? settingsService;
    private readonly DispatcherTimer sessionTimer;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly TimeSpan checkInterval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly HashSet<StreamTabViewModel> observedTabs = [];
    private readonly Dictionary<string, Worker> workers = new(StringComparer.Ordinal);
    private ChatSettings chat;
    private bool sessionChecked, signedIn, checkingSession, accountBusy, disposed;
    private bool sessionExpired;
    private string? sessionError;
    private string historySaveStatus = "";
    private long historyVersion;
    private string status = "Sign in to Twitch for bonuses. This is separate from Connect Twitch.";

    internal TwitchChannelPointsController(
        AppSettings settings,
        ObservableCollection<StreamTabViewModel> tabs,
        Func<StreamTabViewModel?> selectedTab,
        ITwitchBonusBrowser browser,
        IAppLogger logger,
        TimeSpan? checkInterval = null,
        ISettingsService? settingsService = null)
    {
        this.settings = settings;
        this.tabs = tabs;
        this.selectedTab = selectedTab;
        this.browser = browser;
        this.logger = logger;
        this.settingsService = settingsService;
        this.checkInterval = checkInterval ?? TimeSpan.FromSeconds(10);
        lifetimeToken = lifetime.Token;
        chat = settings.Chat;
        SignInCommand = new AsyncRelayCommand(() => ChangeAccountAsync(signOut: false), CanChangeAccount);
        SignOutCommand = new AsyncRelayCommand(() => ChangeAccountAsync(signOut: true), CanChangeAccount);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanChangeAccount);
        OpenPageCommand = new RelayCommand(OpenSelectedPage);
        settings.PropertyChanged += SettingsChanged;
        chat.PropertyChanged += ChatChanged;
        tabs.CollectionChanged += TabsChanged;
        sessionTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            (_, _) =>
            {
                if (!disposed && !checkingSession && !accountBusy && !sessionExpired && workers.Count == 0)
                    _ = CheckSessionAsync();
            }, dispatcher);
        ObserveTabs();
        Synchronize();
        _ = CheckSessionAsync();
    }

    public bool Enabled
    {
        get => chat.AutoClaimTwitchChannelPoints;
        set => chat.AutoClaimTwitchChannelPoints = value;
    }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string SignInStatus => accountBusy ? "Bonus sign-in: Waiting for Twitch."
        : checkingSession || !sessionChecked ? "Bonus sign-in: Checking website session…"
        : sessionError is not null ? "Bonus sign-in: Could not check. Retry bonuses."
        : signedIn ? "Bonus sign-in: Website session saved."
        : sessionExpired ? "Bonus sign-in: Session expired. Sign in for bonuses again."
        : "Bonus sign-in: Not signed in. Sign in for bonuses to enable claims.";
    public bool RequiresSignIn => sessionChecked && !signedIn && !checkingSession && !accountBusy && sessionError is null;
    public bool HasSavedSession => signedIn && !checkingSession && !accountBusy && sessionError is null;
    public string HistorySaveStatus { get => historySaveStatus; private set => SetProperty(ref historySaveStatus, value); }
    public IReadOnlyList<ChannelClaimSummary> ChannelClaims => settings.TwitchBonusClaims.Keys
        .Concat(tabs.Where(tab => tab.Target.Platform == PlatformKind.Twitch && tab.Target.Kind == StreamTargetKind.Live)
            .Select(tab => tab.Target.Channel.Trim().ToLowerInvariant()).Where(TwitchBonusBrowser.IsChannelLogin))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select(channel => new ChannelClaimSummary(channel, settings.TwitchBonusClaims.GetValueOrDefault(channel)?.Count ?? 0))
        .ToArray();
    public bool HasNoChannelClaims => ChannelClaims.Count == 0;
    public AsyncRelayCommand SignInCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public RelayCommand OpenPageCommand { get; }

    private bool CanChangeAccount() => !disposed && !accountBusy && !checkingSession;

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args) => OnUi(() =>
    {
        if (args.PropertyName == nameof(AppSettings.TwitchBonusClaims)) { RaiseChannelClaims(); return; }
        if (args.PropertyName != nameof(AppSettings.Chat)) return;
        chat.PropertyChanged -= ChatChanged;
        chat = settings.Chat;
        chat.PropertyChanged += ChatChanged;
        OnPropertyChanged(nameof(Enabled));
        Synchronize();
    });

    private void ChatChanged(object? sender, PropertyChangedEventArgs args) => OnUi(() =>
    {
        if (args.PropertyName != nameof(ChatSettings.AutoClaimTwitchChannelPoints)) return;
        OnPropertyChanged(nameof(Enabled));
        Synchronize();
    });

    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUi(() =>
    {
        ObserveTabs();
        RaiseChannelClaims();
        Synchronize();
    });

    private void ObserveTabs()
    {
        foreach (var tab in observedTabs.Where(tab => !tabs.Contains(tab)).ToArray())
        {
            tab.PropertyChanged -= TabChanged;
            observedTabs.Remove(tab);
        }
        foreach (var tab in tabs)
            if (observedTabs.Add(tab)) tab.PropertyChanged += TabChanged;
    }

    private void TabChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(StreamTabViewModel.Status) or nameof(StreamTabViewModel.IsBehindLive))
            OnUi(Synchronize);
    }

    private void OnUi(Action action)
    {
        if (disposed) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(() => { if (!disposed) action(); });
    }

    internal static bool IsEligible(StreamTabViewModel tab) =>
        tab.Target.Platform == PlatformKind.Twitch && tab.Target.Kind == StreamTargetKind.Live &&
        !tab.IsBehindLive && tab.Status is PlaybackStatus.Playing or PlaybackStatus.Paused &&
        TwitchBonusBrowser.IsChannelLogin(tab.Target.Channel.Trim().ToLowerInvariant());

    private void Synchronize()
    {
        if (disposed) return;
        var desired = Enabled && !accountBusy
            ? tabs.Where(IsEligible).Select(tab => tab.Target.Channel.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in workers.Keys.Where(key => !desired.Contains(key)).ToArray()) StopWorker(key);
        if (!Enabled) { Status = "Automatic Twitch bonuses are off."; return; }
        if (accountBusy) return;
        if (!sessionChecked)
        {
            return;
        }
        if (!signedIn) { RefreshStatus(); return; }
        foreach (var channel in desired)
        {
            if (!signedIn || disposed || !Enabled || accountBusy) break;
            if (workers.ContainsKey(channel)) continue;
            var worker = new Worker(channel);
            workers.Add(channel, worker);
            _ = RunWorkerAsync(worker);
        }
        RefreshStatus();
    }

    private async Task CheckSessionAsync()
    {
        if (disposed || checkingSession || accountBusy) return;
        checkingSession = true;
        RaiseCommands();
        Status = "Checking Twitch website sign-in…";
        try
        {
            signedIn = await browser.HasSessionAsync(lifetimeToken);
            sessionChecked = true;
            sessionError = null;
            if (!signedIn) StopAllWorkers();
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error)
        {
            signedIn = false;
            if (!disposed) { StopAllWorkers(); Status = sessionError = DescribeError(error); }
            sessionChecked = true;
        }
        finally { checkingSession = false; RaiseCommands(); }
        if (!disposed) Synchronize();
    }

    private async Task ChangeAccountAsync(bool signOut)
    {
        accountBusy = true;
        signedIn = false;
        sessionChecked = true;
        StopAllWorkers();
        RaiseCommands();
        Status = signOut ? "Signing out of Twitch bonuses…" : "Finish Twitch sign-in, then close its window.";
        try
        {
            if (signOut) await browser.SignOutAsync(lifetimeToken);
            else await browser.SignInAsync(lifetimeToken);
            signedIn = await browser.HasSessionAsync(lifetimeToken);
            sessionChecked = true;
            sessionError = null;
            sessionExpired = false;
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error)
        {
            if (!disposed) Status = sessionError = DescribeError(error);
            return;
        }
        finally { accountBusy = false; RaiseCommands(); }
        Synchronize();
    }

    private async Task RetryAsync()
    {
        StopAllWorkers();
        signedIn = false;
        sessionChecked = false;
        sessionExpired = false;
        await CheckSessionAsync();
        if (HistorySaveStatus.Length > 0) await SaveHistoryAsync();
    }

    private async Task RunWorkerAsync(Worker worker)
    {
        var failures = 0;
        try
        {
            while (!worker.Cancellation.IsCancellationRequested)
            {
                var delay = checkInterval;
                try
                {
                    if (worker.Page is null)
                    {
                        worker.Page = await browser.OpenChannelAsync(worker.Channel, worker.Cancellation.Token);
                        var page = worker.Page;
                        page.ClaimConfirmed += (_, id) => OnUi(() =>
                        {
                            if (ReferenceEquals(worker.Page, page)) RecordClaim(worker, id);
                        });
                    }
                    worker.Cancellation.Token.ThrowIfCancellationRequested();
                    var result = await worker.Page.CheckAsync(worker.Cancellation.Token);
                    if (worker.Cancellation.IsCancellationRequested) return;
                    // Log the click, never claim server success based on DOM disappearance.
                    if (result == "Bonus claim clicked; waiting for Twitch.")
                        logger.Write(AppLogLevel.Info, "TwitchBonuses", $"{worker.Channel}: {result}");
                    worker.Message = result;
                    failures = 0;
                }
                catch (OperationCanceledException) when (worker.Cancellation.IsCancellationRequested) { return; }
                catch (TwitchBonusSessionExpiredException)
                {
                    signedIn = false;
                    sessionExpired = true;
                    sessionError = null;
                    StopAllWorkers();
                    RaiseCommands();
                    Status = "Twitch bonus session ended. Sign in for bonuses again.";
                    return;
                }
                catch (Exception error)
                {
                    worker.Page?.Dispose();
                    worker.Page = null;
                    worker.Message = DescribeError(error);
                    delay = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(++failures, 5))));
                    // Avoid logging browser URLs, cookies, or page content from an exception.
                    if (failures == 1) logger.Write(AppLogLevel.Warning, "TwitchBonuses",
                        $"{worker.Channel}: {worker.Message} ({error.GetType().Name})");
                }
                RefreshStatus();
                await Task.Delay(delay, worker.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (worker.Cancellation.IsCancellationRequested) { }
        finally
        {
            worker.Page?.Dispose();
            worker.Cancellation.Dispose();
        }
    }

    private static string DescribeError(Exception error) => error is WebView2RuntimeNotFoundException
        ? "Install Microsoft Edge WebView2 Runtime, then click Retry bonuses."
        : "Twitch bonus page could not load. Click Retry bonuses or sign in again.";

    private void RefreshStatus()
    {
        if (disposed || accountBusy || !Enabled) return;
        if (!signedIn)
        {
            Status = sessionError ?? (sessionExpired ? "Twitch bonus session ended. Sign in for bonuses again."
                : "Sign in to Twitch for bonuses. This is separate from Connect Twitch.");
            return;
        }
        Status = workers.Count == 0
            ? "Twitch website session saved. Open a live Twitch stream to claim bonuses."
            : string.Join(Environment.NewLine, workers.Values.Select(worker => $"{worker.Channel}: {worker.Message}"));
    }

    private void RecordClaim(Worker worker, string id)
    {
        if (!Enabled || !signedIn || accountBusy || worker.Cancellation.IsCancellationRequested ||
            !workers.TryGetValue(worker.Channel, out var current) || !ReferenceEquals(current, worker) ||
            string.IsNullOrWhiteSpace(id) || id.Length > 256) return;
        var history = settings.TwitchBonusClaims.GetValueOrDefault(worker.Channel) ?? new TwitchBonusClaimHistory();
        if (history.RecentClaimIds.Contains(id, StringComparer.Ordinal)) return;
        settings.TwitchBonusClaims = new(settings.TwitchBonusClaims, StringComparer.Ordinal)
        {
            [worker.Channel] = history.Record(id)
        };
        logger.Write(AppLogLevel.Info, "TwitchBonuses", $"{worker.Channel}: Bonus confirmed by Twitch; {settings.TwitchBonusClaims[worker.Channel].Count:N0} total.");
        _ = SaveHistoryAsync();
    }

    private async Task SaveHistoryAsync()
    {
        if (settingsService is null) return;
        var version = ++historyVersion;
        try
        {
            await settingsService.SaveAsync(settings);
            if (!disposed && version == historyVersion) HistorySaveStatus = "";
        }
        catch (Exception error)
        {
            if (!disposed && version == historyVersion)
                HistorySaveStatus = "Claim totals could not be saved. Click Retry bonuses to save them again.";
            logger.Write(AppLogLevel.Warning, "TwitchBonuses", $"Could not save bonus totals ({error.GetType().Name}).");
        }
    }

    private void RaiseChannelClaims()
    {
        OnPropertyChanged(nameof(ChannelClaims));
        OnPropertyChanged(nameof(HasNoChannelClaims));
    }

    private void OpenSelectedPage()
    {
        var tab = selectedTab();
        if (tab is not null && IsEligible(tab) && workers.TryGetValue(tab.Target.Channel.Trim().ToLowerInvariant(), out var worker)
            && worker.Page is not null) worker.Page.Show();
        else Status = "Select an open live Twitch stream, then open its bonus page.";
    }

    private void StopWorker(string channel)
    {
        if (!workers.Remove(channel, out var worker)) return;
        worker.Cancellation.Cancel();
        worker.Page?.Dispose();
        worker.Page = null;
    }
    private void StopAllWorkers()
    {
        foreach (var channel in workers.Keys.ToArray()) StopWorker(channel);
    }
    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(SignInStatus));
        OnPropertyChanged(nameof(RequiresSignIn));
        OnPropertyChanged(nameof(HasSavedSession));
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sessionTimer.Stop();
        lifetime.Cancel();
        StopAllWorkers();
        settings.PropertyChanged -= SettingsChanged;
        chat.PropertyChanged -= ChatChanged;
        tabs.CollectionChanged -= TabsChanged;
        foreach (var tab in observedTabs) tab.PropertyChanged -= TabChanged;
        observedTabs.Clear();
        browser.Dispose();
        lifetime.Dispose();
    }

    public sealed record ChannelClaimSummary(string Channel, long Count);

    private sealed class Worker(string channel)
    {
        internal string Channel { get; } = channel;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal ITwitchBonusPage? Page { get; set; }
        internal string Message { get; set; } = "Loading Twitch…";
    }
}
