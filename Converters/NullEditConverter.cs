using System.Globalization;
using System.Windows.Data;

namespace DataPortStudio.Converters;

/// <summary>
/// Shows "(Null)" for null/DBNull cells (Navicat-style) while keeping in-grid editing type-correct.
/// The bound column's CLR type is passed as the ConverterParameter for ConvertBack.
/// </summary>
public class NullEditConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value == DBNull.Value ? "(Null)" : value;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        var type = parameter as Type ?? targetType;

        if (string.IsNullOrEmpty(text) || text == "(Null)")
            return DBNull.Value;

        try
        {
            if (type == typeof(string)) return text;
            if (type == typeof(Guid)) return Guid.Parse(text.Trim());
            if (type == typeof(TimeSpan)) return TimeSpan.Parse(text.Trim(), CultureInfo.CurrentCulture);
            if (type == typeof(bool)) return ParseBool(text);
            if (type == typeof(DateTime))
                return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt)
                    ? dt
                    : DateTime.Parse(text, CultureInfo.InvariantCulture);
            if (type == typeof(byte[])) return System.Convert.FromBase64String(text.Trim());
            return System.Convert.ChangeType(text, type, CultureInfo.CurrentCulture);
        }
        catch
        {
            // Bad input — leave the cell unchanged rather than throwing a binding exception.
            return Binding.DoNothing;
        }
    }

    private static bool ParseBool(string text)
    {
        var s = text.Trim();
        if (bool.TryParse(s, out var b)) return b;
        return s.ToLowerInvariant() switch
        {
            "1" or "y" or "yes" or "t" or "true" or "si" or "sí" or "verdadero" => true,
            "0" or "n" or "no" or "f" or "false" or "falso" => false,
            _ => System.Convert.ToBoolean(s, CultureInfo.CurrentCulture)
        };
    }
}
