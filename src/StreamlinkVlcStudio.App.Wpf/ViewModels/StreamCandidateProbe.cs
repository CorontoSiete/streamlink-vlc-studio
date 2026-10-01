using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed record StreamCandidateProbe(
    StreamTarget Target,
    StreamlinkProbeResult Result,
    StreamMetadataResult? Metadata = null,
    StreamSearchChannel? Channel = null,
    int? ViewerCount = null);
