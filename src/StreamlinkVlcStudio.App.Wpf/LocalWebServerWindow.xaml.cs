using System.Windows;

namespace StreamlinkVlcStudio.App.Wpf;

internal partial class LocalWebServerWindow : Window
{
    internal LocalWebServerWindow() => InitializeComponent();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
