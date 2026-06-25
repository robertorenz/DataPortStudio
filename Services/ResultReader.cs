using System.Data;
using System.Data.Common;

namespace DataPortStudio.Services;

/// <summary>Loads a query result into a DataTable safely: de-duplicates column names
/// (so <c>SELECT *</c> across joined tables works) and caps the row count.</summary>
public static class ResultReader
{
    public const int DefaultRowCap = 100_000;

    public static async Task<(DataTable Table, bool Truncated)> LoadAsync(DbDataReader reader, int maxRows = DefaultRowCap)
    {
        var table = new DataTable();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrEmpty(name)) name = "Column" + (i + 1);
            var unique = name;
            var n = 2;
            while (!used.Add(unique)) unique = $"{name}_{n++}";

            Type type;
            try { type = reader.GetFieldType(i) ?? typeof(object); }
            catch { type = typeof(object); }
            table.Columns.Add(unique, type);
        }

        var truncated = false;
        var count = 0;
        while (await reader.ReadAsync())
        {
            if (count >= maxRows) { truncated = true; break; }
            count++;
            var row = table.NewRow();
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            table.Rows.Add(row);
        }
        return (table, truncated);
    }
}
