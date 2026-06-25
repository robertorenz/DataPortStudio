using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using MongoDB.Driver;
using MySqlConnector;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>Duplicates a table/collection — within one connection, or across two of the same engine.</summary>
public static class TableCopyService
{
    /// <summary>Existing table/collection names at the target location (for picking a free copy name).</summary>
    public static Task<List<string>> ListObjectsAsync(ConnectionProfile p, string database, string schema)
    {
        var cs = p.BuildConnectionString();
        return p.Engine switch
        {
            DatabaseEngine.Sqlite => SqliteService.GetTablesAsync(cs),
            DatabaseEngine.Firebird => FirebirdService.GetTablesAsync(cs),
            DatabaseEngine.MongoDb => MongoService.ListCollectionsAsync(cs, database),
            DatabaseEngine.Tps => Task.FromResult(TpsService.ListTables(p.FilePath)),
            DatabaseEngine.ClarionDat => Task.FromResult(DatService.ListTables(p.FilePath)),
            DatabaseEngine.Oracle => OracleService.GetTablesAsync(cs),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => MySqlService.GetTablesAsync(cs, database),
            _ => SqlServerService.GetTablesAsync(cs, database, schema)
        };
    }

    /// <summary>Picks "name", else "name_copy", "name_copy2", … that isn't already taken.</summary>
    public static string FreeName(string name, ICollection<string> existing)
    {
        bool Taken(string n) => existing.Contains(n) ||
            existing.Any(e => string.Equals(e, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        var candidate = name + "_copy";
        var i = 2;
        while (Taken(candidate)) candidate = $"{name}_copy{i++}";
        return candidate;
    }

    public static Task CopyAsync(ConnectionProfile p,
        string srcDatabase, string srcSchema, string srcName,
        string tgtDatabase, string tgtSchema, string newName, bool includeData)
        => p.Engine switch
        {
            DatabaseEngine.Sqlite => CopySqliteAsync(p, srcName, newName, includeData),
            DatabaseEngine.Firebird => CopyFirebirdAsync(p, srcName, newName, includeData),
            DatabaseEngine.MongoDb => CopyMongoAsync(p, tgtDatabase, srcName, newName, includeData),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => CopyMySqlAsync(p, srcDatabase, srcName, newName, includeData),
            DatabaseEngine.Oracle => CopyOracleAsync(p, srcName, newName, includeData),
            _ => CopySqlServerAsync(p, tgtDatabase, srcSchema, srcName, tgtSchema, newName, includeData)
        };

    // ---- MySQL / MariaDB (CREATE TABLE … LIKE + INSERT SELECT) ----------
    private static async Task CopyMySqlAsync(ConnectionProfile p, string db, string srcName, string newName, bool includeData)
    {
        var cs = p.BuildConnectionString();
        var src = MySqlService.Quote(srcName);
        var dst = MySqlService.Quote(newName);
        await MySqlService.ExecuteAsync(cs, db, $"CREATE TABLE {dst} LIKE {src}");
        if (includeData)
            await MySqlService.ExecuteAsync(cs, db, $"INSERT INTO {dst} SELECT * FROM {src}");
    }

    // ---- Oracle (CREATE TABLE AS SELECT) --------------------------------
    private static async Task CopyOracleAsync(ConnectionProfile p, string srcName, string newName, bool includeData)
    {
        var cs = p.BuildConnectionString();
        var where = includeData ? "" : " WHERE 1 = 0";
        await OracleService.ExecuteAsync(cs,
            $"CREATE TABLE {OracleService.Quote(newName)} AS SELECT * FROM {OracleService.Quote(srcName)}{where}");
    }

    private static string B(string id) => "[" + id.Replace("]", "]]") + "]";

    // ---- SQL Server (SELECT INTO copies columns + identity) --------------
    private static async Task CopySqlServerAsync(ConnectionProfile p, string db,
        string srcSchema, string srcName, string tgtSchema, string newName, bool includeData)
    {
        var src = $"{B(srcSchema)}.{B(srcName)}";
        var dst = $"{B(tgtSchema)}.{B(newName)}";
        var where = includeData ? "" : " WHERE 1 = 0";
        await SqlServerService.ExecuteAsync(p.BuildConnectionString(), db,
            $"SELECT * INTO {dst} FROM {src}{where}");
    }

    // ---- SQLite (clone the original CREATE statement) --------------------
    private static async Task CopySqliteAsync(ConnectionProfile p, string srcName, string newName, bool includeData)
    {
        var cs = p.BuildConnectionString();
        var createSql = await SqliteScalarAsync(cs,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=$n", srcName);
        if (string.IsNullOrWhiteSpace(createSql))
            throw new InvalidOperationException($"Could not read the definition of '{srcName}'.");

        // Replace the table identifier right after CREATE TABLE with the new name.
        var newCreate = Regex.Replace(createSql,
            @"^\s*CREATE\s+TABLE\s+(""[^""]*""|\[[^\]]*\]|`[^`]*`|\w+)",
            "CREATE TABLE " + B(newName),
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        await SqliteService.ExecuteScriptAsync(cs, newCreate);
        if (includeData)
            await SqliteService.ExecuteScriptAsync(cs, $"INSERT INTO {B(newName)} SELECT * FROM {B(srcName)}");
    }

    // ---- Firebird (rebuild CREATE from catalog) -------------------------
    private static async Task CopyFirebirdAsync(ConnectionProfile p, string srcName, string newName, bool includeData)
    {
        var cs = p.BuildConnectionString();
        var cols = await FirebirdService.GetColumnsAsync(cs, srcName);
        if (cols.Count == 0)
            throw new InvalidOperationException($"Could not read the columns of '{srcName}'.");

        var lines = cols.Select(c => $"  {FirebirdService.Quote(c.Name)} {c.TypeName}{(c.Nullable ? "" : " NOT NULL")}").ToList();
        var pk = cols.Where(c => c.IsPrimaryKey).Select(c => FirebirdService.Quote(c.Name)).ToList();
        if (pk.Count > 0) lines.Add($"  PRIMARY KEY ({string.Join(", ", pk)})");
        await FirebirdService.ExecuteAsync(cs,
            $"CREATE TABLE {FirebirdService.Quote(newName)} (\n{string.Join(",\n", lines)}\n)");

        if (includeData)
        {
            var colList = string.Join(", ", cols.Select(c => FirebirdService.Quote(c.Name)));
            await FirebirdService.ExecuteAsync(cs,
                $"INSERT INTO {FirebirdService.Quote(newName)} ({colList}) SELECT {colList} FROM {FirebirdService.Quote(srcName)}");
        }
    }

    // ---- MongoDB --------------------------------------------------------
    private static async Task CopyMongoAsync(ConnectionProfile p, string db, string srcName, string newName, bool includeData)
    {
        var database = new MongoClient(p.BuildConnectionString()).GetDatabase(db);
        if (includeData)
        {
            var src = database.GetCollection<BsonDocument>(srcName);
            await src.AggregateAsync<BsonDocument>(new BsonDocument[] { new("$out", newName) });
            // $out drops/creates the target; if the source is empty it still leaves no collection,
            // so ensure it exists.
            var names = await (await database.ListCollectionNamesAsync()).ToListAsync();
            if (!names.Contains(newName)) await database.CreateCollectionAsync(newName);
        }
        else
        {
            await database.CreateCollectionAsync(newName);
        }
    }

    private static async Task<string?> SqliteScalarAsync(string cs, string sql, string param)
    {
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$n", param);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    // =====================================================================
    //  Cross-connection copy (two connections of the same engine)
    // =====================================================================

    public static bool IsRelational(DatabaseEngine e) =>
        e is DatabaseEngine.SqlServer or DatabaseEngine.Sqlite or DatabaseEngine.Firebird
          or DatabaseEngine.MySql or DatabaseEngine.MariaDb or DatabaseEngine.Oracle;

    /// <summary>True if a table can be copied from one engine to the other.</summary>
    public static bool CanCopyBetween(DatabaseEngine a, DatabaseEngine b) =>
        // Clarion files (TPS/DAT) are read-only: never a copy target, but a source into any relational engine.
        !b.IsClarionFile() &&
        (a == b
            || (IsRelational(a) && IsRelational(b))
            || (a.IsClarionFile() && IsRelational(b)));

    public static Task CopyCrossAsync(
        ConnectionProfile src, string srcDb, string srcSchema, string srcName,
        ConnectionProfile tgt, string tgtDb, string tgtSchema, string newName, bool includeData,
        IReadOnlyList<ClarionColumnMap>? clarionMappings = null)
    {
        if (src.Engine.IsClarionFile())
            return CopyClarionFileCrossAsync(src, srcName, tgt, tgtDb, tgtSchema, newName, includeData, clarionMappings);

        if (src.Engine == DatabaseEngine.MongoDb || tgt.Engine == DatabaseEngine.MongoDb)
        {
            if (src.Engine == DatabaseEngine.MongoDb && tgt.Engine == DatabaseEngine.MongoDb)
                return CopyMongoCrossAsync(src, srcDb, srcName, tgt, tgtDb, newName, includeData);
            throw new NotSupportedException("Copying between MongoDB and a relational database isn't supported.");
        }
        return CopyRelationalCrossAsync(src, srcDb, srcSchema, srcName, tgt, tgtDb, tgtSchema, newName, includeData);
    }

    private static string Q(DatabaseEngine e, string id) => e is DatabaseEngine.Firebird or DatabaseEngine.Oracle
        ? "\"" + id.Replace("\"", "\"\"") + "\""
        : e.IsMySql()
            ? "`" + id.Replace("`", "``") + "`"
            : "[" + id.Replace("]", "]]") + "]";

    private static string Fq(DatabaseEngine e, string schema, string name) =>
        e == DatabaseEngine.SqlServer ? $"{Q(e, schema)}.{Q(e, name)}" : Q(e, name);

    private static async Task<DbConnection> OpenAsync(ConnectionProfile p, string db)
    {
        DbConnection conn = p.Engine switch
        {
            DatabaseEngine.SqlServer => new SqlConnection(SqlServerService.WithDatabase(p.BuildConnectionString(), db)),
            DatabaseEngine.Sqlite => new SqliteConnection(p.BuildConnectionString()),
            DatabaseEngine.Firebird => new FbConnection(p.BuildConnectionString()),
            DatabaseEngine.Oracle => new Oracle.ManagedDataAccess.Client.OracleConnection(p.BuildConnectionString()),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                new MySqlConnection(string.IsNullOrEmpty(db) ? p.BuildConnectionString() : MySqlService.WithDatabase(p.BuildConnectionString(), db)),
            _ => throw new NotSupportedException($"{p.Engine.DisplayName()} cross-copy is not supported.")
        };
        await conn.OpenAsync();
        return conn;
    }

    private static async Task CopyRelationalCrossAsync(
        ConnectionProfile src, string srcDb, string srcSchema, string srcName,
        ConnectionProfile tgt, string tgtDb, string tgtSchema, string newName, bool includeData)
    {
        // 1. Create the structure at the target (clone when same engine; map types when different).
        var ddl = src.Engine == tgt.Engine
            ? await BuildCreateDdlAsync(src, srcDb, srcSchema, srcName, tgtSchema, newName)
            : await BuildCrossEngineDdlAsync(src, srcDb, srcSchema, srcName, tgt, tgtSchema, newName);
        await ExecuteAsync(tgt, tgtDb, ddl);

        if (!includeData) return;

        // 2. Move the rows. SQL Server uses fast streaming bulk copy; others a prepared INSERT.
        if (tgt.Engine == DatabaseEngine.SqlServer)
            await BulkCopySqlServerAsync(src, srcDb, srcSchema, srcName, tgt, tgtDb, tgtSchema, newName);
        else
            await GenericPumpAsync(src, srcDb, srcSchema, srcName, tgt, tgtDb, tgtSchema, newName);
    }

    /// <summary>Streams source rows into a SQL Server target with SqlBulkCopy (fast, set-based).</summary>
    private static async Task BulkCopySqlServerAsync(
        ConnectionProfile src, string srcDb, string srcSchema, string srcName,
        ConnectionProfile tgt, string tgtDb, string tgtSchema, string newName)
    {
        await using var srcConn = await OpenAsync(src, srcDb);
        await using var readCmd = srcConn.CreateCommand();
        readCmd.CommandText = $"SELECT * FROM {Fq(src.Engine, srcSchema, srcName)}";
        readCmd.CommandTimeout = 0;
        await using var reader = await readCmd.ExecuteReaderAsync();

        await using var tgtConn = (SqlConnection)await OpenAsync(tgt, tgtDb);
        using var bulk = new SqlBulkCopy(tgtConn)
        {
            DestinationTableName = $"{Q(DatabaseEngine.SqlServer, tgtSchema)}.{Q(DatabaseEngine.SqlServer, newName)}",
            BulkCopyTimeout = 0,
            BatchSize = 10_000,
            EnableStreaming = true
        };
        for (var i = 0; i < reader.FieldCount; i++)
            bulk.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));
        await bulk.WriteToServerAsync(reader);
    }

    /// <summary>Streams source rows into a non-SQL-Server target via one prepared, parameterized INSERT.</summary>
    private static async Task GenericPumpAsync(
        ConnectionProfile src, string srcDb, string srcSchema, string srcName,
        ConnectionProfile tgt, string tgtDb, string tgtSchema, string newName)
    {
        await using var srcConn = await OpenAsync(src, srcDb);
        await using var readCmd = srcConn.CreateCommand();
        readCmd.CommandText = $"SELECT * FROM {Fq(src.Engine, srcSchema, srcName)}";
        await using var reader = await readCmd.ExecuteReaderAsync();

        var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        if (cols.Count == 0) return;

        await using var tgtConn = await OpenAsync(tgt, tgtDb);
        await using var tx = await tgtConn.BeginTransactionAsync();

        var oracle = tgt.Engine == DatabaseEngine.Oracle;
        var ph = oracle ? ":" : "@";
        var colList = string.Join(", ", cols.Select(c => Q(tgt.Engine, c)));
        var paramList = string.Join(", ", cols.Select((_, i) => $"{ph}p{i}"));
        await using var insert = tgtConn.CreateCommand();
        if (insert is Oracle.ManagedDataAccess.Client.OracleCommand oc) oc.BindByName = true;
        insert.Transaction = (DbTransaction)tx;
        insert.CommandText = $"INSERT INTO {Fq(tgt.Engine, tgtSchema, newName)} ({colList}) VALUES ({paramList})";

        var ps = new DbParameter[cols.Count];
        for (var i = 0; i < cols.Count; i++)
        {
            var p = insert.CreateParameter();
            p.ParameterName = (oracle ? "p" : "@p") + i;
            insert.Parameters.Add(p);
            ps[i] = p;
        }

        while (await reader.ReadAsync())
        {
            for (var i = 0; i < cols.Count; i++)
                ps[i].Value = SafeReaderValue(reader, i);
            await insert.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>Reads a cell, treating unconvertible values (e.g. out-of-range Oracle dates) as NULL.</summary>
    private static object SafeReaderValue(DbDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return DBNull.Value;
        try { return reader.GetValue(i) ?? DBNull.Value; }
        catch { return DBNull.Value; }
    }

    private static async Task<string> BuildCreateDdlAsync(
        ConnectionProfile src, string srcDb, string srcSchema, string srcName, string tgtSchema, string newName)
    {
        var cs = src.BuildConnectionString();
        switch (src.Engine)
        {
            case DatabaseEngine.Sqlite:
            {
                var create = await SqliteScalarAsync(cs,
                    "SELECT sql FROM sqlite_master WHERE type='table' AND name=$n", srcName)
                    ?? throw new InvalidOperationException($"Could not read the definition of '{srcName}'.");
                return Regex.Replace(create,
                    @"^\s*CREATE\s+TABLE\s+(""[^""]*""|\[[^\]]*\]|`[^`]*`|\w+)",
                    "CREATE TABLE " + Q(DatabaseEngine.Sqlite, newName),
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            case DatabaseEngine.Firebird:
            {
                var cols = await FirebirdService.GetColumnsAsync(cs, srcName);
                var lines = cols.Select(c => $"  {Q(src.Engine, c.Name)} {c.TypeName}{(c.Nullable ? "" : " NOT NULL")}").ToList();
                var pk = cols.Where(c => c.IsPrimaryKey).Select(c => Q(src.Engine, c.Name)).ToList();
                if (pk.Count > 0) lines.Add($"  PRIMARY KEY ({string.Join(", ", pk)})");
                return $"CREATE TABLE {Q(src.Engine, newName)} (\n{string.Join(",\n", lines)}\n)";
            }
            case DatabaseEngine.MySql or DatabaseEngine.MariaDb:
            {
                var cols = await MySqlService.GetColumnsAsync(cs, srcDb, srcName);
                if (cols.Count == 0)
                    throw new InvalidOperationException($"Could not read the columns of '{srcName}'.");
                // Clone column types verbatim; drop AUTO_INCREMENT so explicit values can be inserted.
                var lines = cols.Select(c => $"  {Q(src.Engine, c.Name)} {c.TypeName}{(c.Nullable ? "" : " NOT NULL")}").ToList();
                var pk = cols.Where(c => c.IsPrimaryKey).Select(c => Q(src.Engine, c.Name)).ToList();
                if (pk.Count > 0) lines.Add($"  PRIMARY KEY ({string.Join(", ", pk)})");
                return $"CREATE TABLE {Q(src.Engine, newName)} (\n{string.Join(",\n", lines)}\n)";
            }
            case DatabaseEngine.Oracle:
            {
                var cols = await OracleService.GetColumnsAsync(cs, srcName);
                if (cols.Count == 0)
                    throw new InvalidOperationException($"Could not read the columns of '{srcName}'.");
                var lines = cols.Select(c => $"  {Q(src.Engine, c.Name)} {c.TypeName}{(c.Nullable ? "" : " NOT NULL")}").ToList();
                var pk = cols.Where(c => c.IsPrimaryKey).Select(c => Q(src.Engine, c.Name)).ToList();
                if (pk.Count > 0) lines.Add($"  PRIMARY KEY ({string.Join(", ", pk)})");
                return $"CREATE TABLE {Q(src.Engine, newName)} (\n{string.Join(",\n", lines)}\n)";
            }
            default: // SQL Server
            {
                var cols = await SqlServerService.GetColumnDetailsAsync(cs, srcDb, srcSchema, srcName);
                var lines = new List<string>();
                foreach (var c in cols)
                {
                    // No IDENTITY on the copy so explicit values can be inserted.
                    var line = $"  {Q(src.Engine, c.Name)} {SqlServerType(c)}{(c.Nullable ? " NULL" : " NOT NULL")}";
                    lines.Add(line);
                }
                var pk = cols.Where(c => c.IsPrimaryKey).Select(c => Q(src.Engine, c.Name)).ToList();
                if (pk.Count > 0)
                    lines.Add($"  CONSTRAINT {Q(src.Engine, "PK_" + newName)} PRIMARY KEY ({string.Join(", ", pk)})");
                return $"CREATE TABLE {Q(src.Engine, tgtSchema)}.{Q(src.Engine, newName)} (\n{string.Join(",\n", lines)}\n)";
            }
        }
    }

    // ---- cross-engine structure (map source column CLR types to target) --

    private sealed record CrossColumn(string Name, Type Type, int Size, int Precision, int Scale, bool Nullable);

    private static async Task<string> BuildCrossEngineDdlAsync(
        ConnectionProfile src, string srcDb, string srcSchema, string srcName,
        ConnectionProfile tgt, string tgtSchema, string newName)
    {
        var cols = await DescribeAsync(src, srcDb, srcSchema, srcName);
        if (cols.Count == 0)
            throw new InvalidOperationException($"Could not read the columns of '{srcName}'.");

        var pk = new HashSet<string>(
            await GetPrimaryKeyNamesAsync(src, srcDb, srcSchema, srcName), StringComparer.OrdinalIgnoreCase);

        var lines = cols.Select(c =>
        {
            var notNull = !c.Nullable || pk.Contains(c.Name);
            return $"  {Q(tgt.Engine, c.Name)} {MapType(tgt.Engine, c)}{(notNull ? " NOT NULL" : "")}";
        }).ToList();

        var pkCols = cols.Where(c => pk.Contains(c.Name)).Select(c => Q(tgt.Engine, c.Name)).ToList();
        if (pkCols.Count > 0)
            lines.Add(tgt.Engine == DatabaseEngine.SqlServer
                ? $"  CONSTRAINT {Q(tgt.Engine, "PK_" + newName)} PRIMARY KEY ({string.Join(", ", pkCols)})"
                : $"  PRIMARY KEY ({string.Join(", ", pkCols)})");

        var fq = Fq(tgt.Engine, tgtSchema, newName);
        return $"CREATE TABLE {fq} (\n{string.Join(",\n", lines)}\n)";
    }

    private static async Task<List<CrossColumn>> DescribeAsync(
        ConnectionProfile p, string db, string schema, string name)
    {
        await using var conn = await OpenAsync(p, db);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Fq(p.Engine, schema, name)}";
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly);
        var schemaTable = await reader.GetSchemaTableAsync();

        var result = new List<CrossColumn>();
        if (schemaTable is null) return result;

        int? AsInt(System.Data.DataRow r, string col)
            => schemaTable.Columns.Contains(col) && r[col] is not (null or DBNull) ? Convert.ToInt32(r[col]) : null;
        bool AsBool(System.Data.DataRow r, string col, bool dflt)
            => schemaTable.Columns.Contains(col) && r[col] is bool b ? b : dflt;

        foreach (System.Data.DataRow r in schemaTable.Rows)
        {
            var cname = r["ColumnName"]?.ToString() ?? "";
            var ctype = r["DataType"] as Type ?? typeof(string);
            result.Add(new CrossColumn(cname, ctype,
                AsInt(r, "ColumnSize") ?? 0, AsInt(r, "NumericPrecision") ?? 0,
                AsInt(r, "NumericScale") ?? 0, AsBool(r, "AllowDBNull", true)));
        }
        return result;
    }

    private static Task<List<string>> GetPrimaryKeyNamesAsync(
        ConnectionProfile p, string db, string schema, string name)
    {
        var cs = p.BuildConnectionString();
        return p.Engine switch
        {
            DatabaseEngine.SqlServer => SqlServerService.GetPrimaryKeyAsync(cs, db, schema, name)
                .ContinueWith(t => t.Result.Columns),
            DatabaseEngine.Firebird => FirebirdService.GetPrimaryKeyAsync(cs, name),
            DatabaseEngine.Sqlite => SqliteService.GetColumnDetailsAsync(cs, name)
                .ContinueWith(t => t.Result.Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToList()),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => MySqlService.GetPrimaryKeyAsync(cs, db, name),
            DatabaseEngine.Oracle => OracleService.GetPrimaryKeyAsync(cs, name),
            _ => Task.FromResult(new List<string>())
        };
    }

    private static bool IsLarge(int size, int threshold) => size <= 0 || size > threshold || size == int.MaxValue;

    private static string MapType(DatabaseEngine target, CrossColumn c)
    {
        var t = Nullable.GetUnderlyingType(c.Type) ?? c.Type;
        var tn = t.Name;

        return target switch
        {
            DatabaseEngine.Oracle => tn switch
            {
                "Int64" => "NUMBER(19)",
                "Int32" => "NUMBER(10)",
                "Int16" => "NUMBER(5)",
                "Byte" or "SByte" => "NUMBER(3)",
                "Boolean" => "NUMBER(1)",
                "Decimal" => $"NUMBER({ClampPrec(c.Precision, 38)},{ClampScale(c.Scale, c.Precision, 38)})",
                "Double" => "BINARY_DOUBLE",
                "Single" => "BINARY_FLOAT",
                "Guid" => "RAW(16)",
                "DateTime" or "DateTimeOffset" => "TIMESTAMP",
                "DateOnly" => "DATE",
                "TimeSpan" or "TimeOnly" => "INTERVAL DAY TO SECOND",
                "Byte[]" => "BLOB",
                "String" or "Char" => IsLarge(c.Size, 4000) ? "CLOB" : $"VARCHAR2({c.Size})",
                _ => "CLOB"
            },
            DatabaseEngine.Sqlite => tn switch
            {
                "Int16" or "Int32" or "Int64" or "Byte" or "SByte" or "Boolean" => "INTEGER",
                "Decimal" => "NUMERIC",
                "Double" or "Single" => "REAL",
                "Byte[]" => "BLOB",
                _ => "TEXT"
            },
            DatabaseEngine.Firebird => tn switch
            {
                "Int64" => "BIGINT",
                "Int32" => "INTEGER",
                "Int16" or "Byte" or "SByte" => "SMALLINT",
                "Boolean" => "BOOLEAN",
                "Decimal" => $"DECIMAL({ClampPrec(c.Precision, 18)},{ClampScale(c.Scale, c.Precision, 18)})",
                "Double" => "DOUBLE PRECISION",
                "Single" => "FLOAT",
                "Guid" => "CHAR(38)",
                "DateTime" or "DateTimeOffset" => "TIMESTAMP",
                "DateOnly" => "DATE",
                "TimeSpan" or "TimeOnly" => "TIME",
                "Byte[]" => "BLOB",
                "String" or "Char" => IsLarge(c.Size, 8191) ? "BLOB SUB_TYPE TEXT" : $"VARCHAR({c.Size})",
                _ => "BLOB SUB_TYPE TEXT"
            },
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => tn switch
            {
                "Int64" => "BIGINT",
                "Int32" => "INT",
                "Int16" => "SMALLINT",
                "Byte" or "SByte" => "TINYINT",
                "Boolean" => "TINYINT(1)",
                "Decimal" => $"DECIMAL({ClampPrec(c.Precision, 65)},{ClampScale(c.Scale, c.Precision, 65)})",
                "Double" => "DOUBLE",
                "Single" => "FLOAT",
                "Guid" => "CHAR(36)",
                "DateTime" or "DateTimeOffset" => "DATETIME",
                "DateOnly" => "DATE",
                "TimeSpan" or "TimeOnly" => "TIME",
                "Byte[]" => "LONGBLOB",
                "String" or "Char" => IsLarge(c.Size, 4000) ? "LONGTEXT" : $"VARCHAR({c.Size})",
                _ => "LONGTEXT"
            },
            _ => tn switch // SQL Server
            {
                "Int64" => "bigint",
                "Int32" => "int",
                "Int16" => "smallint",
                "Byte" or "SByte" => "tinyint",
                "Boolean" => "bit",
                "Decimal" => $"decimal({ClampPrec(c.Precision, 38)},{ClampScale(c.Scale, c.Precision, 38)})",
                "Double" => "float",
                "Single" => "real",
                "Guid" => "uniqueidentifier",
                "DateTime" => "datetime2",
                "DateTimeOffset" => "datetimeoffset",
                "DateOnly" => "date",
                "TimeSpan" or "TimeOnly" => "time",
                "Byte[]" => "varbinary(max)",
                "String" or "Char" => IsLarge(c.Size, 4000) ? "nvarchar(max)" : $"nvarchar({c.Size})",
                _ => "nvarchar(max)"
            }
        };
    }

    private static int ClampPrec(int prec, int max) => prec is > 0 and <= 100 ? Math.Min(prec, max) : max == 38 ? 38 : 18;
    private static int ClampScale(int scale, int prec, int maxPrec)
    {
        var p = ClampPrec(prec, maxPrec);
        if (scale < 0) scale = maxPrec == 38 ? 6 : 4;
        return Math.Min(scale, p);
    }

    private static string SqlServerType(SqlServerService.ColumnDetail c)
    {
        var t = c.TypeName.ToLowerInvariant();
        return t switch
        {
            "char" or "varchar" or "binary" or "varbinary" => $"{t}({(c.MaxLength == -1 ? "max" : c.MaxLength.ToString())})",
            "nchar" or "nvarchar" => $"{t}({(c.MaxLength == -1 ? "max" : (c.MaxLength / 2).ToString())})",
            "decimal" or "numeric" => $"{t}({c.Precision},{c.Scale})",
            _ => t
        };
    }

    private static async Task ExecuteAsync(ConnectionProfile p, string db, string sql)
    {
        switch (p.Engine)
        {
            case DatabaseEngine.Sqlite: await SqliteService.ExecuteScriptAsync(p.BuildConnectionString(), sql); break;
            case DatabaseEngine.Firebird: await FirebirdService.ExecuteAsync(p.BuildConnectionString(), sql); break;
            case DatabaseEngine.MySql or DatabaseEngine.MariaDb: await MySqlService.ExecuteAsync(p.BuildConnectionString(), db, sql); break;
            case DatabaseEngine.Oracle: await OracleService.ExecuteAsync(p.BuildConnectionString(), sql); break;
            default: await SqlServerService.ExecuteAsync(p.BuildConnectionString(), db, sql); break;
        }
    }

    // =====================================================================
    //  Clarion file (TPS / DAT) → relational  (read-only source: decode the
    //  file, build the target table and stream the rows in)
    // =====================================================================

    /// <summary>One source column and the SQL type proposed for it (the user may edit <see cref="TargetType"/>).</summary>
    public sealed record ClarionColumnMap(string Name, string SourceType, string TargetType);

    private static DataTable ReadClarion(ConnectionProfile src, string srcName, int rowLimit) =>
        src.Engine == DatabaseEngine.ClarionDat
            ? DatService.ReadTable(src.FilePath ?? "", srcName, rowLimit)
            : TpsService.ReadTable(src.FilePath ?? "", srcName, rowLimit);

    private static CrossColumn ToCrossColumn(DataColumn c)
    {
        var prec = c.ExtendedProperties["prec"] is int p ? p : 0;
        var scale = c.ExtendedProperties["scale"] is int s ? s : 0;
        var size = c.DataType == typeof(string) ? c.MaxLength : 0;
        return new CrossColumn(c.ColumnName, c.DataType, size, prec, scale, true);
    }

    private static string FriendlySourceType(DataColumn c)
    {
        var t = c.DataType;
        if (t == typeof(string)) return c.MaxLength > 0 ? $"String({c.MaxLength})" : "String";
        if (t == typeof(decimal))
        {
            var prec = c.ExtendedProperties["prec"] is int p ? p : 0;
            var scale = c.ExtendedProperties["scale"] is int s ? s : 0;
            return prec > 0 ? $"Decimal({prec},{scale})" : "Decimal";
        }
        return t.Name; // Int32, Int16, Byte, Double, Single, DateTime, TimeSpan, Byte[]
    }

    /// <summary>
    /// Reads a Clarion file and proposes a SQL type for each column. Columns that look like Clarion
    /// dates/times (stored as LONGs) are pre-mapped to SQL <c>date</c>/<c>time</c> so the copy
    /// converts them; everything else gets the default type mapping. Callers can show this to the
    /// user, let them tweak the target types, and pass the result back to <see cref="CopyCrossAsync"/>.
    /// </summary>
    public static async Task<List<ClarionColumnMap>> ProposeClarionMappingAsync(
        ConnectionProfile src, string srcName, DatabaseEngine targetEngine)
    {
        // Sample some rows so ClarionDetector can spot LONG date/time columns by value + name.
        var table = await Task.Run(() => ReadClarion(src, srcName, 1000));
        var clarion = ClarionDetector.Detect(table);

        return table.Columns.Cast<DataColumn>().Select(c =>
        {
            var source = FriendlySourceType(c);
            string target;
            if (IsIntegral(c.DataType) && clarion.TryGetValue(c.ColumnName, out var kind) && kind != ClarionKind.Timestamp)
            {
                target = kind == ClarionKind.Time ? "time" : "date";
                source += $"  ·  Clarion {kind}";
            }
            else
            {
                target = MapType(targetEngine, ToCrossColumn(c));
            }
            return new ClarionColumnMap(c.ColumnName, source, target);
        }).ToList();
    }

    private static async Task CopyClarionFileCrossAsync(
        ConnectionProfile src, string srcName,
        ConnectionProfile tgt, string tgtDb, string tgtSchema, string newName, bool includeData,
        IReadOnlyList<ClarionColumnMap>? mappings = null)
    {
        if (!IsRelational(tgt.Engine))
            throw new NotSupportedException($"A Clarion file can only be copied into a SQL database, not {tgt.Engine.DisplayName()}.");

        // Decode the file (structure-only when the user picked "structure only").
        var table = await Task.Run(() => ReadClarion(src, srcName, includeData ? int.MaxValue : 0));

        var cols = table.Columns.Cast<DataColumn>().ToList();
        if (cols.Count == 0)
            throw new InvalidOperationException($"'{srcName}' has no readable columns.");

        // Target type per column: the user's override if supplied, else the default auto-mapping.
        var overrides = mappings?.ToDictionary(m => m.Name, m => m.TargetType, StringComparer.OrdinalIgnoreCase);
        string TargetType(DataColumn c) =>
            overrides is not null && overrides.TryGetValue(c.ColumnName, out var t) && !string.IsNullOrWhiteSpace(t)
                ? t.Trim()
                : MapType(tgt.Engine, ToCrossColumn(c));

        // Structure (all columns nullable — Clarion has no NULL concept and no primary key here).
        var lines = cols.Select(c => $"  {Q(tgt.Engine, c.ColumnName)} {TargetType(c)}").ToList();
        var ddl = $"CREATE TABLE {Fq(tgt.Engine, tgtSchema, newName)} (\n{string.Join(",\n", lines)}\n)";
        await ExecuteAsync(tgt, tgtDb, ddl);

        if (!includeData) return;

        // When a Clarion LONG (integer) column is mapped to a date/time SQL type, convert the raw
        // Clarion value into a real DateTime/TimeSpan so it lands in the temporal column.
        var pump = ConvertClarionTemporalColumns(table, c => TargetType(c), tgt.Engine);

        using var reader = pump.CreateDataReader();
        if (tgt.Engine == DatabaseEngine.SqlServer)
            await BulkCopyReaderToSqlServerAsync(reader, tgt, tgtDb, tgtSchema, newName);
        else
            await PumpReaderAsync(reader, tgt, tgtDb, tgtSchema, newName);
    }

    private enum Temporal { None, Date, Time }

    /// <summary>Classifies a SQL type string as a date/datetime, a time, or neither.</summary>
    private static Temporal ClassifyTemporal(string sqlType, DatabaseEngine target)
    {
        var t = sqlType.Trim().ToLowerInvariant();
        var paren = t.IndexOf('(');
        if (paren >= 0) t = t[..paren].Trim();

        if (t is "time") return Temporal.Time;
        if (t is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset") return Temporal.Date;
        // 'timestamp' is a datetime in MySQL/MariaDB/Firebird/Postgres, but a binary rowversion in SQL Server.
        if (t is "timestamp" && target != DatabaseEngine.SqlServer) return Temporal.Date;
        return Temporal.None;
    }

    private static bool IsIntegral(Type t) =>
        t == typeof(int) || t == typeof(short) || t == typeof(long) || t == typeof(byte);

    /// <summary>
    /// Returns a copy of <paramref name="source"/> in which integer columns the user mapped to a
    /// date/time SQL type are re-typed to DateTime/TimeSpan and decoded from Clarion Standard
    /// Date/Time values. Columns that need no conversion are carried over unchanged. If nothing
    /// needs converting, the original table is returned untouched.
    /// </summary>
    private static DataTable ConvertClarionTemporalColumns(
        DataTable source, Func<DataColumn, string> targetType, DatabaseEngine target)
    {
        // Decide each column's conversion: 0 = none, 1 = Clarion date, 2 = Clarion time.
        var convert = new int[source.Columns.Count];
        var any = false;
        for (var i = 0; i < source.Columns.Count; i++)
        {
            var c = source.Columns[i];
            if (!IsIntegral(c.DataType)) continue;
            switch (ClassifyTemporal(targetType(c), target))
            {
                case Temporal.Date: convert[i] = 1; any = true; break;
                case Temporal.Time: convert[i] = 2; any = true; break;
            }
        }
        if (!any) return source;

        var result = new DataTable(source.TableName);
        for (var i = 0; i < source.Columns.Count; i++)
        {
            var c = source.Columns[i];
            var type = convert[i] switch { 1 => typeof(DateTime), 2 => typeof(TimeSpan), _ => c.DataType };
            result.Columns.Add(c.ColumnName, type);
        }

        foreach (DataRow sr in source.Rows)
        {
            var dr = result.NewRow();
            for (var i = 0; i < source.Columns.Count; i++)
            {
                var v = sr[i];
                if (convert[i] == 0) { dr[i] = v; continue; }
                if (v is null or DBNull) { dr[i] = DBNull.Value; continue; }

                var n = Convert.ToInt64(v);
                object? converted = convert[i] == 1 ? ClarionDate.FromClarion(n) : ClarionTime.ToTimeSpan(n);
                dr[i] = converted ?? (object)DBNull.Value;
            }
            result.Rows.Add(dr);
        }
        result.AcceptChanges();
        return result;
    }

    /// <summary>Bulk-loads an already-open reader into a SQL Server target.</summary>
    private static async Task BulkCopyReaderToSqlServerAsync(
        DbDataReader reader, ConnectionProfile tgt, string tgtDb, string tgtSchema, string newName)
    {
        await using var tgtConn = (SqlConnection)await OpenAsync(tgt, tgtDb);
        using var bulk = new SqlBulkCopy(tgtConn)
        {
            DestinationTableName = $"{Q(DatabaseEngine.SqlServer, tgtSchema)}.{Q(DatabaseEngine.SqlServer, newName)}",
            BulkCopyTimeout = 0,
            BatchSize = 10_000,
            EnableStreaming = true
        };
        for (var i = 0; i < reader.FieldCount; i++)
            bulk.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));
        await bulk.WriteToServerAsync(reader);
    }

    /// <summary>Streams an already-open reader into a non-SQL-Server target via one prepared INSERT.</summary>
    private static async Task PumpReaderAsync(
        DbDataReader reader, ConnectionProfile tgt, string tgtDb, string tgtSchema, string newName)
    {
        var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        if (cols.Count == 0) return;

        await using var tgtConn = await OpenAsync(tgt, tgtDb);
        await using var tx = await tgtConn.BeginTransactionAsync();

        var oracle = tgt.Engine == DatabaseEngine.Oracle;
        var ph = oracle ? ":" : "@";
        var colList = string.Join(", ", cols.Select(c => Q(tgt.Engine, c)));
        var paramList = string.Join(", ", cols.Select((_, i) => $"{ph}p{i}"));
        await using var insert = tgtConn.CreateCommand();
        if (insert is Oracle.ManagedDataAccess.Client.OracleCommand oc) oc.BindByName = true;
        insert.Transaction = (DbTransaction)tx;
        insert.CommandText = $"INSERT INTO {Fq(tgt.Engine, tgtSchema, newName)} ({colList}) VALUES ({paramList})";

        var ps = new DbParameter[cols.Count];
        for (var i = 0; i < cols.Count; i++)
        {
            var p = insert.CreateParameter();
            p.ParameterName = (oracle ? "p" : "@p") + i;
            insert.Parameters.Add(p);
            ps[i] = p;
        }

        while (await reader.ReadAsync())
        {
            for (var i = 0; i < cols.Count; i++)
                ps[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            await insert.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private static async Task CopyMongoCrossAsync(
        ConnectionProfile src, string srcDb, string srcName,
        ConnectionProfile tgt, string tgtDb, string newName, bool includeData)
    {
        var tgtDatabase = new MongoClient(tgt.BuildConnectionString()).GetDatabase(tgtDb);
        if (!includeData)
        {
            await tgtDatabase.CreateCollectionAsync(newName);
            return;
        }

        var source = new MongoClient(src.BuildConnectionString()).GetDatabase(srcDb).GetCollection<BsonDocument>(srcName);
        var target = tgtDatabase.GetCollection<BsonDocument>(newName);

        using var cursor = await source.FindAsync(new BsonDocument());
        var any = false;
        while (await cursor.MoveNextAsync())
        {
            var batch = cursor.Current.ToList();
            if (batch.Count == 0) continue;
            any = true;
            await target.InsertManyAsync(batch);
        }
        if (!any)
        {
            var names = await (await tgtDatabase.ListCollectionNamesAsync()).ToListAsync();
            if (!names.Contains(newName)) await tgtDatabase.CreateCollectionAsync(newName);
        }
    }
}
