using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly AppSettings settings;
    private readonly IAppLogger logger;

    public ChatClientFactory(AppSettings settings, IAppLogger logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    public IChatClient Create(PlatformKind platform)
    {
        return platform switch
        {
            PlatformKind.Twitch => new TwitchChatClient(settings.Chat, logger),
            PlatformKind.Kick => new KickChatClient(settings.Chat, logger),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
        };
    }
}
