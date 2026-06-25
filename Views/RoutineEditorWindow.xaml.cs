using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class RoutineEditorWindow : Window
{
    private readonly ConnectionProfile _connection;
    private readonly string? _database;
    private readonly string _schema;
    private readonly string _name;
    private readonly bool _isNew;

    public RoutineEditorWindow(ConnectionProfile connection, string? database, string schema, string name, string kind,
        string? template = null)
    {
        InitializeComponent();
        _connection = connection;
        _database = database;
        _schema = schema;
        _name = name;
        _isNew = template is not null;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

        Title = _isNew ? $"New {kind}" : $"Edit {kind} — {schema}.{name}";
        TitleLabel.Text = Title;

        SqlEditorHelper.Configure(Editor);
        SqlEditorHelper.ConfigureCompletion(Editor, connection, database, schema);

        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; await SaveAsync(); }
            if (e.Key == Key.F && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
            { e.Handled = true; FormatSql(); }
        };

        if (_isNew)
        {
            Editor.Document.Text = template!;
            Messages.Text = "New object — edit the definition and press Ctrl+S to create it.";
        }
        else
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        Messages.Text = "Loading…";
        try
        {
            var def = await SqlServerService.GetObjectDefinitionAsync(
                _connection.BuildConnectionString(), _database ?? "", _schema, _name);
            if (string.IsNullOrEmpty(def))
            {
                Editor.Document.Text = "-- Definition not available (the object may be encrypted).";
                Messages.Text = "No definition available.";
            }
            else
            {
                Editor.Document.Text = def;
                Messages.Text = "Loaded. Edit and press Ctrl+S to save.";
            }
        }
        catch (Exception ex)
        {
            Editor.Document.Text = "";
            Messages.Text = "Error: " + ex.Message;
        }
    }

    private void Format_Click(object sender, RoutedEventArgs e) => FormatSql();

    private void FormatSql()
    {
        var selLen = Editor.SelectionLength;
        if (selLen > 0)
        {
            var selStart = Editor.SelectionStart;
            Editor.Document.Replace(selStart, selLen, SqlBeautifier.Format(Editor.Document.GetText(selStart, selLen)));
        }
        else
        {
            var caret = Editor.CaretOffset;
            Editor.Document.Text = SqlBeautifier.Format(Editor.Document.Text);
            Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        var text = Editor.Document.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        SaveButton.IsEnabled = false;
        Messages.Text = "Saving…";
        try
        {
            var sql = _isNew ? text : MakeAlter(text);
            await SqlServerService.ExecuteAsync(_connection.BuildConnectionString(), _database ?? "", sql);
            Messages.Text = "Saved successfully.";
        }
        catch (Exception ex)
        {
            Messages.Text = "Error: " + ex.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>Turns a leading CREATE (or CREATE OR ALTER) into ALTER so an existing routine updates in place.</summary>
    private static string MakeAlter(string text)
    {
        var orAlter = new Regex(@"\bCREATE\s+OR\s+ALTER\b", RegexOptions.IgnoreCase);
        if (orAlter.IsMatch(text)) return orAlter.Replace(text, "ALTER", 1);
        var createKw = new Regex(@"\bCREATE\b(\s+)(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b", RegexOptions.IgnoreCase);
        return createKw.Replace(text, "ALTER$1$2", 1);
    }
}
