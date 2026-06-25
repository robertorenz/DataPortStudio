using System.IO;
using System.Text.Json;

namespace DataPortStudio.Services;

/// <summary>
/// Persists per-table "row identity" column choices for keyless tables to
/// %AppData%\DataPortStudio\rowidentity.json. Keyed by connection + database + schema + table.
/// </summary>
public class RowIdentityStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataPortStudio");
    private static readonly string FilePath = Path.Combine(Dir, "rowidentity.json");

    public static string MakeKey(Guid connectionId, string? database, string? schema, string table)
        => $"{connectionId}|{database}|{schema}|{table}";

    public List<string>? Get(string key)
    {
        var all = Load();
        return all.TryGetValue(key, out var cols) && cols.Count > 0 ? cols : null;
    }

    public void Set(string key, List<string>? columns)
    {
        var all = Load();
        if (columns is null || columns.Count == 0) all.Remove(key);
        else all[key] = columns;
        Save(all);
    }

    private static Dictionary<string, List<string>> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(Dictionary<string, List<string>> all)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort persistence
        }
    }
}
