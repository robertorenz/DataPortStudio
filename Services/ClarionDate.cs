namespace DataPortStudio.Services;

/// <summary>
/// Clarion "Standard Date" helpers. A Clarion date is the number of days since
/// December 28, 1800 (so 4 = 1801-01-01). 0 (or negative) means an empty date.
/// </summary>
public static class ClarionDate
{
    public static readonly DateTime Epoch = new(1800, 12, 28);

    /// <summary>Plausible window used by detection: 1900-01-01 .. 2079-12-31.</summary>
    public static readonly long MinPlausible = ToClarion(new DateTime(1900, 1, 1));
    public static readonly long MaxPlausible = ToClarion(new DateTime(2079, 12, 31));

    public static long ToClarion(DateTime date) => (long)(date.Date - Epoch).TotalDays;

    /// <summary>Returns the date for a Clarion value, or null for empty/out-of-range.</summary>
    public static DateTime? FromClarion(long value)
    {
        if (value <= 0) return null;
        try
        {
            return Epoch.AddDays(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
