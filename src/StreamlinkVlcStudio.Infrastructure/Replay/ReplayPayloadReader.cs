using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Time;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using StreamlinkVlcStudio.Infrastructure.Viewers;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

internal static class ReplayPayloadReader
{

    // Preserve provider text until the field-specific normalization step.
    internal static string ReadReplayString(JsonElement element, string propertyName)
    {
        return JsonElementReader.GetOptionalString(element, propertyName, trimStrings: false);
    }

    internal static string UnescapeJsonUrl(string value) =>
        value.Trim().Trim('"').Replace("\\/", "/", StringComparison.Ordinal);
}
