using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace DataPortStudio.Services;

/// <summary>Helpers for rendering a cell's content as hex, image, or detecting its type.</summary>
public static class CellContent
{
    private const int MaxHexBytes = 256 * 1024;

    public static bool LooksLikeImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4) return false;
        // PNG
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;
        // JPEG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;
        // GIF
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return true;
        // BMP
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return true;
        // WEBP (RIFF....WEBP)
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return true;
        return false;
    }

    public static bool LooksLikeHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.TrimStart().ToLowerInvariant();
        if (lower.StartsWith("<!doctype html") || lower.StartsWith("<html")) return true;
        // Two or more markup tags -> treat as HTML (covers fragments).
        return Regex.Matches(text, "<\\s*/?\\s*[a-zA-Z][^>]*>").Count >= 2;
    }

    public static BitmapImage? TryLoadImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildHexDump(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return "";

        var length = Math.Min(bytes.Length, MaxHexBytes);
        var sb = new StringBuilder(length * 4);

        for (var offset = 0; offset < length; offset += 16)
        {
            sb.Append(offset.ToString("X8")).Append("  ");

            for (var i = 0; i < 16; i++)
            {
                if (offset + i < length) sb.Append(bytes[offset + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }

            sb.Append(' ');
            for (var i = 0; i < 16 && offset + i < length; i++)
            {
                var b = bytes[offset + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }

        if (bytes.Length > MaxHexBytes)
            sb.Append($"\n… {bytes.Length - MaxHexBytes:N0} more byte(s) not shown.");

        return sb.ToString();
    }
}
