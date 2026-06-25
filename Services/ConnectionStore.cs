using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>
/// Persists connection profiles to %AppData%\DataPortStudio\connections.json.
/// Passwords are encrypted at rest with Windows DPAPI (current-user scope).
/// </summary>
public class ConnectionStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataPortStudio");
    private static readonly string FilePath = Path.Combine(Dir, "connections.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<ConnectionProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ConnectionProfile>();
            var json = File.ReadAllText(FilePath);
            var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? new();
            foreach (var p in profiles)
                p.Password = Unprotect(p.Password);
            return profiles;
        }
        catch
        {
            return new List<ConnectionProfile>();
        }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(Dir);

        // Serialize copies so we never expose the encrypted blob to the live UI objects.
        var copies = profiles.Select(p =>
        {
            var c = p.Clone();
            c.Password = Protect(p.Password);
            return c;
        }).ToList();

        File.WriteAllText(FilePath, JsonSerializer.Serialize(copies, JsonOptions));
    }

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return encrypted;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // Not decryptable (e.g. different user / corrupt) — treat as no password.
        }
    }
}
