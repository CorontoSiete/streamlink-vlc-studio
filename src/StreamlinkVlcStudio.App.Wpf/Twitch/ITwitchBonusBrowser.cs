namespace StreamlinkVlcStudio.App.Wpf.Twitch;

internal interface ITwitchBonusBrowser : IDisposable
{
    Task<bool> HasSessionAsync(CancellationToken cancellationToken);
    Task SignInAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
    Task<ITwitchBonusPage> OpenChannelAsync(string channel, CancellationToken cancellationToken);
}

internal interface ITwitchBonusPage : IDisposable
{
    event EventHandler<string>? ClaimConfirmed;
    Task<string> CheckAsync(CancellationToken cancellationToken);
    void Show();
}

internal sealed class TwitchBonusSessionExpiredException : Exception;
