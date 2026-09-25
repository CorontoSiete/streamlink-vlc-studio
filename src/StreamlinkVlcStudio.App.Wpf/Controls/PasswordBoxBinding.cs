using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public static class PasswordBoxBinding
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(PasswordBoxBinding),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password",
        typeof(string),
        typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(
            "",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnPasswordChanged,
            null,
            false,
            UpdateSourceTrigger.LostFocus));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static string GetPassword(DependencyObject element) => (string)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string value) => element.SetValue(PasswordProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged -= PasswordBoxPasswordChanged;
        if ((bool)e.NewValue)
        {
            passwordBox.Password = GetPassword(passwordBox) ?? "";
            passwordBox.PasswordChanged += PasswordBoxPasswordChanged;
        }
    }

    private static void OnPasswordChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && GetIsEnabled(passwordBox))
        {
            var password = (string?)e.NewValue ?? "";
            if (passwordBox.Password != password)
            {
                passwordBox.Password = password;
            }
        }
    }

    private static void PasswordBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        var passwordBox = (PasswordBox)sender;
        // Keep the binding intact so authorization and Clear token still update the field.
        passwordBox.SetCurrentValue(PasswordProperty, passwordBox.Password);
    }
}
