using System.IO;
using System.Windows;

namespace DataPortStudio.Services;

/// <summary>Loads the bundled user-guide HTML (English / Spanish) packaged as WPF resources.</summary>
public static class HelpDocs
{
    /// <summary>Returns the user-guide HTML for a language ("es" → Spanish, anything else → English).</summary>
    public static string GetHtml(string language)
    {
        var lang = string.Equals(language, "es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
        return Read($"Docs/help.{lang}.html") ?? Read("Docs/help.en.html") ?? FallbackHtml;
    }

    private static string? Read(string relativePath)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info is null) return null;
            using var reader = new StreamReader(info.Stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private const string FallbackHtml =
        "<html><body style='font-family:Segoe UI;padding:24px'>" +
        "<h2>DataPortStudio — User Guide</h2><p>The bundled documentation could not be loaded.</p></body></html>";
}
