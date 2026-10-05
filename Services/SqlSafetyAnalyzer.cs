using System.Text;
using System.Text.RegularExpressions;

namespace DataPortStudio.Services;

/// <summary>A statement in an ad-hoc script that can destroy data.</summary>
/// <param name="Kind">What makes it dangerous (no WHERE, TRUNCATE, DROP…).</param>
/// <param name="Statement">The statement text, trimmed for display.</param>
/// <param name="Target">The table it hits, when it could be parsed (used to count rows).</param>
public record DangerousStatement(DangerKind Kind, string Statement, string? Target);

public enum DangerKind { DeleteWithoutWhere, UpdateWithoutWhere, Truncate, Drop }

/// <summary>
/// Spots destructive statements in a script before it runs: UPDATE/DELETE with no top-level
/// WHERE, TRUNCATE and DROP. Comments and string literals are masked first, so keywords inside
/// them never count. Routine / trigger definitions (CREATE PROCEDURE …, DO $$ … $$) are skipped:
/// creating a routine doesn't run its body.
/// </summary>
public static class SqlSafetyAnalyzer
{
    // Creating a routine/trigger doesn't run its body — from here on the batch is one statement.
    private static readonly Regex RoutineHeader = new(
        @"^\s*(CREATE|ALTER|CREATE\s+OR\s+(REPLACE|ALTER))\s+(DEFINER\s*=\s*\S+\s+)?(PROCEDURE|PROC|FUNCTION|TRIGGER|PACKAGE|EVENT|RULE)\b|^\s*DO\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Ident = new(
        @"\G\s*((?:(?:\[[^\]]+\]|""[^""]+""|`[^`]+`|[\w$#@]+)\s*\.\s*)*(?:\[[^\]]+\]|""[^""]+""|`[^`]+`|[\w$#@]+))",
        RegexOptions.Compiled);

    /// <summary>Words that start a new statement — T-SQL lets statements run on without a semicolon.</summary>
    private static readonly HashSet<string> StatementStarts = new()
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "DROP", "CREATE", "ALTER", "EXEC", "EXECUTE",
        "MERGE", "BEGIN", "COMMIT", "ROLLBACK", "DECLARE", "PRINT", "GO",
    };

    /// <summary>A DELETE/UPDATE right after these is part of another clause (FOR UPDATE, ON DELETE CASCADE…).</summary>
    private static readonly HashSet<string> NotAVerbAfter = new()
    {
        "FOR", "ON", "DO", "KEY", "THEN", "OF", "GRANT", "REVOKE", "DENY", "INSERT", "SELECT", "AFTER", "BEFORE", "OR",
    };

    /// <summary>ALTER TABLE … DROP of these loses no data.</summary>
    private static readonly HashSet<string> HarmlessDrops = new()
    {
        "CONSTRAINT", "PRIMARY", "FOREIGN", "DEFAULT", "CHECK", "KEY", "INDEX",
    };

    public static List<DangerousStatement> Analyze(string sql)
    {
        var found = new List<DangerousStatement>();
        if (string.IsNullOrWhiteSpace(sql)) return found;

        var masked = Mask(sql);
        foreach (var (start, length) in SplitStatements(masked))
        {
            var m = masked.Substring(start, length);
            if (string.IsNullOrWhiteSpace(m)) continue;
            if (RoutineHeader.IsMatch(m)) break;

            var words = TopLevelWords(m);
            for (int i = 0; i < words.Count; i++)
            {
                var (verb, pos) = words[i];
                if (verb is not ("DELETE" or "UPDATE" or "TRUNCATE" or "DROP")) continue;
                var prev = i > 0 ? words[i - 1].Word : "";
                var next = i + 1 < words.Count ? words[i + 1].Word : "";
                if (NotAVerbAfter.Contains(prev) || next is "ON" or "TO") continue;

                // The statement runs until the next statement keyword (or the semicolon).
                int end = i + 1;
                while (end < words.Count && !StatementStarts.Contains(words[end].Word)) end++;
                var segment = words.Skip(i + 1).Take(end - i - 1).Select(w => w.Word).ToList();
                var segEnd = end < words.Count ? words[end].Pos : m.Length;
                var display = Shorten(sql.Substring(start + pos, segEnd - pos));
                var hasWhere = segment.Contains("WHERE");
                var joined = segment.Contains("JOIN");

                switch (verb)
                {
                    case "DELETE" when !hasWhere:
                    {
                        // DELETE [FROM] target — skip the count when another FROM/JOIN reshapes the rows.
                        var after = next == "FROM" ? words[i + 1].Pos + 4 : pos + 6;
                        var froms = segment.Count(w => w == "FROM");
                        var countable = !joined && froms <= (next == "FROM" ? 1 : 0);
                        found.Add(new(DangerKind.DeleteWithoutWhere, display,
                            countable ? ReadIdent(sql, start, m, after) : null));
                        break;
                    }
                    case "UPDATE" when !hasWhere:
                    {
                        var after = next is "ONLY" or "LOW_PRIORITY" or "IGNORE" ? words[i + 1].Pos + next.Length : pos + 6;
                        var countable = !joined && !segment.Contains("FROM");
                        found.Add(new(DangerKind.UpdateWithoutWhere, display,
                            countable ? ReadIdent(sql, start, m, after) : null));
                        break;
                    }
                    case "TRUNCATE":
                    {
                        var after = next == "TABLE" ? words[i + 1].Pos + 5 : pos + 8;
                        found.Add(new(DangerKind.Truncate, display, ReadIdent(sql, start, m, after)));
                        break;
                    }
                    case "DROP" when !HarmlessDrops.Contains(next):
                        found.Add(new(DangerKind.Drop, display, null));
                        break;
                }
                i = end - 1;
            }
        }
        return found;
    }

    /// <summary>Replaces comments and quoted literals with spaces, keeping offsets intact.</summary>
    internal static string Mask(string sql)
    {
        var sb = new StringBuilder(sql);
        int i = 0, n = sql.Length;
        while (i < n)
        {
            char c = sql[i];
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') sb[i++] = ' ';
            }
            else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                for (; i < end; i++) if (sql[i] != '\n') sb[i] = ' ';
            }
            else if (c == '\'')
            {
                sb[i++] = ' ';
                while (i < n)
                {
                    if (sql[i] == '\'' && i + 1 < n && sql[i + 1] == '\'') { sb[i] = sb[i + 1] = ' '; i += 2; continue; }
                    if (sql[i] == '\'') { sb[i++] = ' '; break; }
                    if (sql[i] != '\n') sb[i] = ' ';
                    i++;
                }
            }
            else if (c == '$' && TryDollarTag(sql, i, out var tag))
            {
                // PostgreSQL $tag$ … $tag$ body
                int end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                end = end < 0 ? n : end + tag.Length;
                for (; i < end; i++) if (sql[i] != '\n') sb[i] = ' ';
            }
            else i++;
        }
        return sb.ToString();
    }

    private static bool TryDollarTag(string sql, int i, out string tag)
    {
        tag = "";
        int j = i + 1;
        while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
        if (j < sql.Length && sql[j] == '$' && (j == i + 1 || !char.IsDigit(sql[i + 1])))
        {
            tag = sql.Substring(i, j - i + 1);
            return true;
        }
        return false;
    }

    /// <summary>Splits on top-level semicolons and SQL Server <c>GO</c> lines.</summary>
    private static IEnumerable<(int Start, int Length)> SplitStatements(string masked)
    {
        int start = 0, depth = 0;
        for (int i = 0; i < masked.Length; i++)
        {
            char c = masked[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ';' && depth == 0)
            {
                yield return (start, i - start);
                start = i + 1;
            }
        }
        if (start < masked.Length) yield return (start, masked.Length - start);
    }

    /// <summary>Upper-cased words outside parentheses, with their offsets.</summary>
    private static List<(string Word, int Pos)> TopLevelWords(string masked)
    {
        var list = new List<(string, int)>();
        int depth = 0, i = 0;
        while (i < masked.Length)
        {
            char c = masked[i];
            if (c == '(') { depth++; i++; }
            else if (c == ')') { depth = Math.Max(0, depth - 1); i++; }
            else if (c == '[' || c == '"' || c == '`')
            {
                // quoted identifier — never a keyword
                char close = c == '[' ? ']' : c;
                int end = masked.IndexOf(close, i + 1);
                i = end < 0 ? masked.Length : end + 1;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < masked.Length && (char.IsLetterOrDigit(masked[i]) || masked[i] is '_' or '$' or '#' or '@')) i++;
                if (depth == 0) list.Add((masked[s..i].ToUpperInvariant(), s));
            }
            else i++;
        }
        return list;
    }

    private static string? ReadIdent(string sql, int stmtStart, string masked, int offset)
    {
        var m = Ident.Match(masked, Math.Min(offset, masked.Length));
        if (!m.Success) return null;
        var g = m.Groups[1];
        var text = sql.Substring(stmtStart + g.Index, g.Length);
        return text.ToUpperInvariant() is "SET" or "FROM" or "WHERE" ? null : text;
    }

    private static string Shorten(string statement)
    {
        var s = Regex.Replace(statement.Trim(), @"\s+", " ");
        return s.Length > 140 ? s[..137] + "…" : s;
    }
}
