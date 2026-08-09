using System.Windows;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Themes;

public static class ThemeManager
{
    private static readonly Dictionary<AppTheme, Uri> ThemeUris = new()
    {
        [AppTheme.Dark] = PackUri("DarkColors.xaml"),
        [AppTheme.Light] = PackUri("LightColors.xaml"),
        [AppTheme.MidnightBlue] = PackUri("MidnightBlueColors.xaml"),
        [AppTheme.Dracula] = PackUri("DraculaColors.xaml"),
        [AppTheme.Nord] = PackUri("NordColors.xaml"),
        [AppTheme.Solarized] = PackUri("SolarizedColors.xaml"),
        [AppTheme.Monokai] = PackUri("MonokaiColors.xaml"),
        [AppTheme.Cyberpunk] = PackUri("CyberpunkColors.xaml")
    };

    private static ResourceDictionary? currentColorDictionary;

    public static void ApplyTheme(AppTheme theme)
    {
        // The palette lives in application resources, so there is nothing to swap without a WPF
        // Application (headless hosts and designer/test shells run windows on a bare dispatcher).
        if (Application.Current is not { } application)
        {
            return;
        }

        if (!ThemeUris.TryGetValue(theme, out var uri))
        {
            uri = ThemeUris[AppTheme.Dark];
        }

        var newDictionary = new ResourceDictionary { Source = uri };
        var mergedDictionaries = application.Resources.MergedDictionaries;

        if (currentColorDictionary is null)
        {
            currentColorDictionary = FindExistingColorDictionary(mergedDictionaries);
        }

        if (currentColorDictionary is not null)
        {
            var index = mergedDictionaries.IndexOf(currentColorDictionary);
            if (index >= 0)
            {
                mergedDictionaries[index] = newDictionary;
            }
            else
            {
                mergedDictionaries.Insert(0, newDictionary);
            }
        }
        else
        {
            mergedDictionaries.Insert(0, newDictionary);
        }

        currentColorDictionary = newDictionary;
    }

    private static ResourceDictionary? FindExistingColorDictionary(IList<ResourceDictionary> mergedDictionaries)
    {
        foreach (var dictionary in mergedDictionaries)
        {
            if (dictionary.Source is { } source &&
                source.OriginalString.Contains("Colors/", StringComparison.OrdinalIgnoreCase))
            {
                return dictionary;
            }
        }

        return null;
    }

    // Absolute pack URI: a relative one resolves against the entry assembly, so it only works while
    // this assembly is the one that started the process.
    private static Uri PackUri(string fileName) =>
        new($"pack://application:,,,/StreamlinkVlcStudio.App.Wpf;component/Themes/Colors/{fileName}");
}
