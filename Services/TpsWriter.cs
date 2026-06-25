using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TpsParser;

namespace DataPortStudio.Services;

/// <summary>
/// Writes modified records back to a Clarion TPS file.
///
/// Strategy:
///   1. Read the whole file into a byte array.
///   2. Build a map of RecordNumber → (TpsPage, contentOffset) by walking all
///      blocks/pages; MemoryMarshal correlates Content slices with PageData buffers.
///   3. For each changed record:
///        a. Uncompressed pages: find PayloadData verbatim in fileBytes (fast path).
///        b. Compressed pages: copy PageData, patch the Content region, re-encode
///           with TPS RLE, verify the encoded length is unchanged, then write back
///           at AbsoluteAddress + headerSize.
///   4. Write the modified array back to disk.
/// </summary>
public static class TpsWriter
{
    public record TpsFieldChange(string FieldName, object? NewValue);
    public record TpsRowEdit(int RecordNumber, IReadOnlyList<TpsFieldChange> Changes);
    public record SaveResult(int Patched, IReadOnlyList<string> Warnings);

    private record PageRecordInfo(TpsPage Page, int ContentOffset, int ContentLength);

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

        using var ms = new MemoryStream(fileBytes);
        var tpsFile  = new TpsFile(ms);

        var payloads = tpsFile.GetDataRecordPayloads(tableNumber)
            .ToDictionary(p => p.RecordNumber);

        // Build page-location map for the compressed-page slow path.
        var pageMap = BuildPageRecordMap(tpsFile, tableNumber, payloads);

        int patched = 0;
        foreach (var edit in editList)
        {
            if (!payloads.TryGetValue(edit.RecordNumber, out var payload))
            {
                warnings.Add($"Record {edit.RecordNumber}: not found in file.");
                continue;
            }

            var payloadBytes = payload.PayloadData.ToArray();
            var contentBytes = payload.Content.ToArray();
            int contentStart = payloadBytes.Length - contentBytes.Length;
            if (contentStart < 0) contentStart = 0;

            // Build updated content by patching each changed field.
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
                if (serialized.Length < field.Length)
                    Array.Clear(updated, field.Offset + serialized.Length, field.Length - serialized.Length);
            }

            // Fast path: uncompressed page — PayloadData appears verbatim in the file.
            int pos = FindPattern(fileBytes, payloadBytes);
            if (pos >= 0)
            {
                Array.Copy(updated, 0, fileBytes, pos + contentStart, updated.Length);
                patched++;
                continue;
            }

            // Slow path: RLE-compressed page.
            if (!pageMap.TryGetValue(edit.RecordNumber, out var info))
            {
                warnings.Add($"Record {edit.RecordNumber}: could not locate in file.");
                continue;
            }

            var page          = info.Page;
            int contentOffset = info.ContentOffset;

            // Patch the decompressed page data in memory.
            var modifiedPageData = page.PageData.ToArray();
            Array.Copy(updated, 0, modifiedPageData, contentOffset, updated.Length);

            // Re-encode with TPS RLE and verify the length is unchanged.
            var origCompressed = page.CompressedData.ToArray();
            var newCompressed  = TpsRleEncode(modifiedPageData);

            if (newCompressed.Length != origCompressed.Length)
            {
                warnings.Add(
                    $"Record {edit.RecordNumber}: re-compressed size changed " +
                    $"({origCompressed.Length} → {newCompressed.Length} bytes). " +
                    "Record not saved — try a value of similar magnitude.");
                continue;
            }

            // headerSize = total page size − compressed-data portion.
            int headerSize  = (int)page.Size - origCompressed.Length;
            int dataFileOfs = page.AbsoluteAddress + headerSize;

            if (dataFileOfs < 0 || dataFileOfs + newCompressed.Length > fileBytes.Length)
            {
                warnings.Add($"Record {edit.RecordNumber}: file offset out of range.");
                continue;
            }

            Array.Copy(newCompressed, 0, fileBytes, dataFileOfs, newCompressed.Length);
            patched++;
        }

        if (patched > 0)
            File.WriteAllBytes(filePath, fileBytes);

        return new SaveResult(patched, warnings);
    }

    // ---- page / record map -------------------------------------------------

    private static Dictionary<int, PageRecordInfo> BuildPageRecordMap(
        TpsFile tpsFile,
        int tableNumber,
        Dictionary<int, DataRecordPayload> payloads)
    {
        var map       = new Dictionary<int, PageRecordInfo>();
        var remaining = new HashSet<int>(payloads.Keys);

        foreach (var block in tpsFile.GetBlocks())
        {
            if (remaining.Count == 0) break;

            foreach (var page in block.GetPages())
            {
                if (remaining.Count == 0) break;

                MemoryMarshal.TryGetArray(page.PageData, out var pageArr);

                foreach (var record in page.GetRecords(ErrorHandlingOptions.Default))
                {
                    if (record.PayloadType != RecordPayloadType.Data) continue;
                    if (record.GetPayload() is not DataRecordPayload drp) continue;
                    if (drp.TableNumber != tableNumber) continue;
                    if (!remaining.Contains(drp.RecordNumber)) continue;

                    int offset = -1;

                    // Try MemoryMarshal: O(1) if Content is a slice of PageData's buffer.
                    MemoryMarshal.TryGetArray(drp.Content, out var contentArr);
                    if (pageArr.Array is not null && contentArr.Array is not null &&
                        pageArr.Array == contentArr.Array &&
                        contentArr.Offset >= pageArr.Offset &&
                        contentArr.Offset + contentArr.Count <= pageArr.Offset + pageArr.Count)
                    {
                        offset = contentArr.Offset - pageArr.Offset;
                    }
                    else
                    {
                        // Fallback: linear byte search of Content within PageData.
                        offset = IndexOf(page.PageData.ToArray(), drp.Content.ToArray());
                    }

                    if (offset >= 0)
                    {
                        map[drp.RecordNumber] = new PageRecordInfo(page, offset, drp.Content.Length);
                        remaining.Remove(drp.RecordNumber);
                    }
                }
            }
        }

        return map;
    }

    // ---- TPS RLE encoder ---------------------------------------------------

    /// <summary>
    /// Encodes bytes using the Clarion TPS RLE scheme:
    ///   0x80..0xFF — run: repeat next byte (control − 0x80 + 2) times.
    ///   0x01..0x7F — literals: next (control) bytes copied verbatim.
    /// </summary>
    private static byte[] TpsRleEncode(byte[] data)
    {
        var result = new MemoryStream(data.Length + data.Length / 4 + 16);
        int i = 0;

        while (i < data.Length)
        {
            byte val = data[i];

            int runLen = 1;
            while (i + runLen < data.Length && data[i + runLen] == val && runLen < 129)
                runLen++;

            if (runLen >= 2)
            {
                result.WriteByte((byte)(0x80 + runLen - 2));
                result.WriteByte(val);
                i += runLen;
            }
            else
            {
                int litStart = i;
                int litLen   = 0;

                while (litLen < 0x7F && i + litLen < data.Length)
                {
                    int fwd = 1;
                    while (i + litLen + fwd < data.Length &&
                           data[i + litLen + fwd] == data[i + litLen] &&
                           fwd < 129)
                        fwd++;
                    if (fwd >= 2) break;
                    litLen++;
                }

                if (litLen == 0) litLen = 1;
                result.WriteByte((byte)litLen);
                result.Write(data, litStart, litLen);
                i += litLen;
            }
        }

        return result.ToArray();
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

    // ---- search helpers ----------------------------------------------------

    private static int FindPattern(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        var span  = haystack.AsSpan();
        var pat   = needle.AsSpan();
        int limit = haystack.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
            if (span.Slice(i, needle.Length).SequenceEqual(pat))
                return i;
        return -1;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        var span  = haystack.AsSpan();
        var pat   = needle.AsSpan();
        int limit = haystack.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
            if (span.Slice(i, needle.Length).SequenceEqual(pat))
                return i;
        return -1;
    }
}
