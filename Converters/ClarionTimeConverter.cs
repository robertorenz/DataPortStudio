using System.Globalization;
using System.Windows.Data;
using DataPortStudio.Services;

namespace DataPortStudio.Converters;

/// <summary>
/// Two-way converter between a Clarion time integer and a displayed time string
/// (HH:mm:ss[.ff]). Empty/zero shows blank. On edit, a typed time is converted back to
/// the Clarion integer using the bound column's numeric type.
/// </summary>
public class ClarionTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || value == DBNull.Value) return "";
        if (!TryGetLong(value, out var n)) return value.ToString() ?? "";
        if (n <= 0) return "";

        return ClarionTime.Format(n) ?? n.ToString(CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var numericType = ResolveNumericType(targetType, parameter);

        if (!ClarionTime.TryParse(value as string, out var clarion))
            return Binding.DoNothing; // leave the cell unchanged on unparseable input

        return System.Convert.ChangeType(clarion, numericType);
    }

    private static Type ResolveNumericType(Type targetType, object? parameter)
    {
        if (parameter is Type p && IsNumeric(p)) return p;
        if (IsNumeric(targetType)) return targetType;
        return typeof(int);
    }

    private static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(decimal);

    private static bool TryGetLong(object value, out long result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case decimal d when d == Math.Truncate(d): result = (long)d; return true;
            default: result = 0; return false;
        }
    }
}
