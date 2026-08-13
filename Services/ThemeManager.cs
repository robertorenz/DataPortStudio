using System.Windows;
using System.Windows.Media;

namespace DataPortStudio.Services;

public static class ThemeManager
{
    private const string LightSource = "Themes/Theme.xaml";
    private const string DarkSource  = "Themes/ThemeDark.xaml";

    /// <summary>
    /// Swaps the application theme ResourceDictionary.
    /// Call before MainWindow is created (i.e. before base.OnStartup) so that
    /// all StaticResource references resolve against the correct theme.
    /// </summary>
    public static void Apply(string theme)
    {
        var source = theme == "dark" ? DarkSource : LightSource;
        var uri = new Uri(source, UriKind.Relative);

        var merged = Application.Current.Resources.MergedDictionaries;

        // Replace the first merged dictionary (the theme) with the target one
        var existing = merged.FirstOrDefault(d => d.Source != null &&
            (d.Source.OriginalString.Contains("Theme.xaml") ||
             d.Source.OriginalString.Contains("ThemeDark.xaml")));

        // Avoid reloading the active dictionary. This also keeps Current stable while
        // the Settings dialog is open and the selected value has not changed.
        if (existing?.Source?.OriginalString.EndsWith(source, StringComparison.OrdinalIgnoreCase) == true)
            return;

        var newDict = new ResourceDictionary { Source = uri };

        if (existing != null)
        {
            // Most views use StaticResource, so they retain the brush instances resolved
            // when they were created. Update those instances before swapping dictionaries
            // to make the theme change visible immediately without recreating the windows.
            UpdateReferencedBrushes(existing, newDict);

            var idx = merged.IndexOf(existing);
            merged[idx] = newDict;
        }
        else
        {
            merged.Insert(0, newDict);
        }
    }

    private static void UpdateReferencedBrushes(ResourceDictionary current, ResourceDictionary next)
    {
        foreach (var key in current.Keys.Cast<object>().ToArray())
        {
            if (!next.Contains(key)) continue;
            if (current[key] is not SolidColorBrush currentBrush ||
                next[key] is not SolidColorBrush nextBrush || currentBrush.IsFrozen)
                continue;

            currentBrush.Color = nextBrush.Color;
            currentBrush.Opacity = nextBrush.Opacity;
        }
    }

    public static string Current =>
        Application.Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("ThemeDark") == true) != null
            ? "dark" : "light";
}
