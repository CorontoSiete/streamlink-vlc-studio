using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamlinkVlcStudio.App.Wpf.ViewModels;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private MainViewModel? studioNavigationViewModel;

    private void SetStudioNavigationViewModel(MainViewModel? model)
    {
        if (studioNavigationViewModel is not null)
        {
            studioNavigationViewModel.PropertyChanged -= StudioNavigationOnPropertyChanged;
        }

        studioNavigationViewModel = model;
        if (model is not null)
        {
            model.PropertyChanged += StudioNavigationOnPropertyChanged;
        }
    }

    private void StudioNavigationOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedSettingsCategory)) return;

        // ScrollViewer queues these commands until layout. Issuing them now lets a later
        // focus/BringIntoView request win instead of resetting the user's destination.
        SettingsContentScrollViewer.ScrollToHome();
        SettingsViewport.ScrollToHome();
    }

    private void ClearHomeSearch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        model.NewStreamText = "";
        model.DismissStreamSearchDropdown();
        HomeStreamSearchTextBox.Focus();
    }

    private void HomeSearchPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var input = new HotkeyGesture(HotkeyGesture.GetEventKey(e), Keyboard.Modifiers);
        if (TryNavigateHomeSearch(input, Keyboard.FocusedElement))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None ||
            DataContext is not MainViewModel model) return;

        HomeStreamSearchTextBox.Focus();
        // Focusing the input reopens verified results; Escape must leave them closed.
        model.DismissStreamSearchDropdown();
        e.Handled = true;
    }

    private bool TryNavigateHomeSearch(HotkeyGesture input, IInputElement? focusedElement)
    {
        if (input.MouseButton is not null || input.Modifiers != ModifierKeys.None ||
            input.Key is not (Key.Up or Key.Down) ||
            studioNavigationViewModel is not { } model)
        {
            return false;
        }

        var fromInput = ReferenceEquals(focusedElement, HomeStreamSearchTextBox);
        var focusedButton = focusedElement as Button;
        if (!fromInput && (focusedButton is null || !HomeSearchResults.IsAncestorOf(focusedButton)))
        {
            return false;
        }

        if (!model.IsStreamSearchPanelVisible)
        {
            if (!fromInput || !model.IsHomeVisible || model.IsSettingsOpen || !model.HasStreamSearchResults)
                return false;
            model.ShowStreamSearchDropdown();
            if (!model.IsStreamSearchPanelVisible) return false;
        }

        // Use the realized controls so unavailable results are skipped and the keyboard
        // follows exactly the same commands and focus visuals as pointer interaction.
        HomeSearchResults.UpdateLayout();
        var buttons = new List<Button>();
        for (var index = 0; index < HomeSearchResults.Items.Count; index++)
        {
            if (HomeSearchResults.ItemContainerGenerator.ContainerFromIndex(index) is { } container &&
                FindVisualChild<Button>(container) is { IsEnabled: true, IsVisible: true } button)
            {
                buttons.Add(button);
            }
        }
        if (buttons.Count == 0) return false;

        var direction = input.Key == Key.Down ? 1 : -1;
        var currentIndex = fromInput ? (direction > 0 ? -1 : buttons.Count) : buttons.IndexOf(focusedButton!);
        var nextIndex = currentIndex + direction;
        if (nextIndex < 0 || nextIndex >= buttons.Count)
        {
            return HomeStreamSearchTextBox.Focus();
        }

        var next = buttons[nextIndex];
        if (!next.Focus()) return false;
        next.BringIntoView();
        return true;
    }

    private void OpenAccountsSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        model.SelectedSettingsCategory = SettingsCategory.Accounts;
        model.IsSettingsOpen = true;
    }
}
