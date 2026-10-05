using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>One side of a data comparison: a table at a database endpoint.</summary>
public sealed record DataEndpoint(SchemaEndpoint Location, string Table)
{
    public ConnectionProfile Connection => Location.Connection;
    public DatabaseEngine Engine => Location.Connection.Engine;
    public string DisplayName => $"{Location.DisplayName} / {Table}";
}

/// <summary>A row present on both sides whose non-key values differ.</summary>
public sealed record DataRowDiff(object?[] Source, object?[] Target, int[] Changed);

public sealed class DataCompareOptions
{
    public bool IgnoreTrailingSpaces { get; init; } = true;
    public bool IgnoreCase { get; init; }
}

/// <summary>What the comparison found. Value arrays follow <see cref="Columns"/>.</summary>
public sealed class DataCompareResult
{
    public required DataEndpoint Source { get; init; }
    public required DataEndpoint Target { get; init; }
    /// <summary>Columns present on both sides, in source order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }
    /// <summary>Indexes into <see cref="Columns"/> that identify a row.</summary>
    public required IReadOnlyList<int> KeyIndexes { get; init; }
    /// <summary>Column names that exist on one side only (not compared).</summary>
    public required IReadOnlyList<string> SkippedColumns { get; init; }
    /// <summary>Target CLR type per column (drives literals and parameter conversion).</summary>
    public required IReadOnlyList<Type> TargetTypes { get; init; }
    /// <summary>Target columns that can't be written (SQL Server computed / rowversion).</summary>
    public required IReadOnlySet<int> ReadOnlyTargetColumns { get; init; }
    /// <summary>Target columns that are SQL Server IDENTITY (inserts need IDENTITY_INSERT).</summary>
    public required IReadOnlySet<int> IdentityTargetColumns { get; init; }

    public List<object?[]> OnlyInSource { get; } = [];
    public List<object?[]> OnlyInTarget { get; } = [];
    public List<DataRowDiff> Different { get; } = [];
    public long Identical { get; set; }

    public bool IsInSync => OnlyInSource.Count == 0 && OnlyInTarget.Count == 0 && Different.Count == 0;
}

/// <summary>Which changes a sync writes to the target.</summary>
public sealed record DataSyncOptions(bool Insert, bool Update, bool Delete);

/// <summary>
/// Compares the rows of two tables — on the same engine or on different ones — by primary key,
/// and makes the target match the source, either as a SQL script or by applying the changes in
/// one transaction. Values are normalised before comparing (numbers by value, dates to ISO text,
/// booleans to 1/0…) so that the same data held by two engines compares equal.
/// </summary>
public static class DataCompareService
{
    private const char KeySep = '\u001F';

    public static bool IsSupported(ConnectionProfile p) => TableCopyService.IsRelational(p.Engine);

    // ── Compare ─────────────────────────────────────────────────────────────

    public static async Task<DataCompareResult> CompareAsync(
        DataEndpoint source, DataEndpoint target, DataCompareOptions options,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Reading table structure…");
        var srcCols = await DescribeAsync(source, ct);
        var tgtCols = await DescribeAsync(target, ct);
        var tgtByName = tgtCols.Select((c, i) => (c, i))
            .ToDictionary(x => x.c.Name, x => x.i, StringComparer.OrdinalIgnoreCase);

        var columns = srcCols.Where(c => tgtByName.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        if (columns.Count == 0)
            throw new InvalidOperationException("The two tables have no column names in common.");
        var skipped = srcCols.Select(c => c.Name).Where(n => !tgtByName.ContainsKey(n))
            .Concat(tgtCols.Select(c => c.Name).Where(n => !columns.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var keys = await KeyColumnsAsync(source, target, columns);
        var keyIndexes = keys.Select(k => columns.FindIndex(c => c.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        var targetTypes = columns.Select(c => tgtCols[tgtByName[c]].Type).ToList();
        var (readOnly, identity) = await TargetColumnTraitsAsync(target, columns, ct);

        var result = new DataCompareResult
        {
            Source = source, Target = target, Columns = columns, KeyIndexes = keyIndexes,
            SkippedColumns = skipped, TargetTypes = targetTypes,
            ReadOnlyTargetColumns = readOnly, IdentityTargetColumns = identity,
        };

        // 1. Hold the target in memory, keyed — the source then streams past it once.
        progress?.Report($"Reading {target.Table} (target)…");
        var targetRows = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        long read = 0;
        await foreach (var row in ReadRowsAsync(target, columns, ct))
        {
            targetRows.TryAdd(KeyOf(row, keyIndexes, options), row);
            if (++read % 50_000 == 0) progress?.Report($"Reading {target.Table} (target)… {read:N0} rows");
        }

        // 2. Stream the source and match each row.
        progress?.Report($"Comparing with {source.Table} (source)…");
        read = 0;
        var nonKey = Enumerable.Range(0, columns.Count).Except(keyIndexes).ToArray();
        await foreach (var row in ReadRowsAsync(source, columns, ct))
        {
            var key = KeyOf(row, keyIndexes, options);
            if (targetRows.Remove(key, out var other))
            {
                var changed = nonKey.Where(i => Normalize(row[i], options) != Normalize(other[i], options)).ToArray();
                if (changed.Length == 0) result.Identical++;
                else result.Different.Add(new DataRowDiff(row, other, changed));
            }
            else result.OnlyInSource.Add(row);
            if (++read % 50_000 == 0) progress?.Report($"Comparing… {read:N0} rows");
        }
        result.OnlyInTarget.AddRange(targetRows.Values);
        return result;
    }

    private sealed record Col(string Name, Type Type);

    private static async Task<List<Col>> DescribeAsync(DataEndpoint ep, CancellationToken ct)
    {
        await using var conn = await TableCopyService.OpenAsync(ep.Connection, ep.Location.Database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Fq(ep)}";
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, ct);
        return Enumerable.Range(0, reader.FieldCount)
            .Select(i => new Col(reader.GetName(i), SafeFieldType(reader, i)))
            .ToList();
    }

    private static Type SafeFieldType(DbDataReader reader, int i)
    {
        try { return reader.GetFieldType(i); } catch { return typeof(string); }
    }

    private static async Task<List<string>> KeyColumnsAsync(DataEndpoint source, DataEndpoint target, List<string> columns)
    {
        foreach (var ep in new[] { source, target })
        {
            List<string> pk;
            try
            {
                pk = await TableCopyService.GetPrimaryKeyNamesAsync(
                    ep.Connection, ep.Location.Database, ep.Location.Schema, ep.Table);
            }
            catch { continue; }
            if (pk.Count > 0 && pk.All(k => columns.Contains(k, StringComparer.OrdinalIgnoreCase)))
                return pk;
        }
        throw new InvalidOperationException(
            "Neither table has a primary key that both sides share, so rows can't be matched up. " +
            "Add a primary key to the source or target table and compare again.");
    }

    /// <summary>SQL Server: computed / rowversion columns can't be written; IDENTITY needs IDENTITY_INSERT.</summary>
    private static async Task<(HashSet<int> ReadOnly, HashSet<int> Identity)> TargetColumnTraitsAsync(
        DataEndpoint target, List<string> columns, CancellationToken ct)
    {
        var readOnly = new HashSet<int>();
        var identity = new HashSet<int>();
        if (target.Engine != DatabaseEngine.SqlServer) return (readOnly, identity);

        await using var conn = await TableCopyService.OpenAsync(target.Connection, target.Location.Database);
        await using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.name, c.is_identity, c.is_computed, t.name
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@t)
            """;
        cmd.Parameters.AddWithValue("@t", Fq(target));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var i = columns.FindIndex(c => c.Equals(r.GetString(0), StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            if (r.GetBoolean(2) || r.GetString(3) is "timestamp" or "rowversion") readOnly.Add(i);
            else if (r.GetBoolean(1)) identity.Add(i);
        }
        return (readOnly, identity);
    }

    private static async IAsyncEnumerable<object?[]> ReadRowsAsync(
        DataEndpoint ep, List<string> columns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await TableCopyService.OpenAsync(ep.Connection, ep.Location.Database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(c => Q(ep.Engine, c)))} FROM {Fq(ep)}";
        try { cmd.CommandTimeout = 0; } catch { }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < row.Length; i++)
            {
                try { row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i); }
                catch { row[i] = null; } // e.g. an Oracle date outside .NET's range
            }
            yield return row;
        }
    }

    private static string KeyOf(object?[] row, IReadOnlyList<int> keyIndexes, DataCompareOptions o)
    {
        if (keyIndexes.Count == 1) return Normalize(row[keyIndexes[0]], o);
        var sb = new StringBuilder();
        foreach (var i in keyIndexes) sb.Append(Normalize(row[i], o)).Append(KeySep);
        return sb.ToString();
    }

    /// <summary>A canonical text form, so equal values from different engines/drivers compare equal.</summary>
    internal static string Normalize(object? v, DataCompareOptions o)
    {
        switch (v)
        {
            case null or DBNull: return "\0NULL";
            case bool b: return b ? "1" : "0";
            case string s:
                if (o.IgnoreTrailingSpaces) s = s.TrimEnd(' ');
                return o.IgnoreCase ? s.ToUpperInvariant() : s;
            case DateTime dt: return dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
            case DateTimeOffset dto: return dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') + "Z";
            case DateOnly d: return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00";
            case TimeOnly t: return t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
            case TimeSpan ts: return ts.ToString("c", CultureInfo.InvariantCulture);
            case byte[] bytes: return "0x" + Convert.ToHexString(bytes);
            case Guid g: return g.ToString("D");
            case decimal m: return NormalizeDecimal(m);
            case double or float:
            {
                var dbl = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsFinite(dbl) && Math.Abs(dbl) < 7.9e27)
                    try { return NormalizeDecimal((decimal)dbl); } catch { }
                return dbl.ToString("R", CultureInfo.InvariantCulture);
            }
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(v, CultureInfo.InvariantCulture)!;
            default:
                var text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                return o.IgnoreCase ? text.ToUpperInvariant() : text;
        }
    }

    private static string NormalizeDecimal(decimal m)
    {
        var s = m.ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s == "-0" ? "0" : s;
    }

    // ── Script ──────────────────────────────────────────────────────────────

    public static string BuildScript(DataCompareResult r, DataSyncOptions sync)
    {
        var e = r.Target.Engine;
        var fq = Fq(r.Target);
        var sb = new StringBuilder();
        sb.AppendLine($"-- Data sync script generated by DataPortStudio on {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"-- Source: {r.Source.DisplayName}");
        sb.AppendLine($"-- Target: {r.Target.DisplayName}");
        sb.AppendLine($"-- {Count(r, sync)} change(s): " +
                      $"{(sync.Delete ? r.OnlyInTarget.Count : 0):N0} delete, " +
                      $"{(sync.Update ? r.Different.Count : 0):N0} update, " +
                      $"{(sync.Insert ? r.OnlyInSource.Count : 0):N0} insert");
        sb.AppendLine();
        sb.AppendLine(e switch
        {
            DatabaseEngine.SqlServer => "SET XACT_ABORT ON;\nBEGIN TRANSACTION;",
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => "START TRANSACTION;",
            DatabaseEngine.Sqlite => "BEGIN TRANSACTION;",
            DatabaseEngine.PostgreSql => "BEGIN;",
            _ => "-- (runs in the current transaction; COMMIT at the end)",
        });
        sb.AppendLine();

        string Where(object?[] row) => string.Join(" AND ",
            r.KeyIndexes.Select(i => $"{Q(e, r.Columns[i])} = {Literal(row[i], e, r.TargetTypes[i])}"));

        if (sync.Delete)
            foreach (var row in r.OnlyInTarget)
                sb.AppendLine($"DELETE FROM {fq} WHERE {Where(row)};");

        if (sync.Update)
            foreach (var d in r.Different)
            {
                var sets = d.Changed.Where(i => Writable(r, i, forInsert: false)).ToList();
                if (sets.Count == 0) continue;
                sb.AppendLine($"UPDATE {fq} SET " +
                              string.Join(", ", sets.Select(i => $"{Q(e, r.Columns[i])} = {Literal(d.Source[i], e, r.TargetTypes[i])}")) +
                              $" WHERE {Where(d.Source)};");
            }

        if (sync.Insert && r.OnlyInSource.Count > 0)
        {
            var cols = InsertColumns(r);
            var identityInsert = e == DatabaseEngine.SqlServer && cols.Any(r.IdentityTargetColumns.Contains);
            if (identityInsert) sb.AppendLine($"SET IDENTITY_INSERT {fq} ON;");
            var colList = string.Join(", ", cols.Select(i => Q(e, r.Columns[i])));
            foreach (var row in r.OnlyInSource)
                sb.AppendLine($"INSERT INTO {fq} ({colList}) VALUES (" +
                              string.Join(", ", cols.Select(i => Literal(row[i], e, r.TargetTypes[i]))) + ");");
            if (identityInsert) sb.AppendLine($"SET IDENTITY_INSERT {fq} OFF;");
        }

        sb.AppendLine();
        sb.AppendLine("COMMIT;");
        return sb.ToString();
    }

    public static int Count(DataCompareResult r, DataSyncOptions sync) =>
        (sync.Delete ? r.OnlyInTarget.Count : 0) +
        (sync.Update ? r.Different.Count : 0) +
        (sync.Insert ? r.OnlyInSource.Count : 0);

    private static bool Writable(DataCompareResult r, int i, bool forInsert) =>
        !r.ReadOnlyTargetColumns.Contains(i) &&
        (forInsert || (!r.IdentityTargetColumns.Contains(i) && !r.KeyIndexes.Contains(i)));

    private static List<int> InsertColumns(DataCompareResult r) =>
        Enumerable.Range(0, r.Columns.Count).Where(i => Writable(r, i, forInsert: true)).ToList();

    /// <summary>A SQL literal for <paramref name="v"/> in the target engine's dialect.</summary>
    internal static string Literal(object? v, DatabaseEngine e, Type targetType)
    {
        v = ConvertForTarget(v, targetType);
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var inv = CultureInfo.InvariantCulture;
        switch (v)
        {
            case null or DBNull: return "NULL";
            case bool b:
                return e is DatabaseEngine.PostgreSql or DatabaseEngine.Firebird ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0");
            case string s:
                if (e.IsMySql()) s = s.Replace("\\", "\\\\");
                return (e == DatabaseEngine.SqlServer ? "N" : "") + Quote(s);
            case DateTime dt:
                return e switch
                {
                    DatabaseEngine.SqlServer => Quote(dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", inv)),
                    DatabaseEngine.Oracle => "TIMESTAMP " + Quote(dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", inv)),
                    DatabaseEngine.Firebird => Quote(dt.ToString("yyyy-MM-dd HH:mm:ss.ffff", inv)),
                    _ => Quote(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", inv).TrimEnd('.')),
                };
            case DateTimeOffset dto: return Quote(dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", inv));
            case DateOnly d: return (e == DatabaseEngine.Oracle ? "DATE " : "") + Quote(d.ToString("yyyy-MM-dd", inv));
            case TimeOnly t: return Quote(t.ToString("HH:mm:ss.FFFFFFF", inv).TrimEnd('.'));
            case TimeSpan ts: return Quote(ts.ToString(@"hh\:mm\:ss\.fffffff", inv));
            case Guid g: return Quote(g.ToString("D"));
            case byte[] bytes:
                var hex = Convert.ToHexString(bytes);
                return e switch
                {
                    DatabaseEngine.SqlServer => "0x" + hex,
                    DatabaseEngine.PostgreSql => $"'\\x{hex}'::bytea",
                    DatabaseEngine.Oracle => $"HEXTORAW('{hex}')",
                    _ => $"X'{hex}'",
                };
            case double dbl: return dbl.ToString("R", inv);
            case float f: return f.ToString("R", inv);
            case IFormattable num when v.GetType().IsPrimitive || v is decimal: return num.ToString(null, inv);
            default: return Quote(Convert.ToString(v, inv) ?? "");
        }
    }

    /// <summary>Bridges engine type gaps: 1/0 into a boolean column, true/false into a numeric one.</summary>
    private static object? ConvertForTarget(object? v, Type targetType)
    {
        if (v is null or DBNull) return null;
        try
        {
            if (targetType == typeof(bool) && v is not bool && IsNumeric(v)) return Convert.ToDecimal(v) != 0;
            if (v is bool b && IsNumericType(targetType)) return b ? 1 : 0;
            if (targetType == typeof(string) && v is not string && v is not byte[]) return Normalize(v, new DataCompareOptions { IgnoreTrailingSpaces = false });
        }
        catch { /* leave as-is */ }
        return v;
    }

    private static bool IsNumeric(object v) => IsNumericType(v.GetType());

    private static bool IsNumericType(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) ||
        t == typeof(uint) || t == typeof(long) || t == typeof(ulong) || t == typeof(decimal) ||
        t == typeof(double) || t == typeof(float);

    // ── Apply ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the selected changes to the target with parameterised statements in one transaction —
    /// all of them land, or none do.
    /// </summary>
    public static async Task<int> ApplyAsync(DataCompareResult r, DataSyncOptions sync,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var e = r.Target.Engine;
        var fq = Fq(r.Target);
        var oracle = e == DatabaseEngine.Oracle;
        var total = Count(r, sync);
        var done = 0;

        await using var conn = await TableCopyService.OpenAsync(r.Target.Connection, r.Target.Location.Database);
        await using var tx = await conn.BeginTransactionAsync(ct);

        DbCommand NewCommand(string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            try { cmd.CommandTimeout = 0; } catch { }
            if (cmd is Oracle.ManagedDataAccess.Client.OracleCommand oc) oc.BindByName = true;
            return cmd;
        }
        string P(int n) => (oracle ? ":p" : "@p") + n;
        void Bind(DbCommand cmd, int n, object? value, int column)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = (oracle ? "p" : "@p") + n;
            p.Value = ConvertForTarget(value, r.TargetTypes[column]) ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        void Tick()
        {
            if (++done % 500 == 0) progress?.Report($"Applying… {done:N0} of {total:N0}");
        }

        try
        {
            if (sync.Delete)
                foreach (var row in r.OnlyInTarget)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var cmd = NewCommand($"DELETE FROM {fq} WHERE " +
                        string.Join(" AND ", r.KeyIndexes.Select((k, n) => $"{Q(e, r.Columns[k])} = {P(n)}")));
                    for (var n = 0; n < r.KeyIndexes.Count; n++) Bind(cmd, n, row[r.KeyIndexes[n]], r.KeyIndexes[n]);
                    await cmd.ExecuteNonQueryAsync(ct);
                    Tick();
                }

            if (sync.Update)
                foreach (var d in r.Different)
                {
                    ct.ThrowIfCancellationRequested();
                    var sets = d.Changed.Where(i => Writable(r, i, forInsert: false)).ToList();
                    if (sets.Count == 0) { Tick(); continue; }
                    var all = sets.Concat(r.KeyIndexes).ToList();
                    await using var cmd = NewCommand($"UPDATE {fq} SET " +
                        string.Join(", ", sets.Select((c, n) => $"{Q(e, r.Columns[c])} = {P(n)}")) + " WHERE " +
                        string.Join(" AND ", r.KeyIndexes.Select((k, n) => $"{Q(e, r.Columns[k])} = {P(sets.Count + n)}")));
                    for (var n = 0; n < all.Count; n++) Bind(cmd, n, d.Source[all[n]], all[n]);
                    await cmd.ExecuteNonQueryAsync(ct);
                    Tick();
                }

            if (sync.Insert && r.OnlyInSource.Count > 0)
            {
                var cols = InsertColumns(r);
                var identityInsert = e == DatabaseEngine.SqlServer && cols.Any(r.IdentityTargetColumns.Contains);
                if (identityInsert)
                {
                    await using var on = NewCommand($"SET IDENTITY_INSERT {fq} ON");
                    await on.ExecuteNonQueryAsync(ct);
                }
                var sql = $"INSERT INTO {fq} ({string.Join(", ", cols.Select(i => Q(e, r.Columns[i])))}) " +
                          $"VALUES ({string.Join(", ", cols.Select((_, n) => P(n)))})";
                foreach (var row in r.OnlyInSource)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var cmd = NewCommand(sql);
                    for (var n = 0; n < cols.Count; n++) Bind(cmd, n, row[cols[n]], cols[n]);
                    await cmd.ExecuteNonQueryAsync(ct);
                    Tick();
                }
                if (identityInsert)
                {
                    await using var off = NewCommand($"SET IDENTITY_INSERT {fq} OFF");
                    await off.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            return done;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection may be gone */ }
            throw;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Q(DatabaseEngine e, string id) => TableCopyService.Q(e, id);

    private static string Fq(DataEndpoint ep) => TableCopyService.Fq(ep.Engine, ep.Location.Schema, ep.Table);
}
