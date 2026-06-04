using System.Windows.Input;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class StreamSearchResultViewModel : ObservableObject
{
    private readonly StreamlinkProbeResult probeResult;
    private readonly StreamMetadataResult? metadata;

    public StreamSearchResultViewModel(
        StreamTarget target,
        StreamlinkProbeResult probeResult,
        StreamMetadataResult? metadata,
        Func<StreamSearchResultViewModel, bool, Task> openAsync)
    {
        Target = target;
        this.probeResult = probeResult;
        this.metadata = metadata?.State == StreamMetadataState.Available ? metadata : null;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

    public StreamTarget Target { get; }

    public PlatformKind Platform => Target.Platform;

    public string PlatformText => Target.Platform.ToString();

    public string Channel => Target.Channel;

    public string DisplayName => FirstNonEmpty(metadata?.DisplayName, Target.Channel);

    public string ThumbnailUrl => FirstNonEmpty(metadata?.ThumbnailUrl);

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    public string CategoryName => FirstNonEmpty(metadata?.CategoryName);

    public bool HasCategory => !string.IsNullOrWhiteSpace(CategoryName);

    public string Url => Target.Url;

    public string StatusText => string.IsNullOrWhiteSpace(probeResult.Message)
        ? "Playable stream found."
        : probeResult.Message;

    private static bool ShouldStayOnHomeForOpenCommand()
    {
        return (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }
}
