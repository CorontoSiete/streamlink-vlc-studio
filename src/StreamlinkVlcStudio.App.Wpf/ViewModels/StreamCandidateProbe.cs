using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using StreamlinkVlcStudio.App.Wpf.Notifications;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Vlc;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed record StreamCandidateProbe(
    StreamTarget Target,
    StreamlinkProbeResult Result,
    StreamMetadataResult? Metadata = null,
    StreamSearchChannel? Channel = null,
    int? ViewerCount = null);
