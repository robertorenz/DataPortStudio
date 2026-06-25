using System.Data.Common;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

public record ColumnInfo(string Name, string DataType, bool IsNullable);

public record TableInfo(string Name, IReadOnlyList<ColumnInfo> Columns);

public enum DiffKind { OnlyInLeft, OnlyInRight, ColumnsDiffer }

public record ColumnDiff(string Name, ColumnInfo? Left, ColumnInfo? Right);

public record TableDiff(DiffKind Kind, string TableName, IReadOnlyList<ColumnDiff> ColumnDiffs);

public static class SchemaDiffService
{
    public static async Task<List<TableDiff>> CompareAsync(
        ConnectionProfile connection, string dbLeft, string dbRight, string schema = "dbo")
    {
        var left  = await LoadAsync(connection, dbLeft,  schema);
        var right = await LoadAsync(connection, dbRight, schema);
        return Diff(left, right);
    }

    public static async Task<List<string>> GetDatabasesAsync(ConnectionProfile connection)
    {
        var cs  = connection.BuildConnectionString();
        var sql = connection.Engine switch
        {
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME",
            DatabaseEngine.Sqlite => null,
            _ => "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name"
        };
        if (sql is null) return [];

        var list = new List<string>();
        await using var conn = OpenConnection(connection, cs, null);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private static async Task<Dictionary<string, TableInfo>> LoadAsync(
        ConnectionProfile connection, string database, string schema)
    {
        var cs = connection.BuildConnectionString();
        var sql = connection.Engine switch
        {
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                $"SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE " +
                $"FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{database}' " +
                $"ORDER BY TABLE_NAME, ORDINAL_POSITION",
            _ =>
                $"SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE " +
                $"FROM INFORMATION_SCHEMA.COLUMNS c " +
                $"JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_SCHEMA = c.TABLE_SCHEMA " +
                $"WHERE c.TABLE_SCHEMA = '{schema}' AND t.TABLE_TYPE = 'BASE TABLE' " +
                $"ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION"
        };

        var dict = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        await using var conn = OpenConnection(connection, cs, database);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var tbl  = r.GetString(0);
            var col  = r.GetString(1);
            var type = r.GetString(2);
            var nul  = r.GetString(3).Equals("YES", StringComparison.OrdinalIgnoreCase);
            if (!dict.ContainsKey(tbl)) dict[tbl] = [];
            dict[tbl].Add(new ColumnInfo(col, type, nul));
        }
        return dict.ToDictionary(kv => kv.Key, kv => (TableInfo)new TableInfo(kv.Key, kv.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static DbConnection OpenConnection(ConnectionProfile connection, string cs, string? database)
    {
        return connection.Engine switch
        {
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                new MySqlConnection(string.IsNullOrEmpty(database) ? cs : MySqlService.WithDatabase(cs, database)),
            DatabaseEngine.Sqlite => new SqliteConnection(cs),
            DatabaseEngine.Firebird => new FbConnection(cs),
            _ => new SqlConnection(string.IsNullOrEmpty(database) ? cs : SqlServerService.WithDatabase(cs, database))
        };
    }

    private static List<TableDiff> Diff(
        Dictionary<string, TableInfo> left, Dictionary<string, TableInfo> right)
    {
        var result = new List<TableDiff>();

        foreach (var name in left.Keys.Except(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            result.Add(new(DiffKind.OnlyInLeft, name, []));

        foreach (var name in right.Keys.Except(left.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            result.Add(new(DiffKind.OnlyInRight, name, []));

        foreach (var name in left.Keys.Intersect(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            var lt = left[name]; var rt = right[name];
            var diffs = new List<ColumnDiff>();

            var lCols = lt.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var rCols = rt.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var c in lCols.Keys.Except(rCols.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x=>x))
                diffs.Add(new(c, lCols[c], null));

            foreach (var c in rCols.Keys.Except(lCols.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x=>x))
                diffs.Add(new(c, null, rCols[c]));

            foreach (var c in lCols.Keys.Intersect(rCols.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x=>x))
            {
                var l = lCols[c]; var r2 = rCols[c];
                if (!l.DataType.Equals(r2.DataType, StringComparison.OrdinalIgnoreCase) ||
                    l.IsNullable != r2.IsNullable)
                    diffs.Add(new(c, l, r2));
            }

            if (diffs.Count > 0)
                result.Add(new(DiffKind.ColumnsDiffer, name, diffs));
        }
        return result;
    }
}
