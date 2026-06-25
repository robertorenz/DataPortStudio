using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DataPortStudio.Services;

namespace DataPortStudio.Converters;

/// <summary>
/// Two-way converter between a Clarion date integer and a displayed date string
/// (ISO yyyy-MM-dd). Empty/zero shows blank. On edit, a typed date is converted
/// back to the Clarion integer using the bound column's numeric type.
/// </summary>
public class ClarionDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || value == DBNull.Value) return "";
        if (!TryGetLong(value, out var n)) return value.ToString() ?? "";
        if (n <= 0) return "";

        var date = ClarionDate.FromClarion(n);
        return date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
               ?? n.ToString(CultureInfo.InvariantCulture); // out of range — show raw
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var numericType = ResolveNumericType(targetType, parameter);
        var text = value as string;

        if (string.IsNullOrWhiteSpace(text))
            return System.Convert.ChangeType(0, numericType); // empty date -> 0

        if (!DateTime.TryParse(text, culture, DateTimeStyles.None, out var date) &&
            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return Binding.DoNothing; // leave the cell unchanged on unparseable input

        var clarion = ClarionDate.ToClarion(date);
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
