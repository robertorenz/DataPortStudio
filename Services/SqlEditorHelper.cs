using System.IO;
using System.Reflection;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using DataPortStudio.Services.SqlCompletion;

namespace DataPortStudio.Services;

public static class SqlEditorHelper
{
    private static IHighlightingDefinition? _sqlHighlighting;

    public static IHighlightingDefinition SqlHighlighting =>
        _sqlHighlighting ??= LoadSqlHighlighting();

    private static IHighlightingDefinition LoadSqlHighlighting()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("DataPortStudio.Assets.SQL.xshd")!;
        using var reader = new System.Xml.XmlTextReader(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    /// <summary>Applies SQL syntax highlighting and standard editor settings.</summary>
    public static void Configure(TextEditor editor)
    {
        editor.SyntaxHighlighting = SqlHighlighting;
        editor.FontFamily = new FontFamily("Consolas");
        editor.FontSize = 13;
        editor.ShowLineNumbers = true;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.ConvertTabsToSpaces = false;
        editor.Options.IndentationSize = 4;
        editor.Padding = new System.Windows.Thickness(8);
        var sp = SearchPanel.Install(editor);
        // MarkerBrush must be set after Install() — applying it via an implicit Style
        // before the panel is attached to a TextArea throws NullReferenceException.
        sp.MarkerBrush = ThemeManager.Current == "dark"
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x66, 0x4F, 0xC3, 0xF7))  // blue
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x66, 0x15, 0x65, 0xC0)); // accent blue
        ApplyThemeColors(editor);
    }

    private static void ApplyThemeColors(TextEditor editor)
    {
        var res = System.Windows.Application.Current.Resources;
        if (res["B.Surface"] is System.Windows.Media.Brush bg)
            editor.Background = bg;
        if (res["B.Text"] is System.Windows.Media.Brush fg)
            editor.Foreground = fg;
        if (res["B.TextMuted"] is System.Windows.Media.Brush ln)
            editor.LineNumbersForeground = ln;
    }

    /// <summary>
    /// Attaches SQL autocompletion to the editor.
    /// Starts loading the schema in the background; completion is available as soon as it finishes.
    /// </summary>
    public static SqlSchemaCache ConfigureCompletion(TextEditor editor, Models.ConnectionProfile connection,
        string? database, string? schema = null)
    {
        var cache = new SqlSchemaCache();
        _ = cache.LoadAsync(connection, database, schema);
        _ = new SqlCompletionProvider(editor, cache); // registers event handlers
        return cache;
    }
}
