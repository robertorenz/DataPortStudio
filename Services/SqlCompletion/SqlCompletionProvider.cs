using System.Text.RegularExpressions;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;

namespace DataPortStudio.Services.SqlCompletion;

/// <summary>Wires AvalonEdit TextEditor to SQL keyword + schema autocompletion.</summary>
public class SqlCompletionProvider
{
    private static readonly string[] SqlKeywords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN",
        "ON", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS NULL", "IS NOT NULL",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "MERGE",
        "CREATE", "ALTER", "DROP", "TRUNCATE",
        "GROUP BY", "ORDER BY", "HAVING", "DISTINCT", "TOP", "OFFSET", "FETCH NEXT",
        "UNION", "UNION ALL", "INTERSECT", "EXCEPT",
        "WITH", "AS", "CASE", "WHEN", "THEN", "ELSE", "END",
        "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION",
        "DECLARE", "EXEC", "EXECUTE", "RETURN", "OUTPUT",
        "IF", "WHILE", "BREAK", "CONTINUE",
        "GO", "USE", "NOCOUNT", "PRINT",
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "CAST", "CONVERT", "COALESCE", "ISNULL", "NULLIF", "IIF",
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "DATEADD", "DATEDIFF", "DATEPART", "FORMAT",
        "LEN", "SUBSTRING", "UPPER", "LOWER", "TRIM", "LTRIM", "RTRIM", "REPLACE",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD",
        "NEWID", "SCOPE_IDENTITY", "OBJECT_ID",
        "INT", "BIGINT", "SMALLINT", "TINYINT", "BIT",
        "VARCHAR", "NVARCHAR", "CHAR", "NCHAR", "TEXT", "NTEXT",
        "DATETIME", "DATETIME2", "DATE", "TIME", "DATETIMEOFFSET",
        "DECIMAL", "NUMERIC", "FLOAT", "REAL", "MONEY",
        "UNIQUEIDENTIFIER", "VARBINARY", "XML",
        "NULL", "TRUE", "FALSE",
    ];

    // Matches table name after FROM/JOIN/INTO/UPDATE, with optional alias
    private static readonly Regex FromTablePattern = new(
        @"\b(?:FROM|JOIN|INTO|UPDATE)\s+([\w\.\[\]""]+)(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TextEditor _editor;
    private readonly SqlSchemaCache _schema;
    private CompletionWindow? _completionWindow;

    public SqlCompletionProvider(TextEditor editor, SqlSchemaCache schema)
    {
        _editor = editor;
        _schema = schema;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            ShowCompletion(forceAll: true);
        }
    }

    private void OnTextEntered(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (e.Text == ".")
        {
            ShowCompletion(dotTriggered: true);
        }
        else if (e.Text.Length == 1 && (char.IsLetter(e.Text[0]) || e.Text[0] == '_'))
        {
            if (GetCurrentWord().Length >= 2)
                ShowCompletion();
        }
        else
        {
            _completionWindow?.Close();
        }
    }

    private void ShowCompletion(bool dotTriggered = false, bool forceAll = false)
    {
        _completionWindow?.Close();

        var offset = _editor.CaretOffset;
        var textBefore = _editor.Document.GetText(0, offset);
        var fullText = _editor.Document.Text;
        var word = dotTriggered ? "" : GetCurrentWord();
        var wordStart = offset - word.Length;

        var items = BuildCompletionList(textBefore, fullText, word, dotTriggered, forceAll);
        if (items.Count == 0) return;

        _completionWindow = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = wordStart,
            EndOffset = offset,
        };

        foreach (var item in items)
            _completionWindow.CompletionList.CompletionData.Add(item);

        if (!dotTriggered && word.Length > 0)
            _completionWindow.CompletionList.SelectItem(word);

        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private List<SqlCompletionData> BuildCompletionList(string textBefore, string fullText,
        string word, bool dotTriggered, bool forceAll)
    {
        var list = new List<SqlCompletionData>();

        // --- Dot-triggered ---
        if (dotTriggered)
        {
            // Find last dot position (just typed) and the token before it
            var lastDot = textBefore.Length - 1; // the dot just typed
            var prefixBefore = textBefore[..lastDot];

            // Check for second dot: could be "schema.table." → columns
            var prevDotIdx = prefixBefore.LastIndexOf('.');
            if (prevDotIdx >= 0)
            {
                // "schema.table." → try qualified lookup first
                var tablePart  = GetTrailingWord(prefixBefore);          // e.g. "Orders"
                var schemaPart = GetTrailingWord(prefixBefore[..prevDotIdx]); // e.g. "dbo"
                var qKey = $"{schemaPart}.{tablePart}";
                if (_schema.Columns.TryGetValue(qKey, out var qCols))
                {
                    list.AddRange(qCols.Select(c => new SqlCompletionData(c, CompletionKind.Column)));
                    return list;
                }
                // Also try unqualified table name
                if (_schema.Columns.TryGetValue(tablePart, out var tCols))
                {
                    list.AddRange(tCols.Select(c => new SqlCompletionData(c, CompletionKind.Column)));
                    return list;
                }
            }

            // Single-segment before dot: check if it's a schema name
            var nameBeforeDot = GetTrailingWord(prefixBefore);
            if (!string.IsNullOrEmpty(nameBeforeDot) &&
                _schema.SchemaToTables.TryGetValue(nameBeforeDot, out var schemaTables))
            {
                // "dbo." → suggest tables in that schema
                list.AddRange(schemaTables.Select(t =>
                    new SqlCompletionData(t, CompletionKind.Table, $"{nameBeforeDot}.{t}")));
                return list;
            }

            // Fallback: treat as table/alias → columns
            if (!string.IsNullOrEmpty(nameBeforeDot))
            {
                foreach (var col in ResolveColumns(fullText, nameBeforeDot))
                    list.Add(new SqlCompletionData(col, CompletionKind.Column));
            }
            return list;
        }

        var filter = word.ToUpperInvariant();

        // Context: cursor is right after FROM/JOIN → suggest tables
        var tableCtxMatch = Regex.Match(
            textBefore[..Math.Max(0, textBefore.Length - word.Length)].TrimEnd(),
            @"\b(FROM|JOIN|INTO|UPDATE|TABLE)\s*$", RegexOptions.IgnoreCase);
        bool isTableContext = tableCtxMatch.Success;

        // Columns from tables referenced anywhere in the full document
        var scopeTables = GetTablesInScope(fullText);
        foreach (var tbl in scopeTables)
        {
            if (!_schema.Columns.TryGetValue(tbl, out var cols)) continue;
            foreach (var col in cols)
                if (forceAll || col.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                    list.Add(new SqlCompletionData(col, CompletionKind.Column, $"Column of {tbl}"));
        }

        // Tables (active schema, unqualified)
        foreach (var t in _schema.Tables)
            if (forceAll || isTableContext || t.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                list.Add(new SqlCompletionData(t, CompletionKind.Table));

        // Schema names (so user can type "dbo" then "." to get table list)
        foreach (var s in _schema.Schemas)
            if (forceAll || s.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                list.Add(new SqlCompletionData(s, CompletionKind.Table, $"Schema: {s}"));

        // Keywords
        foreach (var kw in SqlKeywords)
            if (forceAll || kw.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                list.Add(new SqlCompletionData(kw, CompletionKind.Keyword));

        return list;
    }

    /// <summary>Extracts table names referenced in FROM/JOIN clauses of the query text.</summary>
    private List<string> GetTablesInScope(string text)
    {
        var result = new List<string>();
        foreach (Match m in FromTablePattern.Matches(text))
        {
            var raw = m.Groups[1].Value;
            // Try qualified key first (schema.table), then bare name
            if (_schema.Columns.ContainsKey(raw))
            {
                result.Add(raw);
                continue;
            }
            var tableName = raw.Contains('.') ? raw[(raw.LastIndexOf('.') + 1)..] : raw;
            tableName = tableName.Trim('[', ']', '"');
            if (_schema.Columns.ContainsKey(tableName))
                result.Add(tableName);
        }
        return result;
    }

    /// <summary>Resolves column list for a table name or its alias.</summary>
    private List<string> ResolveColumns(string text, string tableOrAlias)
    {
        if (_schema.Columns.TryGetValue(tableOrAlias, out var direct))
            return direct;

        var aliasRegex = new Regex(
            $@"\b([\w\.\[\]""]+)\s+(?:AS\s+)?{Regex.Escape(tableOrAlias)}\b",
            RegexOptions.IgnoreCase);
        var m = aliasRegex.Match(text);
        if (m.Success)
        {
            var raw = m.Groups[1].Value;
            // Try qualified key first, then bare name
            if (_schema.Columns.TryGetValue(raw, out var qCols))
                return qCols;
            var name = raw.Contains('.') ? raw[(raw.LastIndexOf('.') + 1)..] : raw;
            name = name.Trim('[', ']', '"');
            if (_schema.Columns.TryGetValue(name, out var aliasCols))
                return aliasCols;
        }

        return [];
    }

    private string GetCurrentWord()
    {
        var offset = _editor.CaretOffset;
        var doc = _editor.Document;
        var start = offset;
        while (start > 0 && IsWordChar(doc.GetCharAt(start - 1)))
            start--;
        return doc.GetText(start, offset - start);
    }

    private static string GetTrailingWord(string text)
    {
        var i = text.Length - 1;
        while (i >= 0 && IsWordChar(text[i])) i--;
        return text[(i + 1)..];
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
