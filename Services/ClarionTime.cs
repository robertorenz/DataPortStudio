namespace DataPortStudio.Services;

/// <summary>
/// Clarion "Standard Time" helpers. A Clarion time is the number of hundredths of a
/// second (centiseconds) since midnight, plus one (so 1 = 00:00:00.00). Max = 8,640,001.
/// 0 means an empty time.
/// </summary>
public static class ClarionTime
{
    public const long MaxValue = 8_640_001; // 24:00:00.00

    /// <summary>Formats a Clarion time value as HH:mm:ss (with .ff only when needed).</summary>
    public static string? Format(long value)
    {
        if (value <= 0 || value > MaxValue) return null;

        var centi = value - 1;
        var cc = centi % 100;
        var totalSeconds = centi / 100;
        var s = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var m = totalMinutes % 60;
        var h = totalMinutes / 60;

        return cc > 0
            ? $"{h:00}:{m:00}:{s:00}.{cc:00}"
            : $"{h:00}:{m:00}:{s:00}";
    }

    /// <summary>Returns the time-of-day for a Clarion value, or null for empty/out-of-range.</summary>
    public static TimeSpan? ToTimeSpan(long value)
    {
        if (value <= 0 || value > MaxValue) return null;
        var ts = TimeSpan.FromTicks((value - 1) * 100_000L); // 1 centisecond = 100,000 ticks
        return ts >= TimeSpan.FromHours(24) ? null : ts;
    }

    /// <summary>Parses HH:mm[:ss[.ff]] back into a Clarion time value. Blank -> 0 (empty).</summary>
    public static bool TryParse(string? text, out long clarion)
    {
        clarion = 0;
        text = text?.Trim() ?? "";
        if (text.Length == 0) return true; // empty time

        var colon = text.Split(':');
        if (colon.Length is < 2 or > 3) return false;
        if (!int.TryParse(colon[0], out var h)) return false;
        if (!int.TryParse(colon[1], out var m)) return false;

        int s = 0, cc = 0;
        if (colon.Length == 3)
        {
            var secParts = colon[2].Split('.');
            if (!int.TryParse(secParts[0], out s)) return false;
            if (secParts.Length > 1)
            {
                var frac = (secParts[1] + "00")[..2]; // pad/truncate to 2 digits
                if (!int.TryParse(frac, out cc)) return false;
            }
        }

        if (h < 0 || m is < 0 or > 59 || s is < 0 or > 59 || cc is < 0 or > 99) return false;

        var centi = ((long)h * 3600 + m * 60 + s) * 100 + cc;
        if (centi < 0 || centi > MaxValue - 1) return false;

        clarion = centi + 1;
        return true;
    }
}
