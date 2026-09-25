using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace StreamlinkVlcStudio.Bootstrapper;

public partial class MainWindow : Window
{
    private readonly StudioBootstrapperApplication application;
    private bool allowClose;

    internal MainWindow(StudioBootstrapperApplication application, BootstrapperViewModel viewModel)
    {
        this.application = application;
        DataContext = viewModel;
        InitializeComponent();
    }

    public nint WindowHandle => new WindowInteropHelper(this).EnsureHandle();

    public void CloseFromApplication()
    {
        allowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
        {
            return;
        }

        if (application.IsApplying)
        {
            e.Cancel = true;
            var answer = MessageBox.Show(
                this,
                "Cancel the current setup operation? Setup will finish rolling back before it closes.",
                "Cancel setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
            {
                application.RequestCancel();
            }

            return;
        }

        application.NotifyWindowClosing();
    }
}
