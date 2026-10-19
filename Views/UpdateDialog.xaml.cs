using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

/// <summary>Offers a newer release, then downloads and launches its installer.</summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _download;

    private static string L(string key) => LocalizationManager.Instance[key];

    public UpdateDialog(UpdateInfo update)
    {
        InitializeComponent();
        _update = update;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

        TitleText.Text = L("Update_Title");
        VersionText.Text = string.Format(L("Update_VersionLine"), update.Version, UpdateService.CurrentVersion);
        NotesBox.Text = PlainNotes(update.Notes);
    }

    /// <summary>Shows the offer for <paramref name="update"/>. Returns only if the user didn't install.</summary>
    public static void Offer(UpdateInfo update) => new UpdateDialog(update).ShowDialog();

    /// <summary>Strips the most common Markdown so the release notes read cleanly as plain text.</summary>
    private static string PlainNotes(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");
        // Drop the Downloads table at the end of the release notes.
        var downloads = text.IndexOf("\n## Downloads", StringComparison.OrdinalIgnoreCase);
        if (downloads >= 0) text = text[..downloads];
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^(\s*)[-*]\s+", "$1•  ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"(?<!\w)\*(.+?)\*(?!\w)", "$1");
        text = text.Replace("`", "");
        return text.Trim();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Current.Clone();
        settings.SkippedUpdateVersion = _update.Version.ToString();
        SettingsStore.Save(settings);
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_download is not null) { _download.Cancel(); return; }
        Close();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _download = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        LaterButton.Content = L("Btn_Cancel");
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = L("Update_Downloading");

        var progress = new Progress<double>(p =>
        {
            Progress.Value = p;
            var mb = _update.Size / 1048576.0;
            ProgressText.Text = mb > 0
                ? string.Format(L("Update_DownloadingSize"), p * mb, mb)
                : L("Update_Downloading");
        });

        string installer;
        try
        {
            installer = await UpdateService.DownloadAsync(_update, progress, _download.Token);
        }
        catch (OperationCanceledException)
        {
            ResetAfterDownload();
            return;
        }
        catch (Exception ex)
        {
            ResetAfterDownload();
            Dialogs.ShowError(L("Update_FailedTitle"), ex.Message);
            return;
        }

        ProgressText.Text = L("Update_Starting");
        try
        {
            UpdateService.LaunchInstaller(installer);
        }
        catch (Exception ex)
        {
            // Typically the UAC prompt was declined.
            ResetAfterDownload();
            Dialogs.ShowError(L("Update_FailedTitle"), ex.Message);
            return;
        }

        // The installer replaces DataPortStudio.exe, so the app must exit; it is relaunched when setup finishes.
        Application.Current?.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _download?.Cancel();
        base.OnClosed(e);
    }

    private void ResetAfterDownload()
    {
        _download = null;
        InstallButton.IsEnabled = true;
        SkipButton.IsEnabled = true;
        LaterButton.Content = L("Update_Later");
        ProgressPanel.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
    }
}
