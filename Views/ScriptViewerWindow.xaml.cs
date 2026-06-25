using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace DataPortStudio.Views;

public partial class ScriptViewerWindow : Window
{
    private readonly string _suggestedName;

    public ScriptViewerWindow(string title, string script, string suggestedName)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        Title = title;
        TitleLabel.Text = title;
        Editor.Text = script;
        _suggestedName = suggestedName;
        Status.Text = $"{script.Split('\n').Length:N0} line(s).";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Editor.Text); Status.Text = "Copied to clipboard."; }
        catch { Status.Text = "Could not access the clipboard."; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = _suggestedName + ".sql",
            DefaultExt = "sql",
            Filter = "SQL script (*.sql)|*.sql|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, Editor.Text, new UTF8Encoding(true));
            Status.Text = "Saved to " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Status.Text = "Save failed: " + ex.Message;
        }
    }
}
