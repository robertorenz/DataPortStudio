using System.IO;
using System.Text;
using TpsParser;

namespace DataPortStudio.Services;

/// <summary>
/// Writes modified records back to a Clarion TPS file.
///
/// TpsParser is a read-only library, so this class patches raw bytes in place:
///   1. Read the whole file into a byte array.
///   2. Use TpsParser to enumerate DataRecordPayloads and locate each record's
///      PayloadData bytes inside the file array (byte-pattern search).
///   3. Serialize each changed field value at its FieldDefinition.Offset within
///      the Content region and copy the patch into the file array.
///   4. Write the modified array back to disk.
///
/// Only UPDATE is supported. INSERT requires appending records / updating page
/// headers; DELETE requires index-file maintenance — neither is implemented.
///
/// Compressed pages: if TpsParser returns a PayloadData sequence that cannot be
/// located in the raw file bytes (because the page is RLE-compressed), the record
/// is skipped and a warning is added to the result.
/// </summary>
public static class TpsWriter
{
    public record TpsFieldChange(string FieldName, object? NewValue);
    public record TpsRowEdit(int RecordNumber, IReadOnlyList<TpsFieldChange> Changes);
    public record SaveResult(int Patched, IReadOnlyList<string> Warnings);

    public static SaveResult SaveChanges(
        string filePath,
        TableDefinition def,
        int tableNumber,
        IEnumerable<TpsRowEdit> edits)
    {
        var editList = edits.ToList();
        if (editList.Count == 0) return new SaveResult(0, Array.Empty<string>());

        var fileBytes = File.ReadAllBytes(filePath);
        var warnings  = new List<string>();

        // Parse the file to get all data record payloads for our table.
        using var ms = new MemoryStream(fileBytes);
        var tpsFile  = new TpsFile(ms);
        var payloads = tpsFile.GetDataRecordPayloads(tableNumber)
            .ToDictionary(p => p.RecordNumber);

        var patched = 0;
        foreach (var edit in editList)
        {
            if (!payloads.TryGetValue(edit.RecordNumber, out var payload))
            {
                warnings.Add($"Record {edit.RecordNumber}: not found in file.");
                continue;
            }

            var payloadBytes = payload.PayloadData.ToArray();
            var contentBytes = payload.Content.ToArray();

            // Determine the offset of Content within PayloadData.
            // Content is the trailing field-data portion of PayloadData.
            int contentStart = payloadBytes.Length - contentBytes.Length;
            if (contentStart < 0) contentStart = 0;

            // Build the updated content by patching each changed field.
            var updated = (byte[])contentBytes.Clone();
            foreach (var change in edit.Changes)
            {
                var field = def.Fields.FirstOrDefault(f =>
                    string.Equals(f.Name, change.FieldName, StringComparison.OrdinalIgnoreCase));
                if (field is null) continue;

                var serialized = SerializeField(field, change.NewValue);
                if (serialized is null) continue;

                int copyLen = Math.Min(serialized.Length, field.Length);
                Array.Copy(serialized, 0, updated, field.Offset, copyLen);
                // Zero-pad the remainder when serialized is shorter than the field slot.
                if (serialized.Length < field.Length)
                    Array.Clear(updated, field.Offset + serialized.Length, field.Length - serialized.Length);
            }

            // Locate PayloadData in the raw file bytes, then patch only the Content region.
            int pos = FindPattern(fileBytes, payloadBytes);
            if (pos < 0)
            {
                warnings.Add($"Record {edit.RecordNumber}: could not locate in file " +
                             "(the page may be RLE-compressed — save is not supported for compressed pages).");
                continue;
            }

            Array.Copy(updated, 0, fileBytes, pos + contentStart, updated.Length);
            patched++;
        }

        if (patched > 0)
            File.WriteAllBytes(filePath, fileBytes);

        return new SaveResult(patched, warnings);
    }

    // ---- field serialization -----------------------------------------------

    private static byte[]? SerializeField(FieldDefinition field, object? value)
    {
        if (value is null || value is DBNull)
            return new byte[field.Length]; // null → zeros

        try
        {
            return field.TypeCode switch
            {
                FieldTypeCode.Byte    => new[] { Convert.ToByte(value) },
                FieldTypeCode.Short   => BitConverter.GetBytes(Convert.ToInt16(value)),
                FieldTypeCode.UShort  => BitConverter.GetBytes(Convert.ToUInt16(value)),
                FieldTypeCode.Long    => BitConverter.GetBytes(Convert.ToInt32(value)),
                FieldTypeCode.ULong   => BitConverter.GetBytes(Convert.ToUInt32(value)),
                FieldTypeCode.SReal   => BitConverter.GetBytes(Convert.ToSingle(value)),
                FieldTypeCode.Real    => BitConverter.GetBytes(Convert.ToDouble(value)),
                FieldTypeCode.Date    => BitConverter.GetBytes(ToClarionDate(value)),
                FieldTypeCode.Time    => BitConverter.GetBytes(ToClarionTime(value)),
                FieldTypeCode.Decimal => SerializeBcd(field, value),
                FieldTypeCode.FString => SerializeFixed(value, field.Length, pad: ' '),
                FieldTypeCode.CString => SerializeFixed(value, field.Length, pad: '\0'),
                FieldTypeCode.PString => SerializePString(value, field.StringLength),
                _                     => null
            };
        }
        catch
        {
            return null; // skip unconvertible value rather than corrupting the record
        }
    }

    private static int ToClarionDate(object value)
    {
        var dt = value switch
        {
            DateTime d  => DateOnly.FromDateTime(d),
            DateOnly d  => d,
            string s    => DateOnly.Parse(s),
            _           => DateOnly.FromDateTime(Convert.ToDateTime(value))
        };
        // Clarion date = days since 28 Dec 1800 (so 1 Jan 1801 = day 4).
        var epoch = new DateOnly(1800, 12, 28);
        return dt.DayNumber - epoch.DayNumber;
    }

    private static int ToClarionTime(object value)
    {
        var ts = value switch
        {
            TimeSpan t => t,
            string s   => TimeSpan.Parse(s),
            _          => TimeSpan.FromTicks(Convert.ToInt64(value))
        };
        // Clarion time = centiseconds since midnight + 1 (0 means no time).
        return (int)(ts.TotalMilliseconds / 10.0) + 1;
    }

    private static byte[] SerializeFixed(object value, int fieldLength, char pad)
    {
        var s   = value.ToString() ?? "";
        var raw = Encoding.Latin1.GetBytes(s);
        var buf = new byte[fieldLength];
        if (pad != '\0') Array.Fill(buf, (byte)pad); // space-fill for FString
        int len = Math.Min(raw.Length, fieldLength);
        Array.Copy(raw, 0, buf, 0, len);
        return buf;
    }

    private static byte[] SerializePString(object value, int maxLength)
    {
        var s   = value.ToString() ?? "";
        var raw = Encoding.Latin1.GetBytes(s);
        int len = Math.Min(raw.Length, maxLength);
        var buf = new byte[maxLength + 1]; // leading length byte
        buf[0]  = (byte)len;
        Array.Copy(raw, 0, buf, 1, len);
        return buf;
    }

    /// <summary>
    /// Packs a decimal value into Clarion BCD format.
    /// Each byte holds two decimal digits (high nibble first); the final nibble
    /// is the sign: 0x0F = positive / zero, 0x0D = negative.
    /// </summary>
    private static byte[] SerializeBcd(FieldDefinition field, object value)
    {
        var dec   = Math.Abs(Convert.ToDecimal(value));
        bool neg  = Convert.ToDecimal(value) < 0;
        int bytes = (int)field.BcdElementLength;
        int scale = (int)field.BcdDigitsAfterDecimalPoint;

        // Shift the decimal value to an integer of all significant digits.
        var shifted = decimal.Round(dec * (decimal)Math.Pow(10, scale), 0);
        var digits  = shifted.ToString("0");

        var buf = new byte[bytes];
        // Total nibbles = bytes * 2; last nibble is sign, rest are BCD digits right-aligned.
        int totalNibbles = bytes * 2;
        int signNibble   = totalNibbles - 1;
        int digitNibbles = signNibble;

        // Fill digits from right to left (right before sign nibble).
        for (int i = 0; i < digitNibbles; i++)
        {
            int digitIndex = digits.Length - 1 - i;
            byte d = digitIndex >= 0 ? (byte)(digits[digitIndex] - '0') : (byte)0;
            int nibblePos = signNibble - 1 - i;
            int byteIndex = nibblePos / 2;
            if (nibblePos % 2 == 0) // high nibble
                buf[byteIndex] = (byte)((buf[byteIndex] & 0x0F) | (d << 4));
            else                    // low nibble
                buf[byteIndex] = (byte)((buf[byteIndex] & 0xF0) | d);
        }

        // Sign nibble (last nibble of last byte).
        byte sign = neg ? (byte)0x0D : (byte)0x0F;
        buf[bytes - 1] = (byte)((buf[bytes - 1] & 0xF0) | sign);
        return buf;
    }

    // ---- byte-pattern search -----------------------------------------------

    /// <summary>Finds the first occurrence of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int FindPattern(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        var span = haystack.AsSpan();
        var pat  = needle.AsSpan();
        int limit = haystack.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
            if (span.Slice(i, needle.Length).SequenceEqual(pat))
                return i;
        return -1;
    }
}
