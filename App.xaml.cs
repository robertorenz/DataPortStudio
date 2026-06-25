using System.Windows;
using DataPortStudio.Services;

namespace DataPortStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply theme BEFORE base.OnStartup so StaticResource bindings resolve correctly
        ThemeManager.Apply(SettingsStore.Current.Theme);
        base.OnStartup(e);
        LocalizationManager.Instance.Language = SettingsStore.Current.Language;

        try
        {
            var uri = new Uri("pack://application:,,,/Assets/dataporticon.png", UriKind.Absolute);
            var icon = new System.Windows.Media.Imaging.BitmapImage(uri);
            if (MainWindow != null) MainWindow.Icon = icon;
        }
        catch { }
    }
}
