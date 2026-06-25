using System.Data;

namespace DataPortStudio.Services;

public enum ClarionKind { Date, Time, Timestamp }

/// <summary>
/// Heuristically classifies integer columns that most likely hold Clarion dates or times.
///
/// Dates are flagged by name ("date"/"…dt") with most non-zero values in the plausible
/// date window, or purely by values when every non-zero value sits in that window with a
/// reasonable spread (which excludes small sequential IDs).
///
/// Times are flagged primarily by name ("time"/"…tm"), because the Clarion time range
/// (1..8,640,001) overlaps far too much ordinary integer data to trust values alone.
/// </summary>
public static class ClarionDetector
{
    public static Dictionary<string, ClarionKind> Detect(DataTable table)
    {
        var result = new Dictionary<string, ClarionKind>(StringComparer.OrdinalIgnoreCase);

        foreach (DataColumn col in table.Columns)
        {
            if (!IsCandidateType(col.DataType)) continue;

            var nameDate = NameLooksLikeDate(col.ColumnName);
            var nameTime = NameLooksLikeTime(col.ColumnName);
            var nameTs = NameLooksLikeTimestamp(col.ColumnName);

            int nonNull = 0, nonZero = 0, dateInRange = 0, timeInRange = 0, tsInRange = 0;
            var distinct = new HashSet<long>();
            var disqualified = false;

            foreach (DataRow row in table.Rows)
            {
                var raw = row[col];
                if (raw is null || raw == DBNull.Value) continue;
                if (!TryGetIntegral(raw, out var n)) { disqualified = true; break; }

                nonNull++;
                if (n == 0) continue;
                nonZero++;
                if (n > 0) distinct.Add(n);

                if (n >= ClarionDate.MinPlausible && n <= ClarionDate.MaxPlausible) dateInRange++;
                if (n >= 1 && n <= ClarionTime.MaxValue) timeInRange++;
                if (n >= TsMin && n <= TsMax) tsInRange++;
            }

            if (disqualified) continue;

            if (nonZero == 0)
            {
                // No values to range-check — trust the name only.
                if (nameTs && nonNull > 0) result[col.ColumnName] = ClarionKind.Timestamp;
                else if (nameDate && nonNull > 0) result[col.ColumnName] = ClarionKind.Date;
                else if (nameTime && nonNull > 0) result[col.ColumnName] = ClarionKind.Time;
                continue;
            }

            var dateFrac = (double)dateInRange / nonZero;
            var timeFrac = (double)timeInRange / nonZero;
            var tsFrac = (double)tsInRange / nonZero;

            var isDate = (nameDate && dateFrac >= 0.8) || (dateFrac >= 0.999 && distinct.Count >= 5);
            var isTime = nameTime && timeFrac >= 0.8;
            var isTimestamp = nameTs && tsFrac >= 0.8;

            if (isTimestamp)
                result[col.ColumnName] = ClarionKind.Timestamp;
            else if (isDate && isTime)
                result[col.ColumnName] = dateInRange >= timeInRange ? ClarionKind.Date : ClarionKind.Time;
            else if (isDate)
                result[col.ColumnName] = ClarionKind.Date;
            else if (isTime)
                result[col.ColumnName] = ClarionKind.Time;
        }

        return result;
    }

    // Unix-millisecond window: ~1973 .. ~2096.
    private const long TsMin = 100_000_000_000L;
    private const long TsMax = 4_000_000_000_000L;

    private static bool IsCandidateType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(decimal) ||
        t == typeof(double) || t == typeof(float);

    private static bool NameLooksLikeDate(string name)
    {
        var lower = name.ToLowerInvariant();
        // English + Spanish (fecha) date hints.
        return lower.Contains("date") || lower.Contains("fecha")
            || lower.EndsWith("dt") || lower.EndsWith("fec");
    }

    private static bool NameLooksLikeTime(string name)
    {
        var lower = name.ToLowerInvariant();
        // English + Spanish (hora) time hints.
        return lower.Contains("time") || lower.Contains("hora")
            || lower.EndsWith("tm") || lower.EndsWith("hra");
    }

    private static bool NameLooksLikeTimestamp(string name)
    {
        var lower = name.ToLowerInvariant();
        // Clarion epoch-millisecond stamps: ts (TimeStamp), sts (Server TS), dts (Deleted TS).
        return lower is "ts" or "sts" or "dts" || lower.Contains("timestamp");
    }

    private static bool TryGetIntegral(object value, out long result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case decimal d when d == Math.Truncate(d): result = (long)d; return true;
            case double db when db == Math.Truncate(db) && Math.Abs(db) < 9.2e18: result = (long)db; return true;
            case float f when f == Math.Truncate(f) && Math.Abs(f) < 9.2e18: result = (long)f; return true;
            default: result = 0; return false;
        }
    }
}
