using System.IO;
using System.Text;
using TpsParser;

namespace DataPortStudio.Services;

/// <summary>
/// Writes modified records back to a Clarion TPS file.
///
/// Write strategy:
///   TPS page.CompressedData equals the raw bytes stored in the file at the page's
///   data offset (page.AbsoluteAddress + headerSize). For every record — including
///   delta-encoded ones (PayloadInheritedBytes > 0) where Content is synthesized by
///   TpsParser from inherited + stored bytes — the 12 Content bytes appear verbatim
///   within CompressedData. A simple byte search within CompressedData locates the
///   exact file offset to patch. No RLE re-encoding is needed; the page bytes are
///   modified in place.
/// </summary>
public static class TpsWriter
{
    public record TpsFieldChange(string FieldName, object? NewValue);
    public record TpsRowEdit(int RecordNumber, IReadOnlyList<TpsFieldChange> Changes);
    public record SaveResult(int Patched, IReadOnlyList<string> Warnings);

    // ContentFileOffset = byte offset in the file where drp.Content begins
    private record struct RecordLoc(int ContentFileOffset);

    // ---- public entry point -----------------------------------------------

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

        using var ms    = new MemoryStream(fileBytes);
        var tpsFile     = new TpsFile(ms);

        var locations   = BuildRecordLocations(tpsFile, tableNumber);

        int patched = 0;
        foreach (var edit in editList)
        {
            if (!locations.TryGetValue(edit.RecordNumber, out var loc))
            {
                warnings.Add($"Record {edit.RecordNumber}: could not locate in file.");
                continue;
            }

            bool anyChange = false;
            foreach (var change in edit.Changes)
            {
                var field = def.Fields.FirstOrDefault(f =>
                    string.Equals(f.Name, change.FieldName, StringComparison.OrdinalIgnoreCase));
                if (field is null) continue;

                var serialized = SerializeField(field, change.NewValue);
                if (serialized is null) continue;

                // field.Offset is relative to Content (= PayloadData after the header)
                int copyLen = Math.Min(serialized.Length, field.Length);
                int filePos = loc.ContentFileOffset + field.Offset;

                if (filePos < 0 || filePos + copyLen > fileBytes.Length) continue;

                Array.Copy(serialized, 0, fileBytes, filePos, copyLen);
                if (serialized.Length < field.Length)
                    Array.Clear(fileBytes, filePos + serialized.Length, field.Length - serialized.Length);
                anyChange = true;
            }

            if (anyChange) patched++;
        }

        if (patched > 0)
            File.WriteAllBytes(filePath, fileBytes);

        return new SaveResult(patched, warnings);
    }

    // ---- record location builder -------------------------------------------

    /// <summary>
    /// Searches for each record's Content bytes within page.CompressedData.
    /// CompressedData equals the raw file bytes at the page data offset, and
    /// Content appears verbatim there for both anchor and delta-encoded records.
    /// </summary>
    private static Dictionary<int, RecordLoc> BuildRecordLocations(
        TpsFile tpsFile, int tableNumber)
    {
        var map = new Dictionary<int, RecordLoc>();

        foreach (var block in tpsFile.GetBlocks())
        {
            foreach (var page in block.GetPages())
            {
                IReadOnlyList<TpsRecord> records;
                try { records = page.GetRecords(ErrorHandlingOptions.Default); }
                catch { continue; }

                // headerSize = page header bytes before the data payload in the file
                int headerSize    = (int)page.Size - page.CompressedData.Length;
                int pageDataStart = (int)page.AbsoluteAddress + headerSize;
                var compData      = page.CompressedData.ToArray();

                foreach (var rec in records)
                {
                    if (rec.PayloadType != RecordPayloadType.Data) continue;
                    if (rec.GetPayload() is not DataRecordPayload drp) continue;
                    if (drp.TableNumber != tableNumber) continue;

                    var content = drp.Content.ToArray();
                    if (content.Length == 0) continue;

                    // Search for Content bytes in CompressedData (= raw file bytes)
                    int compOffset = IndexOf(compData, content);
                    if (compOffset < 0) continue;

                    map[drp.RecordNumber] = new RecordLoc(pageDataStart + compOffset);
                }
            }
        }

        return map;
    }

    // ---- search helper -----------------------------------------------------

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        var span  = haystack.AsSpan();
        int limit = haystack.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
            if (span.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    // ---- field serialization -----------------------------------------------

    private static byte[]? SerializeField(FieldDefinition field, object? value)
    {
        if (value is null || value is DBNull)
            return new byte[field.Length];

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
            return null;
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
        return (int)(ts.TotalMilliseconds / 10.0) + 1;
    }

    private static byte[] SerializeFixed(object value, int fieldLength, char pad)
    {
        var s   = value.ToString() ?? "";
        var raw = Encoding.Latin1.GetBytes(s);
        var buf = new byte[fieldLength];
        if (pad != '\0') Array.Fill(buf, (byte)pad);
        int len = Math.Min(raw.Length, fieldLength);
        Array.Copy(raw, 0, buf, 0, len);
        return buf;
    }

    private static byte[] SerializePString(object value, int maxLength)
    {
        var s   = value.ToString() ?? "";
        var raw = Encoding.Latin1.GetBytes(s);
        int len = Math.Min(raw.Length, maxLength);
        var buf = new byte[maxLength + 1];
        buf[0]  = (byte)len;
        Array.Copy(raw, 0, buf, 1, len);
        return buf;
    }

    private static byte[] SerializeBcd(FieldDefinition field, object value)
    {
        var dec  = Math.Abs(Convert.ToDecimal(value));
        bool neg = Convert.ToDecimal(value) < 0;
        int bytes = (int)field.BcdElementLength;
        int scale = (int)field.BcdDigitsAfterDecimalPoint;

        var shifted = decimal.Round(dec * (decimal)Math.Pow(10, scale), 0);
        var digits  = shifted.ToString("0");

        var buf          = new byte[bytes];
        int totalNibbles = bytes * 2;
        int signNibble   = totalNibbles - 1;
        int digitNibbles = signNibble;

        for (int i = 0; i < digitNibbles; i++)
        {
            int digitIndex = digits.Length - 1 - i;
            byte d = digitIndex >= 0 ? (byte)(digits[digitIndex] - '0') : (byte)0;
            int nibblePos = signNibble - 1 - i;
            int byteIndex = nibblePos / 2;
            if (nibblePos % 2 == 0)
                buf[byteIndex] = (byte)((buf[byteIndex] & 0x0F) | (d << 4));
            else
                buf[byteIndex] = (byte)((buf[byteIndex] & 0xF0) | d);
        }

        byte sign = neg ? (byte)0x0D : (byte)0x0F;
        buf[bytes - 1] = (byte)((buf[bytes - 1] & 0xF0) | sign);
        return buf;
    }
}
