using System.IO;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

public sealed record SchemaEndpoint(ConnectionProfile Connection, string Database, string Schema)
{
    public string DisplayName => $"{Connection.Name} / {Database}" +
        (string.IsNullOrWhiteSpace(Schema) || Schema.Equals(Database, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" / {Schema}");
}

public record ColumnInfo(string Name, string DataType, bool IsNullable, int Ordinal);

public enum SchemaObjectType { Table, View, Function, Procedure }

public static class SchemaObjectTypeInfo
{
    public static string DisplayName(this SchemaObjectType type) => type switch
    {
        SchemaObjectType.Table => "Table",
        SchemaObjectType.View => "View",
        SchemaObjectType.Function => "Function",
        SchemaObjectType.Procedure => "Procedure",
        _ => type.ToString()
    };
}

public record TableInfo(string Name, IReadOnlyList<ColumnInfo> Columns,
    SchemaObjectType ObjectType = SchemaObjectType.Table, string? Definition = null);

public enum DiffKind { OnlyInLeft, OnlyInRight, ColumnsDiffer, DefinitionDiffers }

public record ColumnDiff(string Name, ColumnInfo? Left, ColumnInfo? Right, bool OrderDiffers = false);

public record TableDiff(DiffKind Kind, string TableName, IReadOnlyList<ColumnDiff> ColumnDiffs,
    SchemaObjectType ObjectType = SchemaObjectType.Table,
    string? LeftDefinition = null, string? RightDefinition = null);

/// <summary>
/// Loads and compares table metadata from independent relational endpoints.  An endpoint can be a
/// different database on the same server, a different saved connection, or a different SQL engine.
/// </summary>
public static class SchemaDiffService
{
    public static bool IsSupported(ConnectionProfile connection) =>
        TableCopyService.IsRelational(connection.Engine);

    public static async Task<List<TableDiff>> CompareAsync(
        SchemaEndpoint left, SchemaEndpoint right, bool respectColumnOrder = false,
        IReadOnlyCollection<SchemaObjectType>? objectTypes = null)
    {
        var requested = (objectTypes ?? [SchemaObjectType.Table]).ToHashSet();
        var loads = await Task.WhenAll(LoadAsync(left, requested), LoadAsync(right, requested));
        return Diff(loads[0], loads[1], respectColumnOrder);
    }

    // Kept for callers using the original same-connection API.
    public static Task<List<TableDiff>> CompareAsync(
        ConnectionProfile connection, string dbLeft, string dbRight, string schema = "dbo") =>
        CompareAsync(new(connection, dbLeft, schema), new(connection, dbRight, schema));

    public static async Task<List<string>> GetDatabasesAsync(ConnectionProfile connection)
    {
        var cs = connection.BuildConnectionString();
        var databases = connection.Engine switch
        {
            DatabaseEngine.SqlServer => await SqlServerService.GetDatabasesAsync(cs),
            DatabaseEngine.PostgreSql => await PostgresService.GetDatabasesAsync(cs),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => await MySqlService.GetDatabasesAsync(cs),
            DatabaseEngine.Sqlite => SingleDatabase(connection, "main"),
            DatabaseEngine.Firebird => SingleDatabase(connection, "Firebird"),
            DatabaseEngine.Oracle => SingleDatabase(connection, connection.Database ?? "Oracle"),
            _ => throw new NotSupportedException(
                $"Schema comparison is not available for {connection.Engine.DisplayName()}.")
        };

        return databases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<List<string>> GetSchemasAsync(ConnectionProfile connection, string database)
    {
        var cs = connection.BuildConnectionString();
        var schemas = connection.Engine switch
        {
            DatabaseEngine.SqlServer => await SqlServerService.GetSchemasAsync(cs, database),
            DatabaseEngine.PostgreSql => await PostgresService.GetSchemasAsync(cs, database),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => [database],
            DatabaseEngine.Sqlite => ["main"],
            DatabaseEngine.Firebird => [""],
            DatabaseEngine.Oracle => [connection.Username?.ToUpperInvariant() ?? ""],
            _ => []
        };

        // Empty databases are valid and should still provide a useful schema choice.
        if (schemas.Count == 0 && connection.Engine == DatabaseEngine.SqlServer) schemas.Add("dbo");
        if (schemas.Count == 0 && connection.Engine == DatabaseEngine.PostgreSql) schemas.Add("public");
        return schemas;
    }

    private static List<string> SingleDatabase(ConnectionProfile connection, string fallback)
    {
        var value = connection.Engine is DatabaseEngine.Sqlite or DatabaseEngine.Firebird
            ? Path.GetFileName(connection.FilePath)
            : connection.Database;
        return [string.IsNullOrWhiteSpace(value) ? fallback : value];
    }

    private static async Task<Dictionary<string, TableInfo>> LoadAsync(
        SchemaEndpoint endpoint, IReadOnlySet<SchemaObjectType> objectTypes)
    {
        var p = endpoint.Connection;
        var cs = p.BuildConnectionString();
        var tables = objectTypes.Contains(SchemaObjectType.Table) ? p.Engine switch
        {
            DatabaseEngine.SqlServer => await SqlServerService.GetTablesAsync(cs, endpoint.Database, endpoint.Schema),
            DatabaseEngine.PostgreSql => await PostgresService.GetTablesAsync(cs, endpoint.Database, endpoint.Schema),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => await MySqlService.GetTablesAsync(cs, endpoint.Database),
            DatabaseEngine.Sqlite => await SqliteService.GetTablesAsync(cs),
            DatabaseEngine.Firebird => await FirebirdService.GetTablesAsync(cs),
            DatabaseEngine.Oracle => await OracleService.GetTablesAsync(cs),
            _ => throw new NotSupportedException(
                $"Schema comparison is not available for {p.Engine.DisplayName()}.")
        } : [];

        var result = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            IReadOnlyList<ColumnInfo> columns = p.Engine switch
            {
                DatabaseEngine.SqlServer => (await SqlServerService.GetColumnDetailsAsync(
                        cs, endpoint.Database, endpoint.Schema, table))
                    .Select((c, i) => new ColumnInfo(c.Name, SqlServerType(c), c.Nullable, i + 1)).ToList(),
                DatabaseEngine.PostgreSql => (await PostgresService.GetColumnsAsync(
                        cs, endpoint.Database, endpoint.Schema, table))
                    .Select((c, i) => new ColumnInfo(c.Name, c.TypeName, c.Nullable, i + 1)).ToList(),
                DatabaseEngine.MySql or DatabaseEngine.MariaDb => (await MySqlService.GetColumnsAsync(
                        cs, endpoint.Database, table))
                    .Select((c, i) => new ColumnInfo(c.Name, c.TypeName, c.Nullable, i + 1)).ToList(),
                DatabaseEngine.Sqlite => (await SqliteService.GetColumnDetailsAsync(cs, table))
                    .Select((c, i) => new ColumnInfo(c.Name, c.Type, !c.NotNull, i + 1)).ToList(),
                DatabaseEngine.Firebird => (await FirebirdService.GetColumnsAsync(cs, table))
                    .Select((c, i) => new ColumnInfo(c.Name, c.TypeName, c.Nullable, i + 1)).ToList(),
                DatabaseEngine.Oracle => (await OracleService.GetColumnsAsync(cs, table))
                    .Select((c, i) => new ColumnInfo(c.Name, c.TypeName, c.Nullable, i + 1)).ToList(),
                _ => []
            };
            result[ObjectKey(SchemaObjectType.Table, table)] = new TableInfo(table, columns);
        }

        var programmable = await SchemaObjectMetadataService.LoadAsync(endpoint, objectTypes);
        foreach (var obj in programmable)
            result[ObjectKey(obj.Type, obj.Name)] = new TableInfo(obj.Name, [], obj.Type, obj.Definition);
        return result;
    }

    private static string ObjectKey(SchemaObjectType type, string name) => $"{(int)type}:{name}";

    private static string SqlServerType(SqlServerService.ColumnDetail c)
    {
        var type = c.TypeName.ToLowerInvariant();
        if (type is "varchar" or "char" or "varbinary" or "binary")
            return $"{type}({(c.MaxLength == -1 ? "max" : c.MaxLength)})";
        if (type is "nvarchar" or "nchar")
            return $"{type}({(c.MaxLength == -1 ? "max" : c.MaxLength / 2)})";
        if (type is "decimal" or "numeric") return $"{type}({c.Precision},{c.Scale})";
        if (type is "datetime2" or "datetimeoffset" or "time") return $"{type}({c.Scale})";
        return type;
    }

    internal static List<TableDiff> Diff(
        Dictionary<string, TableInfo> left,
        Dictionary<string, TableInfo> right,
        bool respectColumnOrder)
    {
        var result = new List<TableDiff>();

        foreach (var key in left.Keys.Except(right.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => left[x].ObjectType).ThenBy(x => left[x].Name))
        {
            var obj = left[key];
            result.Add(new(DiffKind.OnlyInLeft, obj.Name, [], obj.ObjectType,
                LeftDefinition: obj.Definition));
        }

        foreach (var key in right.Keys.Except(left.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => right[x].ObjectType).ThenBy(x => right[x].Name))
        {
            var obj = right[key];
            result.Add(new(DiffKind.OnlyInRight, obj.Name, [], obj.ObjectType,
                RightDefinition: obj.Definition));
        }

        foreach (var key in left.Keys.Intersect(right.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => left[x].ObjectType).ThenBy(x => left[x].Name))
        {
            var lt = left[key];
            var rt = right[key];
            if (lt.ObjectType != SchemaObjectType.Table)
            {
                if (!DefinitionsEqual(lt.Definition, rt.Definition))
                    result.Add(new(DiffKind.DefinitionDiffers, lt.Name, [], lt.ObjectType,
                        lt.Definition, rt.Definition));
                continue;
            }

            var diffs = new List<ColumnDiff>();
            var lCols = lt.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var rCols = rt.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

            // Follow physical order so positional differences are easy to understand in the result.
            foreach (var c in lt.Columns.Where(c => !rCols.ContainsKey(c.Name)))
                diffs.Add(new(c.Name, c, null));
            foreach (var c in rt.Columns.Where(c => !lCols.ContainsKey(c.Name)))
                diffs.Add(new(c.Name, null, c));
            foreach (var c in lt.Columns.Where(c => rCols.ContainsKey(c.Name)))
            {
                var l = c;
                var r = rCols[c.Name];
                var orderDiffers = respectColumnOrder && l.Ordinal != r.Ordinal;
                if (!NormalizeType(l.DataType).Equals(NormalizeType(r.DataType), StringComparison.OrdinalIgnoreCase) ||
                    l.IsNullable != r.IsNullable || orderDiffers)
                    diffs.Add(new(c.Name, l, r, orderDiffers));
            }

            if (diffs.Count > 0)
                result.Add(new(DiffKind.ColumnsDiffer, lt.Name, diffs));
        }
        return result;
    }

    private static string NormalizeType(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    private static bool DefinitionsEqual(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        static string Normalize(string value)
        {
            var normalized = value.Replace("\r\n", "\n").Trim();
            const string identifier = "(?:\\[[^\\]]+\\]|\"[^\"]+\"|`[^`]+`|[\\w@$#]+)";
            return System.Text.RegularExpressions.Regex.Replace(normalized,
                $@"^\s*(?:CREATE(?:\s+OR\s+(?:ALTER|REPLACE))?|ALTER)\s+(VIEW|FUNCTION|PROC(?:EDURE)?)\s+{identifier}(?:\s*\.\s*{identifier})?",
                "CREATE $1 __object__",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }
        return Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    }
}
