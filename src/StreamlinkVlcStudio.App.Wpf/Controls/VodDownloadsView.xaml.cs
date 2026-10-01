using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamlinkVlcStudio.App.Wpf.ViewModels;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public partial class VodDownloadsView : UserControl
{
    public VodDownloadsView() => InitializeComponent();

    private void DownloadUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None ||
            DataContext is not MainViewModel main || !main.DownloadVodUrlCommand.CanExecute(null)) return;
        e.Handled = true;
        if (!e.IsRepeat) main.DownloadVodUrlCommand.Execute(null);
    }

    private void DownloadInputSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is ResponsiveDockPanel panel)
            VodDownloadButton.Margin = args.NewSize.Width < panel.CompactWidth
                ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
    }
}
