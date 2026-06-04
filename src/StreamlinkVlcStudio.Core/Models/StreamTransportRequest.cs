namespace StreamlinkVlcStudio.Core.Models;

public sealed record StreamTransportRequest(
    StreamTarget Target,
    string Quality,
    string StreamlinkPath,
    bool LowLatency,
    IReadOnlyList<string> CustomArguments);
