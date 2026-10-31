using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Follows;

internal static class ChannelFollowSignInWindow
{
    internal static async Task<bool> ShowAsync(Window owner, CoreWebView2Environment environment,
        StreamTarget channel, CancellationToken cancellationToken)
    {
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = false;
        var closed = false;
        using var browser = new WebView2();
        var status = new TextBlock
        {
            Text = $"Sign in to {channel.Platform} below, then select Continue to follow {channel.Channel} with this website account.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        var proceed = new Button { Content = "Continue", IsEnabled = false, Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var buttons = new WrapPanel { Margin = new Thickness(6) };
        buttons.Children.Add(proceed);
        buttons.Children.Add(cancel);
        var layout = new DockPanel();
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Top);
        layout.Children.Add(status);
        layout.Children.Add(buttons);
        layout.Children.Add(browser);
        var window = new Window
        {
            Owner = owner,
            Title = $"Sign in to {channel.Platform} to follow {channel.Channel}",
            Width = 1100,
            Height = 800,
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        window.Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            completion.TrySetResult(accepted);
        };
        proceed.Click += (_, _) => { accepted = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Loaded += async (_, _) =>
        {
            try
            {
                await browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30), token);
                token.ThrowIfCancellationRequested();
                ChannelFollowService.ConfigureBrowser(browser.CoreWebView2, channel.Platform);
                browser.CoreWebView2.Navigate(channel.Platform == PlatformKind.Twitch ? "https://www.twitch.tv/login" : channel.Url);
                proceed.IsEnabled = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!closed) status.Text = "The sign-in browser could not start. Install or repair Microsoft Edge WebView2 Runtime, then retry Follow.";
            }
        };
        using var registration = token.Register(() => owner.Dispatcher.BeginInvoke(() => { if (!closed) window.Close(); }));
        window.Show();
        var result = await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
