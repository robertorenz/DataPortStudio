using System.Windows;

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

        var newDict = new ResourceDictionary { Source = uri };

        if (existing != null)
        {
            var idx = merged.IndexOf(existing);
            merged[idx] = newDict;
        }
        else
        {
            merged.Insert(0, newDict);
        }
    }

    public static string Current =>
        Application.Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("ThemeDark") == true) != null
            ? "dark" : "light";
}
