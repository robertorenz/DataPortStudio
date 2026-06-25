using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

public record TableStructure(string Ddl, string Info, string Indexes);

/// <summary>Builds a CREATE TABLE script, summary info, and foreign-key relationships for a table.</summary>
public static class TableMetadataService
{
    private static string Quote(string id) => "[" + id.Replace("]", "]]") + "]";

    /// <summary>Formats a size given in KB as KB / MB / GB.</summary>
    private static string FormatSize(long kb)
    {
        if (kb < 1024) return $"{kb:N0} KB";
        var mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:N1} MB";
        return $"{mb / 1024.0:N2} GB";
    }

    public static Task<TableStructure> GetAsync(
        DatabaseEngine engine, string connectionString, string database, string schema, string table,
        string connectionName = "")
        => engine switch
        {
            DatabaseEngine.Sqlite => GetSqliteAsync(connectionString, table, connectionName),
            DatabaseEngine.Firebird => GetFirebirdAsync(connectionString, table, connectionName),
            DatabaseEngine.MongoDb => MongoService.GetStructureAsync(connectionString, database, table, connectionName),
            DatabaseEngine.Tps => TpsService.GetStructureAsync(connectionString, table, connectionName),
            DatabaseEngine.ClarionDat => DatService.GetStructureAsync(connectionString, table, connectionName),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => GetMySqlAsync(connectionString, database, table, connectionName),
            DatabaseEngine.Oracle => GetOracleAsync(connectionString, table, connectionName),
            _ => GetSqlServerAsync(connectionString, database, schema, table, connectionName)
        };

    private static async Task<TableStructure> GetMySqlAsync(string cs, string db, string table, string connectionName)
    {
        var loc = LocalizationManager.Instance;
        var cols = await MySqlService.GetColumnsAsync(cs, db, table);
        var indexes = await MySqlService.GetIndexesAsync(cs, db, table);
        var pk = cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        long rows = -1;
        try { rows = await MySqlService.GetRowCountAsync(cs, db, table); } catch { }

        var ddl = await MySqlService.GetCreateTableAsync(cs, db, table) ?? "-- (definition not available)";

        const int w = -18;
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{loc["Info_Connection"],w}{connectionName}");
        info.AppendLine($"{loc["Info_Database"],w}{db}");
        info.AppendLine($"{loc["Info_Table"],w}{table}");
        info.AppendLine($"{loc["Info_PrimaryKey"],w}{(pk.Count == 0 ? loc["Info_None"] : string.Join(", ", pk))}");
        info.AppendLine($"{loc["Info_Indexes"],w}{indexes.Count}");
        if (rows >= 0) info.AppendLine($"{loc["Info_Rows"],w}{rows:N0}");
        info.AppendLine();
        info.AppendLine($"{loc["Info_Columns"],w}{cols.Count}");
        info.AppendLine();
        info.AppendLine(loc["Info_Columns"]);
        foreach (var c in cols)
            info.AppendLine($"  • {c.Name}  {c.TypeName}  {(c.Nullable ? "NULL" : "NOT NULL")}");

        return new TableStructure(ddl, info.ToString().TrimEnd(), BuildIndexText(indexes));
    }

    private static async Task<TableStructure> GetOracleAsync(string cs, string table, string connectionName)
    {
        var loc = LocalizationManager.Instance;
        var cols = await OracleService.GetColumnsAsync(cs, table);
        var indexes = await OracleService.GetIndexesAsync(cs, table);
        var pk = cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        long rows = -1;
        try { rows = await OracleService.GetRowCountAsync(cs, table); } catch { /* best effort */ }

        // DDL (reconstructed from the data dictionary).
        var lines = cols.Select(c =>
            $"  {OracleService.Quote(c.Name)} {c.TypeName}{(c.Nullable ? "" : " NOT NULL")}").ToList();
        if (pk.Count > 0)
            lines.Add($"  PRIMARY KEY ({string.Join(", ", pk.Select(OracleService.Quote))})");
        var ddl = $"CREATE TABLE {OracleService.Quote(table)} (\n{string.Join(",\n", lines)}\n);";

        const int w = -18;
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{loc["Info_Connection"],w}{connectionName}");
        info.AppendLine($"{loc["Info_Table"],w}{table}");
        if (rows >= 0) info.AppendLine($"{loc["Info_Rows"],w}{rows:N0}");
        info.AppendLine();
        info.AppendLine($"{loc["Info_Columns"],w}{cols.Count}");
        info.AppendLine($"{loc["Info_PrimaryKey"],w}{(pk.Count == 0 ? loc["Info_None"] : string.Join(", ", pk))}");
        info.AppendLine($"{loc["Info_Indexes"],w}{indexes.Count}");
        info.AppendLine();
        info.AppendLine(loc["Info_Columns"]);
        foreach (var c in cols)
            info.AppendLine($"  • {c.Name}  {c.TypeName}  {(c.Nullable ? "NULL" : "NOT NULL")}");

        return new TableStructure(ddl, info.ToString().TrimEnd(), BuildIndexText(indexes));
    }

    private static async Task<TableStructure> GetFirebirdAsync(string connectionString, string table, string connectionName)
    {
        var loc = LocalizationManager.Instance;
        var cols = await FirebirdService.GetColumnsAsync(connectionString, table);
        var indexes = await FirebirdService.GetIndexesAsync(connectionString, table);
        var pk = cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        long rows = -1;
        try { rows = await FirebirdService.GetRowCountAsync(connectionString, table); } catch { /* best effort */ }

        // DDL (best effort, reconstructed from system tables).
        var ddl = new StringBuilder($"CREATE TABLE {FirebirdService.Quote(table)} (\n");
        var lines = cols.Select(c =>
            $"  {FirebirdService.Quote(c.Name)} {c.TypeName}{(c.Nullable ? "" : " NOT NULL")}").ToList();
        if (pk.Count > 0)
            lines.Add($"  PRIMARY KEY ({string.Join(", ", pk.Select(FirebirdService.Quote))})");
        ddl.Append(string.Join(",\n", lines)).Append("\n);");
        foreach (var ix in indexes)
            ddl.Append($"\nCREATE {(ix.Unique ? "UNIQUE " : "")}INDEX {FirebirdService.Quote(ix.Name)} ON {FirebirdService.Quote(table)} ({string.Join(", ", ix.Columns.Select(FirebirdService.Quote))});");

        // Info.
        const int w = -18;
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{loc["Info_Connection"],w}{connectionName}");
        info.AppendLine($"{loc["Info_Table"],w}{table}");
        if (rows >= 0) info.AppendLine($"{loc["Info_Rows"],w}{rows:N0}");
        info.AppendLine();
        info.AppendLine($"{loc["Info_Columns"],w}{cols.Count}");
        info.AppendLine($"{loc["Info_PrimaryKey"],w}{(pk.Count == 0 ? loc["Info_None"] : string.Join(", ", pk))}");
        info.AppendLine($"{loc["Info_Indexes"],w}{indexes.Count}");
        info.AppendLine();
        info.AppendLine(loc["Info_Columns"]);
        foreach (var c in cols)
            info.AppendLine($"  • {c.Name}  {c.TypeName}  {(c.Nullable ? "NULL" : "NOT NULL")}");

        return new TableStructure(ddl.ToString(), info.ToString().TrimEnd(), BuildIndexText(indexes));
    }

    private static async Task<TableStructure> GetSqlServerAsync(string connectionString, string database, string schema, string table, string connectionName)
    {
        await using var conn = new SqlConnection(SqlServerService.WithDatabase(connectionString, database));
        await conn.OpenAsync();
        var fq = $"{Quote(schema)}.{Quote(table)}";

        var columns = await GetColumnsAsync(conn, fq);
        var identity = await GetIdentityAsync(conn, fq);
        var indexes = await GetIndexesAsync(conn, fq);
        var fks = await GetForeignKeysAsync(conn, fq);

        var ddl = BuildDdl(schema, table, columns, identity, indexes, fks);
        var info = await BuildInfoAsync(conn, fq, schema, table, columns, indexes, database, connectionName);

        return new TableStructure(ddl, info, BuildIndexText(indexes));
    }

    // ---- queries ---------------------------------------------------------

    private sealed record ColumnDef(string Name, string TypeName, int MaxLength, byte Precision, byte Scale,
        bool IsNullable, bool IsComputed, string? Collation, string? Default, string? ComputedDefinition);

    private static async Task<List<ColumnDef>> GetColumnsAsync(SqlConnection conn, string fq)
    {
        const string sql = @"
            SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable,
                   c.is_computed, c.collation_name, dc.definition AS default_def, cc.definition AS computed_def
            FROM sys.columns c
            JOIN sys.types t ON c.user_type_id = t.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@fq)
            ORDER BY c.column_id";

        var list = new List<ColumnDef>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", fq);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ColumnDef(
                r.GetString(0), r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4),
                r.GetBoolean(5), r.GetBoolean(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9)));
        }
        return list;
    }

    private static async Task<Dictionary<string, (long Seed, long Incr)>> GetIdentityAsync(SqlConnection conn, string fq)
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        const string sql = "SELECT name, CONVERT(bigint, seed_value), CONVERT(bigint, increment_value) " +
                           "FROM sys.identity_columns WHERE object_id = OBJECT_ID(@fq)";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", fq);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result[r.GetString(0)] = (r.IsDBNull(1) ? 1 : r.GetInt64(1), r.IsDBNull(2) ? 1 : r.GetInt64(2));
        return result;
    }

    private sealed record IndexDef(string Name, bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint,
        string TypeDesc, List<(string Col, bool Desc)> Columns);

    private static async Task<List<IndexDef>> GetIndexesAsync(SqlConnection conn, string fq)
    {
        const string sql = @"
            SELECT i.name, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.type_desc,
                   col.name AS col, ic.is_descending_key, ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns col ON col.object_id = i.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@fq) AND i.type > 0
            ORDER BY i.index_id, ic.key_ordinal";

        var map = new Dictionary<string, IndexDef>();
        var order = new List<string>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", fq);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.IsDBNull(0) ? "" : r.GetString(0);
            if (!map.TryGetValue(name, out var def))
            {
                def = new IndexDef(name, r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetString(4), new());
                map[name] = def;
                order.Add(name);
            }
            def.Columns.Add((r.GetString(5), r.GetBoolean(6)));
        }
        return order.Select(n => map[n]).ToList();
    }

    private sealed record FkDef(string Name, string ParentSchema, string ParentTable, string RefSchema, string RefTable,
        List<(string ParentCol, string RefCol)> Columns);

    private static async Task<List<FkDef>> GetForeignKeysAsync(SqlConnection conn, string fq)
    {
        const string sql = @"
            SELECT fk.name,
                   OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_NAME(fk.parent_object_id),
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id), OBJECT_NAME(fk.referenced_object_id),
                   pc.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@fq) OR fk.referenced_object_id = OBJECT_ID(@fq)
            ORDER BY fk.name, fkc.constraint_column_id";

        var map = new Dictionary<string, FkDef>();
        var order = new List<string>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", fq);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            if (!map.TryGetValue(name, out var def))
            {
                def = new FkDef(name, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), new());
                map[name] = def;
                order.Add(name);
            }
            def.Columns.Add((r.GetString(5), r.GetString(6)));
        }
        return order.Select(n => map[n]).ToList();
    }

    // ---- builders --------------------------------------------------------

    private static string FormatType(ColumnDef c)
    {
        var t = c.TypeName.ToLowerInvariant();
        switch (t)
        {
            case "varchar": case "char": case "varbinary": case "binary":
                return $"{c.TypeName}({(c.MaxLength == -1 ? "max" : c.MaxLength.ToString())})";
            case "nvarchar": case "nchar":
                return $"{c.TypeName}({(c.MaxLength == -1 ? "max" : (c.MaxLength / 2).ToString())})";
            case "decimal": case "numeric":
                return $"{c.TypeName}({c.Precision},{c.Scale})";
            case "datetime2": case "time": case "datetimeoffset":
                return c.Scale == 7 ? c.TypeName : $"{c.TypeName}({c.Scale})";
            case "float":
                return c.Precision == 53 ? "float" : $"float({c.Precision})";
            default:
                return c.TypeName;
        }
    }

    private static string BuildDdl(string schema, string table, List<ColumnDef> columns,
        Dictionary<string, (long Seed, long Incr)> identity, List<IndexDef> indexes, List<FkDef> fks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {Quote(schema)}.{Quote(table)} (");

        var lines = new List<string>();
        foreach (var c in columns)
        {
            if (c.IsComputed)
            {
                lines.Add($"  {Quote(c.Name)} AS {c.ComputedDefinition}");
                continue;
            }

            var parts = new StringBuilder($"  {Quote(c.Name)} {FormatType(c)}");
            if (c.Collation is not null) parts.Append($" COLLATE {c.Collation}");
            if (identity.TryGetValue(c.Name, out var id)) parts.Append($" IDENTITY({id.Seed},{id.Incr})");
            parts.Append(c.IsNullable ? " NULL" : " NOT NULL");
            if (c.Default is not null) parts.Append($" DEFAULT {c.Default}");
            lines.Add(parts.ToString());
        }

        var pk = indexes.FirstOrDefault(i => i.IsPrimaryKey);
        if (pk is not null)
        {
            var cols = string.Join(", ", pk.Columns.Select(c => Quote(c.Col) + (c.Desc ? " DESC" : "")));
            var clustered = pk.TypeDesc.Contains("CLUSTERED") && !pk.TypeDesc.Contains("NONCLUSTERED")
                ? "CLUSTERED" : "NONCLUSTERED";
            lines.Add($"  CONSTRAINT {Quote(pk.Name)} PRIMARY KEY {clustered} ({cols})");
        }

        sb.AppendLine(string.Join(",\n", lines));
        sb.AppendLine(")");
        sb.AppendLine("GO");

        // Secondary indexes
        foreach (var ix in indexes.Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint))
        {
            var cols = string.Join(", ", ix.Columns.Select(c => Quote(c.Col) + (c.Desc ? " DESC" : "")));
            var unique = ix.IsUnique ? "UNIQUE " : "";
            sb.AppendLine();
            sb.AppendLine($"CREATE {unique}INDEX {Quote(ix.Name)} ON {Quote(schema)}.{Quote(table)} ({cols})");
            sb.AppendLine("GO");
        }

        // Outgoing foreign keys
        foreach (var fk in fks.Where(f => f.ParentSchema == schema && f.ParentTable == table))
        {
            var pcols = string.Join(", ", fk.Columns.Select(c => Quote(c.ParentCol)));
            var rcols = string.Join(", ", fk.Columns.Select(c => Quote(c.RefCol)));
            sb.AppendLine();
            sb.AppendLine($"ALTER TABLE {Quote(schema)}.{Quote(table)} ADD CONSTRAINT {Quote(fk.Name)}");
            sb.AppendLine($"  FOREIGN KEY ({pcols}) REFERENCES {Quote(fk.RefSchema)}.{Quote(fk.RefTable)} ({rcols})");
            sb.AppendLine("GO");
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<string> BuildInfoAsync(SqlConnection conn, string fq, string schema, string table,
        List<ColumnDef> columns, List<IndexDef> indexes, string database, string connectionName)
    {
        long rows = -1, oid = -1;
        DateTime? created = null, modified = null;
        string? comment = null, owner = null, collation = null;
        try
        {
            const string sql = @"
                SELECT t.object_id,
                    (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1)),
                    t.create_date, t.modify_date,
                    CAST(ep.value AS nvarchar(4000)),
                    (SELECT dp.name FROM sys.database_principals dp WHERE dp.principal_id = s.principal_id),
                    CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                LEFT JOIN sys.extended_properties ep
                       ON ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.class = 1 AND ep.name = 'MS_Description'
                WHERE t.object_id = OBJECT_ID(@fq)";
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@fq", fq);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                oid = r.IsDBNull(0) ? -1 : r.GetInt32(0);
                rows = r.IsDBNull(1) ? -1 : r.GetInt64(1);
                created = r.IsDBNull(2) ? null : r.GetDateTime(2);
                modified = r.IsDBNull(3) ? null : r.GetDateTime(3);
                comment = r.IsDBNull(4) ? null : r.GetString(4);
                owner = r.IsDBNull(5) ? null : r.GetString(5);
                collation = r.IsDBNull(6) ? null : r.GetString(6);
            }
        }
        catch { /* info is best-effort */ }

        // Storage sizes (sp_spaceused-style, in KB).
        long dataKb = -1, indexKb = -1, totalKb = -1;
        try
        {
            const string sizeSql = @"
                SELECT
                    SUM(ps.reserved_page_count) * 8 AS reserved_kb,
                    SUM(ps.used_page_count) * 8 AS used_kb,
                    SUM(CASE WHEN ps.index_id < 2
                             THEN ps.in_row_data_page_count + ps.lob_used_page_count + ps.row_overflow_used_page_count
                             ELSE ps.lob_used_page_count + ps.row_overflow_used_page_count END) * 8 AS data_kb
                FROM sys.dm_db_partition_stats ps
                WHERE ps.object_id = OBJECT_ID(@fq)";
            await using var cmd = new SqlCommand(sizeSql, conn);
            cmd.Parameters.AddWithValue("@fq", fq);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync() && !r.IsDBNull(0))
            {
                totalKb = Convert.ToInt64(r.GetValue(0));
                var usedKb = Convert.ToInt64(r.GetValue(1));
                dataKb = Convert.ToInt64(r.GetValue(2));
                indexKb = usedKb - dataKb;
            }
        }
        catch { /* sizes are best-effort */ }

        var loc = LocalizationManager.Instance;
        var pk = indexes.FirstOrDefault(i => i.IsPrimaryKey);
        const int w = -18;
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) sb.AppendLine($"{loc["Info_Connection"],w}{connectionName}");
        sb.AppendLine($"{loc["Info_Database"],w}{database}");
        sb.AppendLine($"{loc["Info_Schema"],w}{schema}");
        if (!string.IsNullOrEmpty(owner)) sb.AppendLine($"{loc["Info_Owner"],w}{owner}");
        if (oid >= 0) sb.AppendLine($"{loc["Info_Oid"],w}{oid}");
        if (rows >= 0) sb.AppendLine($"{loc["Info_Rows"],w}{rows:N0}");
        if (dataKb >= 0) sb.AppendLine($"{loc["Info_DataSize"],w}{FormatSize(dataKb)}");
        if (indexKb >= 0) sb.AppendLine($"{loc["Info_IndexSize"],w}{FormatSize(indexKb)}");
        if (totalKb >= 0) sb.AppendLine($"{loc["Info_TotalSize"],w}{FormatSize(totalKb)}");
        if (!string.IsNullOrEmpty(collation)) sb.AppendLine($"{loc["Info_Collation"],w}{collation}");
        if (created is not null) sb.AppendLine($"{loc["Info_Created"],w}{created:yyyy-MM-dd HH:mm:ss.fff}");
        if (modified is not null) sb.AppendLine($"{loc["Info_Modified"],w}{modified:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"{loc["Info_Comment"],w}{(string.IsNullOrWhiteSpace(comment) ? "--" : comment)}");
        sb.AppendLine();
        sb.AppendLine($"{loc["Info_Columns"],w}{columns.Count}");
        sb.AppendLine($"{loc["Info_PrimaryKey"],w}{(pk is null ? loc["Info_None"] : string.Join(", ", pk.Columns.Select(c => c.Col)))}");
        sb.AppendLine($"{loc["Info_Indexes"],w}{indexes.Count}");
        sb.AppendLine();
        sb.AppendLine(loc["Info_Columns"]);
        foreach (var c in columns)
            sb.AppendLine($"  • {c.Name}  {FormatType(c)}  {(c.IsNullable ? "NULL" : "NOT NULL")}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats a list of (name, unique, columns) indexes for the structure panel.</summary>
    private static string BuildIndexText(IReadOnlyList<(string Name, bool Unique, List<string> Columns)> indexes)
    {
        var loc = LocalizationManager.Instance;
        if (indexes.Count == 0) return loc["Insp_NoIndexes"];
        var sb = new StringBuilder();
        sb.AppendLine(loc["Info_Indexes"]);
        foreach (var ix in indexes)
            sb.AppendLine($"  • {ix.Name}{(ix.Unique ? " (UNIQUE)" : "")}  ({string.Join(", ", ix.Columns)})");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats SQL Server indexes (with PK / unique flags and type) for the structure panel.</summary>
    private static string BuildIndexText(List<IndexDef> indexes)
    {
        var loc = LocalizationManager.Instance;
        if (indexes.Count == 0) return loc["Insp_NoIndexes"];
        var sb = new StringBuilder();
        sb.AppendLine(loc["Info_Indexes"]);
        foreach (var ix in indexes)
        {
            var flags = ix.IsPrimaryKey ? " (PRIMARY KEY)" : ix.IsUnique ? " (UNIQUE)" : "";
            var cols = string.Join(", ", ix.Columns.Select(c => c.Col + (c.Desc ? " DESC" : "")));
            sb.AppendLine($"  • {ix.Name}{flags}  ({cols})  [{ix.TypeDesc}]");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- SQLite ----------------------------------------------------------

    private static async Task<TableStructure> GetSqliteAsync(string connectionString, string table, string connectionName)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        var ddl = await SqliteScalarAsync(conn,
            "SELECT sql FROM sqlite_master WHERE name = $t", table) ?? "-- (definition not available)";

        var columns = new List<(string Name, string Type, bool NotNull, int Pk, string? Default)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}')";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                columns.Add((r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    r.GetInt32(3) != 0, r.GetInt32(5), r.IsDBNull(4) ? null : r.GetValue(4)?.ToString()));
        }

        var indexes = await SqliteService.GetIndexesAsync(connectionString, table);

        long rows = -1;
        try { rows = Convert.ToInt64(await SqliteScalarObjAsync(conn, $"SELECT COUNT(*) FROM {Quote(table)}")); }
        catch { /* best effort */ }

        var loc = LocalizationManager.Instance;
        const int w = -18;
        var pk = columns.Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToList();
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{loc["Info_Connection"],w}{connectionName}");
        info.AppendLine($"{loc["Info_Table"],w}{table}");
        if (rows >= 0) info.AppendLine($"{loc["Info_Rows"],w}{rows:N0}");
        info.AppendLine();
        info.AppendLine($"{loc["Info_Columns"],w}{columns.Count}");
        info.AppendLine($"{loc["Info_PrimaryKey"],w}{(pk.Count == 0 ? loc["Info_None"] : string.Join(", ", pk))}");
        info.AppendLine($"{loc["Info_Indexes"],w}{indexes.Count}");
        info.AppendLine();
        info.AppendLine(loc["Info_Columns"]);
        foreach (var c in columns)
            info.AppendLine($"  • {c.Name}  {(string.IsNullOrEmpty(c.Type) ? "" : c.Type)}  {(c.NotNull ? "NOT NULL" : "NULL")}".TrimEnd());

        return new TableStructure(ddl.TrimEnd() + (ddl.TrimEnd().EndsWith(";") ? "" : ";"),
            info.ToString().TrimEnd(), BuildIndexText(indexes));
    }

    private static async Task<string?> SqliteScalarAsync(SqliteConnection conn, string sql, string tableParam)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$t", tableParam);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static async Task<object?> SqliteScalarObjAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
