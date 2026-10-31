using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public interface ILocalWebChatPresentation
{
    long Version { get; }
    void EnsureLoaded(ChatMessage message, ChatSettings settings) { }
    ChatMessage Present(ChatMessage message, ChatSettings settings);
    Task ServeAssetAsync(HttpContext context, string assetId);
}
