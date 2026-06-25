using System.IO;
using System.Text.Json;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>Loads/saves <see cref="AppSettings"/> to %AppData%\DataPortStudio\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataPortStudio");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static AppSettings? _current;

    /// <summary>The active settings (loaded once, refreshed on Save).</summary>
    public static AppSettings Current => _current ??= Load();

    public static void Save(AppSettings settings)
    {
        _current = settings;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort persistence
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
