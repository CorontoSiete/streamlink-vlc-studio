using StreamlinkVlcStudio.App.Wpf.Follows;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IChannelFollowService? channelFollowService;
    private CancellationTokenSource? activeChannelFollowCancellation;
    // Retain completed attempts until their tab closes, including unverified or failed reads.
    private readonly Dictionary<StreamTabViewModel, CancellationTokenSource?> channelFollowStatusChecks = [];
    private readonly Dictionary<string, bool> channelFollowStates = new(StringComparer.OrdinalIgnoreCase);
    private string followingStateKey = "";
    private bool isUnfollowing;

    public AsyncRelayCommand FollowSelectedChannelCommand { get; }

    public bool IsKickFollowChangeBusy => followed.IsKickFollowChangeBusy;

    private bool IsCheckingSelectedChannelFollowStatus => SelectedTab is { } tab &&
        channelFollowStatusChecks.TryGetValue(tab, out var cancellation) && cancellation is not null;

    public bool IsSelectedChannelFollowed => ChannelFollowResponseTracker.TryGetChannel(SelectedTab?.Target, out var channel) &&
        channelFollowStates.TryGetValue(channel.StateKey, out var following) && following;

    public string FollowButtonText => ChannelFollowResponseTracker.TryGetChannel(SelectedTab?.Target, out var channel)
        ? channel.StateKey == followingStateKey ? isUnfollowing ? "Unfollowing\u2026" : "Following\u2026"
            : IsCheckingSelectedChannelFollowStatus ? "Checking\u2026" : IsSelectedChannelFollowed ? "Following" : "Follow"
        : "Follow";

    public string FollowButtonToolTip => ChannelFollowResponseTracker.TryGetChannel(SelectedTab?.Target, out var channel)
        ? IsCheckingSelectedChannelFollowStatus ? $"Checking whether you follow {channel.Channel} on {channel.Platform}"
            : IsSelectedChannelFollowed
                ? $"Unfollow {channel.Channel} with your {channel.Platform} website account"
                : $"Follow {channel.Channel} with your {channel.Platform} website account"
        : "Select a Twitch or Kick broadcaster to follow";

    public string FollowButtonAccessibleName => IsSelectedChannelFollowed ? "Unfollow selected streamer" : "Follow selected streamer";

    private bool CanFollowSelectedChannel() => channelFollowService is not null &&
        ChannelFollowResponseTracker.TryGetChannel(SelectedTab?.Target, out var channel) &&
        !IsCheckingSelectedChannelFollowStatus &&
        (channel.Platform != PlatformKind.Kick || !followed.IsKickFollowChangeBusy);

    private void RaiseFollowButtonState()
    {
        OnPropertyChanged(nameof(IsSelectedChannelFollowed));
        OnPropertyChanged(nameof(FollowButtonText));
        OnPropertyChanged(nameof(FollowButtonToolTip));
        OnPropertyChanged(nameof(FollowButtonAccessibleName));
        FollowSelectedChannelCommand.RaiseCanExecuteChanged();
    }

    private void CheckOpenedChannelFollowStatus(StreamTabViewModel tab)
    {
        if (disposed || channelFollowService is null || channelFollowStatusChecks.ContainsKey(tab) ||
            !ChannelFollowResponseTracker.TryGetChannel(tab.Target, out var channel)) return;
        // An outstanding follow/unfollow will supply the authoritative state for this channel.
        if (channel.StateKey == followingStateKey)
        {
            channelFollowStatusChecks.Add(tab, null);
            return;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        channelFollowStatusChecks.Add(tab, cancellation);
        RaiseFollowButtonState();
        backgroundOperationController.Track(RefreshAsync());

        async Task RefreshAsync()
        {
            try
            {
                var result = await channelFollowService.GetFollowStatusAsync(tab.Target, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (disposed || !channelFollowStatusChecks.TryGetValue(tab, out var current) ||
                    !ReferenceEquals(current, cancellation)) return;
                if (result is null) channelFollowStates.Remove(channel.StateKey);
                else
                {
                    ValidateFollowResult(result, channel);
                    channelFollowStates[channel.StateKey] = result.IsFollowing;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested || disposed) { }
            catch (Exception ex) when (!disposed)
            {
                logger.Write(AppLogLevel.Warning, "Follow", $"Could not check follow status for {channel.Channel} on {channel.Platform}.", ex);
            }
            finally
            {
                if (channelFollowStatusChecks.TryGetValue(tab, out var current) && ReferenceEquals(current, cancellation))
                {
                    channelFollowStatusChecks[tab] = null;
                    if (!disposed) RaiseFollowButtonState();
                }
                cancellation.Dispose();
            }
        }
    }

    private void RemoveClosedChannelFollowStatusChecks()
    {
        foreach (var tab in channelFollowStatusChecks.Keys.Where(tab => !Tabs.Contains(tab)).ToArray())
        {
            var cancellation = channelFollowStatusChecks[tab];
            channelFollowStatusChecks.Remove(tab);
            if (cancellation is not null) CancellationSourceCleanup.Cancel(cancellation);
        }
    }

    private void CancelFollowStatusChecks(string? stateKey = null)
    {
        foreach (var (tab, cancellation) in channelFollowStatusChecks.ToArray())
        {
            if (cancellation is null || stateKey is not null &&
                (!ChannelFollowResponseTracker.TryGetChannel(tab.Target, out var channel) || channel.StateKey != stateKey)) continue;
            // Invalidate before cancellation so a late read cannot overwrite a confirmed change.
            channelFollowStatusChecks[tab] = null;
            CancellationSourceCleanup.Cancel(cancellation);
        }
    }

    private Task FollowSelectedChannelAsync()
    {
        var operation = FollowSelectedChannelCoreAsync();
        backgroundOperationController.Track(operation);
        return operation;
    }

    private async Task FollowSelectedChannelCoreAsync()
    {
        if (channelFollowService is null || SelectedTab is not { } tab ||
            !ChannelFollowResponseTracker.TryGetChannel(tab.Target, out var channel)) return;
        // Capture both broadcaster and intent before any await.
        var target = tab.Target;
        var shouldFollow = !IsSelectedChannelFollowed;
        var action = shouldFollow ? "Following" : "Unfollowing";
        var kick = channel.Platform == PlatformKind.Kick;
        if (kick && !followed.TryBeginKickFollowChange()) return;
        CancelFollowStatusChecks(channel.StateKey);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        activeChannelFollowCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            if (kick) followed.NotifyKickFollowChange();
            followingStateKey = channel.StateKey;
            isUnfollowing = !shouldFollow;
            RaiseFollowButtonState();
            StatusMessage = $"{action} {channel.Channel} on {channel.Platform}";
            var result = shouldFollow ? await channelFollowService.FollowAsync(target, token)
                : await channelFollowService.UnfollowAsync(target, token);
            token.ThrowIfCancellationRequested();
            if (disposed) return;
            if (result is null)
            {
                StatusMessage = $"{action} {channel.Channel} on {channel.Platform} was not confirmed.";
                return;
            }
            ValidateFollowResult(result, channel);
            if (result.IsFollowing != shouldFollow)
                throw new InvalidOperationException("The requested follow state was not confirmed. The library was not changed.");

            if (kick)
            {
                var channels = ValidateKickFollows(result.KickFollowedChannels, channel.Channel, shouldFollow);
                followed.ApplyImportedKickFollows(channels);
                await settingsAutoSave.FlushAsync();
                token.ThrowIfCancellationRequested();
                if (disposed) return;
            }
            channelFollowStates[channel.StateKey] = shouldFollow;
            followingStateKey = "";
            RaiseFollowButtonState();
            // Complete the button independently of slower library metadata refreshes.
            _ = RefreshFollowedChannelsAsync();
            if (!disposed)
                StatusMessage = $"{(shouldFollow ? "Following" : "Unfollowed")} {channel.Channel} on {channel.Platform}." +
                    (kick && HasSettingsSaveError
                        ? " The imported follows could not be saved. Retry saving in Settings." : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || disposed) { }
        catch (OperationCanceledException)
        {
            if (!disposed) StatusMessage = $"{action} {channel.Channel} on {channel.Platform} was not confirmed.";
        }
        catch (Exception ex) when (!disposed)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Follow", $"{action} {channel.Channel} on {channel.Platform} failed.", ex);
        }
        finally
        {
            if (ReferenceEquals(activeChannelFollowCancellation, cancellation)) activeChannelFollowCancellation = null;
            if (kick) followed.EndKickFollowChange();
            if (!disposed)
            {
                followingStateKey = "";
                RaiseFollowButtonState();
            }
        }
    }

    private static void ValidateFollowResult(ChannelFollowResult result, StreamTarget channel)
    {
        if (result.Platform != channel.Platform ||
            !string.Equals(result.Channel, channel.Channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The follow result did not match the selected broadcaster. The library was not changed.");
    }

    private static IReadOnlyList<string> ValidateKickFollows(IReadOnlyList<string>? values, string channel, bool following)
    {
        if (values is null || values.Any(value => !StreamInputParser.TryFromChannel(PlatformKind.Kick, value, out _)))
            throw new InvalidOperationException("Kick's followed list was not verified. The imported library was not changed.");
        var channels = values.Select(value => StreamInputParser.FromChannel(PlatformKind.Kick, value).Channel)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (channels.Contains(channel, StringComparer.OrdinalIgnoreCase) != following)
            throw new InvalidOperationException("Kick did not confirm the broadcaster's follow state. The imported library was not changed.");
        return channels;
    }
}
