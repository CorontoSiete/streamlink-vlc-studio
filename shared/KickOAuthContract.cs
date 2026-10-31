namespace StreamlinkVlcStudio.Authentication;

public static class KickOAuthContract
{
    public const string RedirectUri = "http://localhost:39177";
    public const string TokenPath = "/v1/kick/token";
    public static readonly string[] Scopes = ["user:read", "channel:read", "chat:write"];
}
