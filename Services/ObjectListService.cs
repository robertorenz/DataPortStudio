using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using MongoDB.Driver;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>One row in the Navicat-style object list.</summary>
public sealed record ObjectListItem(string Name, long? Rows, DateTime? Modified, string? Comment, string? Schema = null);

/// <summary>Loads the tables/collections of a container with row counts (and dates/comments where available).</summary>
public static class ObjectListService
{
    public static Task<List<ObjectListItem>> LoadTablesAsync(ConnectionProfile p, string database, string schema)
        => p.Engine switch
        {
            DatabaseEngine.SqlServer => LoadSqlServerAsync(p, database, schema),
            DatabaseEngine.Sqlite => LoadSqliteAsync(p),
            DatabaseEngine.Firebird => LoadFirebirdAsync(p),
            DatabaseEngine.MongoDb => LoadMongoAsync(p, database),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => LoadMySqlAsync(p, database),
            DatabaseEngine.Tps => LoadClarionFilesAsync(p, ".tps", TpsService.ListTables(p.FilePath)),
            DatabaseEngine.ClarionDat => LoadClarionFilesAsync(p, ".dat", DatService.ListTables(p.FilePath)),
            DatabaseEngine.Oracle => LoadOracleAsync(p),
            _ => Task.FromResult(new List<ObjectListItem>())
        };

    private static async Task<List<ObjectListItem>> LoadOracleAsync(ConnectionProfile p)
    {
        var cs = p.BuildConnectionString();
        var names = await OracleService.GetTablesAsync(cs);
        var result = new List<ObjectListItem>();
        foreach (var n in names)
        {
            long? rows = null;
            try { rows = await OracleService.GetRowCountAsync(cs, n); } catch { /* best effort */ }
            result.Add(new ObjectListItem(n, rows, null, null));
        }
        return result;
    }

    /// <summary>Clarion flat files: each file in the connection's folder, with its size as a comment.</summary>
    private static Task<List<ObjectListItem>> LoadClarionFilesAsync(ConnectionProfile p, string ext, List<string> names)
    {
        var folder = p.FilePath;
        var result = names.Select(n =>
        {
            DateTime? modified = null;
            string? size = null;
            try
            {
                var fi = new FileInfo(System.IO.Path.Combine(folder!, n + ext));
                if (fi.Exists) { modified = fi.LastWriteTime; size = FormatSize(fi.Length); }
            }
            catch { /* best effort */ }
            return new ObjectListItem(n, null, modified, size);
        }).ToList();
        return Task.FromResult(result);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:N0} KB";
        var mb = kb / 1024.0;
        return mb < 1024 ? $"{mb:N1} MB" : $"{mb / 1024.0:N2} GB";
    }

    private static async Task<List<ObjectListItem>> LoadMySqlAsync(ConnectionProfile p, string database)
    {
        var cs = p.BuildConnectionString();
        var names = await MySqlService.GetTablesAsync(cs, database);
        var result = new List<ObjectListItem>();
        foreach (var n in names)
        {
            long? rows = null;
            try { rows = await MySqlService.GetRowCountAsync(cs, database, n); } catch { }
            result.Add(new ObjectListItem(n, rows, null, null));
        }
        return result;
    }

    /// <summary>Lists views / functions / procedures by name. kind = "view" | "function" | "procedure".</summary>
    public static async Task<List<ObjectListItem>> LoadNamesAsync(ConnectionProfile p, string database, string schema, string kind)
    {
        var cs = p.BuildConnectionString();
        List<string> names = p.Engine switch
        {
            DatabaseEngine.SqlServer => kind switch
            {
                "view" => await SqlServerService.GetViewsAsync(cs, database, schema),
                "function" => await SqlServerService.GetFunctionsAsync(cs, database, schema),
                "procedure" => await SqlServerService.GetProceduresAsync(cs, database, schema),
                _ => new()
            },
            DatabaseEngine.Sqlite => kind == "view" ? await SqliteService.GetViewsAsync(cs) : new(),
            DatabaseEngine.Firebird => kind == "view" ? await FirebirdService.GetViewsAsync(cs) : new(),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => kind switch
            {
                "view" => await MySqlService.GetViewsAsync(cs, database),
                "function" => await MySqlService.GetFunctionsAsync(cs, database),
                "procedure" => await MySqlService.GetProceduresAsync(cs, database),
                _ => new()
            },
            DatabaseEngine.Oracle => kind switch
            {
                "view" => await OracleService.GetViewsAsync(cs),
                "function" => await OracleService.GetFunctionsAsync(cs),
                "procedure" => await OracleService.GetProceduresAsync(cs),
                _ => new()
            },
            _ => new()
        };
        return names.Select(n => new ObjectListItem(n, null, null, null, schema)).ToList();
    }

    private static async Task<List<ObjectListItem>> LoadSqlServerAsync(ConnectionProfile p, string database, string schema)
    {
        var allSchemas = string.IsNullOrEmpty(schema);
        var result = new List<ObjectListItem>();
        await using var conn = new SqlConnection(SqlServerService.WithDatabase(p.BuildConnectionString(), database));
        await conn.OpenAsync();
        var sql = @"
            SELECT s.name AS sch, t.name,
                   (SELECT SUM(pp.rows) FROM sys.partitions pp
                    WHERE pp.object_id = t.object_id AND pp.index_id IN (0,1)) AS rows,
                   t.modify_date,
                   CAST(ep.value AS nvarchar(4000)) AS comment
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.extended_properties ep
                   ON ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.class = 1 AND ep.name = 'MS_Description'
            " + (allSchemas ? "" : "WHERE s.name = @schema") + @"
            ORDER BY t.name";
        await using var cmd = new SqlCommand(sql, conn);
        if (!allSchemas) cmd.Parameters.AddWithValue("@schema", schema);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            result.Add(new ObjectListItem(
                r.GetString(1),
                r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2)),
                r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(0)));
        }
        return result;
    }

    private static async Task<List<ObjectListItem>> LoadSqliteAsync(ConnectionProfile p)
    {
        var cs = p.BuildConnectionString();
        var names = await SqliteService.GetTablesAsync(cs);
        var result = new List<ObjectListItem>();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        foreach (var n in names)
        {
            long? rows = null;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {SqliteService.Quote(n)}";
                rows = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            catch { /* counting is best-effort */ }
            result.Add(new ObjectListItem(n, rows, null, null));
        }
        return result;
    }

    private static async Task<List<ObjectListItem>> LoadFirebirdAsync(ConnectionProfile p)
    {
        var cs = p.BuildConnectionString();
        var names = await FirebirdService.GetTablesAsync(cs);
        var result = new List<ObjectListItem>();
        foreach (var n in names)
        {
            long? rows = null;
            try { rows = await FirebirdService.GetRowCountAsync(cs, n); } catch { /* best effort */ }
            result.Add(new ObjectListItem(n, rows, null, null));
        }
        return result;
    }

    private static async Task<List<ObjectListItem>> LoadMongoAsync(ConnectionProfile p, string database)
    {
        var db = new MongoClient(p.BuildConnectionString()).GetDatabase(database);
        var names = await (await db.ListCollectionNamesAsync()).ToListAsync();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var result = new List<ObjectListItem>();
        foreach (var n in names)
        {
            long? count = null;
            try { count = await db.GetCollection<BsonDocument>(n).EstimatedDocumentCountAsync(); } catch { }
            result.Add(new ObjectListItem(n, count, null, null));
        }
        return result;
    }
}
