using System.Text;

namespace DataPortStudio.Services;

/// <summary>
/// Simple but practical SQL formatter. Uppercases keywords, indents clauses and subqueries.
/// Wraps the original text on any parse error so the editor never gets corrupted.
/// </summary>
public static class SqlBeautifier
{
    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        try   { return Reconstruct(Tokenize(sql.Trim())); }
        catch { return sql; }
    }

    // ── Token types ──────────────────────────────────────────────────────────

    private enum K { Word, QuotedId, String, LineComment, BlockComment, Comma, Semi, Open, Close, Dot, Op }

    private readonly record struct Tok(K Kind, string Text);

    // ── Tokenizer ────────────────────────────────────────────────────────────

    private static List<Tok> Tokenize(string sql)
    {
        var list = new List<Tok>();
        int i = 0, n = sql.Length;

        while (i < n)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // -- line comment
            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                int e = sql.IndexOf('\n', i); if (e < 0) e = n;
                list.Add(new(K.LineComment, sql[i..e].TrimEnd())); i = e; continue;
            }
            // /* block comment */
            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                int e = sql.IndexOf("*/", i + 2); e = e < 0 ? n : e + 2;
                list.Add(new(K.BlockComment, sql[i..e])); i = e; continue;
            }
            // N'…' or '…' string
            if (c == '\'' || (c is 'N' or 'n' && Peek(sql, i + 1) == '\''))
            {
                var sb = new StringBuilder();
                if (c is 'N' or 'n') { sb.Append('N'); i++; }
                sb.Append('\''); i++;
                while (i < n)
                {
                    if (sql[i] == '\'' && Peek(sql, i + 1) == '\'') { sb.Append("''"); i += 2; }
                    else if (sql[i] == '\'') { sb.Append('\''); i++; break; }
                    else sb.Append(sql[i++]);
                }
                list.Add(new(K.String, sb.ToString())); continue;
            }
            // [bracket] or "double" quoted identifier
            if (c == '[') { int e = sql.IndexOf(']', i + 1); e = e < 0 ? n - 1 : e; list.Add(new(K.QuotedId, sql[i..(e + 1)])); i = e + 1; continue; }
            if (c == '"') { int e = sql.IndexOf('"', i + 1); e = e < 0 ? n - 1 : e; list.Add(new(K.QuotedId, sql[i..(e + 1)])); i = e + 1; continue; }
            // `backtick` (MySQL)
            if (c == '`') { int e = sql.IndexOf('`', i + 1); e = e < 0 ? n - 1 : e; list.Add(new(K.QuotedId, sql[i..(e + 1)])); i = e + 1; continue; }

            // word / keyword
            if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var sb = new StringBuilder();
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$'))
                    sb.Append(sql[i++]);
                list.Add(new(K.Word, sb.ToString())); continue;
            }
            // number
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(sql[i + 1])))
            {
                var sb = new StringBuilder();
                while (i < n && (char.IsDigit(sql[i]) || sql[i] is '.' or 'e' or 'E'))
                    sb.Append(sql[i++]);
                if (i < n && sql[i] is '+' or '-' && sb[^1] is 'e' or 'E') sb.Append(sql[i++]);
                list.Add(new(K.Op, sb.ToString())); continue;
            }

            switch (c)
            {
                case ',': list.Add(new(K.Comma, ",")); i++; break;
                case ';': list.Add(new(K.Semi, ";")); i++; break;
                case '(': list.Add(new(K.Open, "(")); i++; break;
                case ')': list.Add(new(K.Close, ")")); i++; break;
                case '.': list.Add(new(K.Dot, ".")); i++; break;
                default:
                    var op = new StringBuilder();
                    while (i < n && "=<>!+-*/%&|^~:".Contains(sql[i])) op.Append(sql[i++]);
                    if (op.Length > 0) list.Add(new(K.Op, op.ToString()));
                    else { list.Add(new(K.Op, c.ToString())); i++; }
                    break;
            }
        }
        return list;
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    // ── Keyword classification ────────────────────────────────────────────────

    private static readonly HashSet<string> _all = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","AND","OR","NOT","IN","EXISTS","BETWEEN","LIKE","IS","NULL",
        "AS","ON","INNER","LEFT","RIGHT","FULL","OUTER","CROSS","JOIN","UNION","ALL",
        "DISTINCT","TOP","GROUP","BY","ORDER","HAVING","LIMIT","OFFSET","FETCH","NEXT",
        "WITH","INSERT","INTO","VALUES","UPDATE","SET","DELETE","MERGE","OUTPUT",
        "CREATE","TABLE","VIEW","INDEX","PROCEDURE","FUNCTION","TRIGGER","DATABASE","SCHEMA",
        "ALTER","DROP","TRUNCATE","WHEN","THEN","ELSE","END","CASE","OVER","PARTITION",
        "ROWS","RANGE","BETWEEN","FOLLOWING","PRECEDING","CURRENT","ROW","UNBOUNDED",
        "BEGIN","COMMIT","ROLLBACK","SAVEPOINT","DECLARE","EXEC","EXECUTE","CALL","GO",
        "IF","WHILE","RETURN","PRINT","RAISE","RAISERROR","THROW","TRY","CATCH",
        "PRIMARY","KEY","FOREIGN","REFERENCES","UNIQUE","CONSTRAINT","DEFAULT","CHECK",
        "IDENTITY","SERIAL","AUTO_INCREMENT","ASC","DESC","NULLS","FIRST","LAST",
        "CAST","CONVERT","COALESCE","NULLIF","IIF","ISNULL","IFNULL","NVL",
        "COUNT","SUM","AVG","MIN","MAX","ROWNUM","ROWID",
        "INSERTED","DELETED","NEW","OLD","NOLOCK","READPAST","UPDLOCK","TABLOCK",
        "RETURNS","LANGUAGE","VOLATILE","STRICT","SECURITY","DEFINER","INVOKER",
        "REPLACE","IGNORE","DELAYED","LOW_PRIORITY","HIGH_PRIORITY",
        "SHOW","DESCRIBE","EXPLAIN","ANALYZE","VACUUM",
        "OUTER","APPLY","CROSS","PIVOT","UNPIVOT",
        "EXCEPT","INTERSECT","MINUS",
    };

    // Tokens that start a new top-level line (reset to current indent)
    private static readonly HashSet<string> _clauseStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","HAVING","LIMIT","OFFSET","FETCH",
        "UNION","EXCEPT","INTERSECT","MINUS",
        "INSERT","UPDATE","MERGE","DELETE",
        "VALUES",
        "CREATE","ALTER","DROP","TRUNCATE",
        "WITH","BEGIN","COMMIT","ROLLBACK","SAVEPOINT",
        "DECLARE","EXEC","EXECUTE","CALL","GO","PRINT",
        "IF","ELSE","WHILE","RETURN","THROW","RAISE","RAISERROR",
        "SHOW","DESCRIBE","EXPLAIN","ANALYZE","VACUUM",
        "REPLACE",
    };

    // These get indented by one extra level
    private static readonly HashSet<string> _clauseIndent = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND","OR",
    };

    private static readonly HashSet<string> _joinKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "JOIN","INNER","LEFT","RIGHT","FULL","CROSS","OUTER","APPLY",
    };

    private static bool IsClauseStart(string w, List<Tok> toks, int i) =>
        _clauseStart.Contains(w) ||
        (_joinKeywords.Contains(w) && i + 1 < toks.Count && (toks[i + 1].Text.Equals("JOIN", StringComparison.OrdinalIgnoreCase) || toks[i + 1].Text.Equals("APPLY", StringComparison.OrdinalIgnoreCase) || IsJoin(w))) ||
        IsJoin(w);

    private static bool IsJoin(string w) => w.Equals("JOIN", StringComparison.OrdinalIgnoreCase);

    // Reconstruct with indentation
    private static string Reconstruct(List<Tok> toks)
    {
        var sb = new StringBuilder();
        int indent = 0;
        bool atLineStart = true;
        // track whether we're inside a subquery paren to know if commas = new line
        var parenSelectDepth = new Stack<bool>(); // true = paren contains SELECT

        void NewLine() { sb.Append('\n'); sb.Append(Ind(indent)); atLineStart = true; }
        void Append(string s) { sb.Append(s); atLineStart = false; }
        void Space() { if (!atLineStart) sb.Append(' '); }

        int n = toks.Count;
        for (int i = 0; i < n; i++)
        {
            var (kind, text) = toks[i];

            if (kind is K.LineComment)
            {
                if (!atLineStart) NewLine();
                Append(text); NewLine(); continue;
            }
            if (kind is K.BlockComment)
            {
                if (!atLineStart) NewLine();
                Append(text); NewLine(); continue;
            }

            if (kind is K.Word)
            {
                bool kw = _all.Contains(text);
                string upper = kw ? text.ToUpperInvariant() : text;

                // Merge multi-word clauses: GROUP BY, ORDER BY, PARTITION BY, etc.
                if (kw && i + 2 < n && toks[i + 1].Kind == K.Word &&
                    toks[i + 1].Text.Equals("BY", StringComparison.OrdinalIgnoreCase) &&
                    text.Equals("GROUP", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("ORDER", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("PARTITION", StringComparison.OrdinalIgnoreCase))
                {
                    if (!atLineStart) NewLine();
                    Append(upper + " BY"); i++; // consume BY
                    atLineStart = false; continue;
                }
                if (kw && i + 1 < n && toks[i + 1].Kind == K.Word &&
                    toks[i + 1].Text.Equals("BY", StringComparison.OrdinalIgnoreCase) &&
                    (text.Equals("GROUP", StringComparison.OrdinalIgnoreCase) ||
                     text.Equals("ORDER", StringComparison.OrdinalIgnoreCase) ||
                     text.Equals("PARTITION", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!atLineStart) NewLine();
                    Append(upper + " BY"); i++;
                    atLineStart = false; continue;
                }

                // SET inside UPDATE is a clause start
                if (kw && text.Equals("SET", StringComparison.OrdinalIgnoreCase) && indent == 0)
                {
                    if (!atLineStart) NewLine();
                    Append(upper); atLineStart = false; continue;
                }

                // ON after JOIN stays on same line as JOIN
                if (kw && text.Equals("ON", StringComparison.OrdinalIgnoreCase) && !atLineStart)
                {
                    NewLine(); indent++; Append(upper); indent--; atLineStart = false; continue;
                }

                if (kw && _clauseStart.Contains(text))
                {
                    if (!atLineStart) NewLine();
                    Append(upper); atLineStart = false; continue;
                }
                if (kw && IsJoin(text))
                {
                    if (!atLineStart) NewLine();
                    Append(upper); atLineStart = false; continue;
                }
                if (kw && _joinKeywords.Contains(text))
                {
                    // INNER/LEFT/RIGHT/FULL/CROSS/OUTER before JOIN
                    if (!atLineStart) NewLine();
                    Append(upper); atLineStart = false; continue;
                }
                if (kw && _clauseIndent.Contains(text))
                {
                    if (!atLineStart) { NewLine(); }
                    Append(Ind(1) + upper); atLineStart = false; continue;
                }
                // END closes a BEGIN block
                if (kw && text.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    indent = Math.Max(0, indent - 1);
                    if (!atLineStart) NewLine();
                    Append(upper); atLineStart = false; continue;
                }
                if (kw && text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    if (!atLineStart) NewLine();
                    Append(upper); indent++; atLineStart = false; continue;
                }

                Space(); Append(upper); continue;
            }

            if (kind is K.Open)
            {
                // Peek ahead: is there a SELECT inside this paren level?
                bool hasSel = PeekForSelect(toks, i + 1);
                parenSelectDepth.Push(hasSel);
                if (hasSel) { Space(); Append("("); indent++; NewLine(); }
                else { Space(); Append("("); atLineStart = false; }
                continue;
            }
            if (kind is K.Close)
            {
                bool wasSel = parenSelectDepth.Count > 0 && parenSelectDepth.Pop();
                if (wasSel) { indent = Math.Max(0, indent - 1); NewLine(); Append(")"); }
                else Append(")");
                atLineStart = false; continue;
            }
            if (kind is K.Comma)
            {
                Append(",");
                // inside a subquery paren: new line after comma
                bool inSel = parenSelectDepth.Count > 0 && parenSelectDepth.Peek();
                if (inSel) NewLine();
                else { sb.Append(' '); atLineStart = false; }
                continue;
            }
            if (kind is K.Semi)
            {
                Append(";"); NewLine(); continue;
            }
            if (kind is K.Dot)
            {
                Append("."); atLineStart = false; continue;
            }
            if (kind is K.String or K.QuotedId)
            {
                Space(); Append(text); continue;
            }
            // Op / number / other
            if (kind is K.Op)
            {
                // Operators: surround with spaces unless it's unary minus right after open paren or operator
                bool unary = text is "-" or "+" && (i == 0 || toks[i - 1].Kind is K.Open or K.Op or K.Comma);
                if (unary) { Space(); Append(text); }
                else { Space(); Append(text); sb.Append(' '); atLineStart = false; }
                continue;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool PeekForSelect(List<Tok> toks, int start)
    {
        int depth = 0;
        for (int i = start; i < toks.Count; i++)
        {
            if (toks[i].Kind is K.Open) depth++;
            else if (toks[i].Kind is K.Close) { if (depth == 0) return false; depth--; }
            else if (depth == 0 && toks[i].Kind is K.Word &&
                     toks[i].Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Ind(int extra) => new(' ', extra * 4);
}
