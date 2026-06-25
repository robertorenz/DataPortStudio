using System.Globalization;
using System.Windows.Data;

namespace DataPortStudio.Converters;

/// <summary>
/// Two-way converter between a Unix epoch-millisecond timestamp (Clarion ts/sts/dts) and a
/// readable local date-time (yyyy-MM-dd HH:mm:ss). 0/blank shows empty. On edit, a typed
/// date-time is converted back to epoch milliseconds in the column's numeric type.
/// </summary>
public class ClarionTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || value == DBNull.Value) return "";
        if (!TryGetLong(value, out var ms)) return value.ToString() ?? "";
        if (ms <= 0) return "";

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return ms.ToString(CultureInfo.InvariantCulture);
        }
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var numericType = ResolveNumericType(targetType, parameter);
        var text = value as string;

        if (string.IsNullOrWhiteSpace(text))
            return System.Convert.ChangeType(0, numericType);

        if (!DateTime.TryParse(text, culture, DateTimeStyles.None, out var dt) &&
            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return Binding.DoNothing;

        var local = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
        return System.Convert.ChangeType(local.ToUnixTimeMilliseconds(), numericType);
    }

    private static Type ResolveNumericType(Type targetType, object? parameter)
    {
        if (parameter is Type p && IsNumeric(p)) return p;
        if (IsNumeric(targetType)) return targetType;
        return typeof(long);
    }

    private static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) ||
        t == typeof(decimal) || t == typeof(double) || t == typeof(float);

    private static bool TryGetLong(object value, out long result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case decimal d: result = (long)d; return true;
            case double db: result = (long)db; return true;
            case float f: result = (long)f; return true;
            default: result = 0; return false;
        }
    }
}
