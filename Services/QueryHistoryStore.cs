using System.IO;
using System.Text.Json;

namespace DataPortStudio.Services;

public static class QueryHistoryStore
{
    public const int MaxPerConnection = 50;

    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DataPortStudio", "query_history.json");

    private static Dictionary<string, List<string>>? _cache;

    private static Dictionary<string, List<string>> Data =>
        _cache ??= Load();

    public static void Add(string key, string sql)
    {
        sql = sql.Trim();
        if (string.IsNullOrWhiteSpace(sql)) return;

        if (!Data.TryGetValue(key, out var list))
            Data[key] = list = new();

        list.Remove(sql);
        list.Insert(0, sql);
        if (list.Count > MaxPerConnection) list.RemoveRange(MaxPerConnection, list.Count - MaxPerConnection);

        Save();
    }

    public static IReadOnlyList<string> Get(string key) =>
        Data.TryGetValue(key, out var list) ? list : [];

    private static Dictionary<string, List<string>> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return new();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_cache,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
