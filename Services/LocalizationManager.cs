using System.ComponentModel;

namespace DataPortStudio.Services;

/// <summary>
/// Runtime-switchable UI string provider. XAML binds to the indexer via the
/// <c>{loc:Tr Key}</c> markup extension; changing <see cref="Language"/> raises a
/// change for the indexer so every bound string refreshes live.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private string _language = "en";

    private LocalizationManager() { }

    /// <summary>Two-letter language code: "en" or "es".</summary>
    public string Language
    {
        get => _language;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "en" : value.ToLowerInvariant();
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    /// <summary>Localized string for a key (falls back to English, then the key itself).</summary>
    public string this[string key] => Strings.Get(key, _language);

    /// <summary>Convenience for code-behind / view-models.</summary>
    public string T(string key) => this[key];

    public event PropertyChangedEventHandler? PropertyChanged;
}
