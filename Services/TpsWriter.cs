using System.IO;
using System.Text;
using TpsParser;

namespace DataPortStudio.Services;

public static class TpsWriter
{
    public record TpsFieldChange(string FieldName, object? NewValue);
    public record TpsRowEdit(int RecordNumber, IReadOnlyList<TpsFieldChange> Changes);
    public record SaveResult(int Patched, IReadOnlyList<string> Warnings);

    private sealed class PageRleInfo
    {
        public int PageAddr;             // absolute file offset of page header
        public int PageDataStart;        // absolute file offset of compressed data
        public int CompLen;              // original compressed data length
        public int[] DecToEnc = [];      // decoded index → encoded index in compData (-1 = run byte)
        public byte[] Decoded = [];      // full decoded page data
    }

    private abstract record LocBase;
    private record DirectLoc(int ContentFileOffset) : LocBase;
    private record RleLoc(PageRleInfo Page, int ContentDecStart) : LocBase;

    // ---- public entry point --------------------------------------------------

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

        using var ms  = new MemoryStream(fileBytes.ToArray());
        var tpsFile   = new TpsFile(ms);
        var locations = BuildRecordLocations(tpsFile, tableNumber);

        // Pages that need full RLE re-encoding: page → working copy of decoded bytes
        var pageReencodes = new Dictionary<PageRleInfo, byte[]>();

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

                int copyLen = Math.Min(serialized.Length, field.Length);

                if (loc is DirectLoc dl)
                {
                    int filePos = dl.ContentFileOffset + field.Offset;
                    if (filePos < 0 || filePos + copyLen > fileBytes.Length) continue;
                    Array.Copy(serialized, 0, fileBytes, filePos, copyLen);
                    if (serialized.Length < field.Length)
                        Array.Clear(fileBytes, filePos + serialized.Length, field.Length - serialized.Length);
                    anyChange = true;
                }
                else if (loc is RleLoc rl)
                {
                    // Check whether any of this field's bytes land in an RLE run
                    bool needsReencode = false;
                    for (int i = 0; i < copyLen && !needsReencode; i++)
                    {
                        int decIdx = rl.ContentDecStart + field.Offset + i;
                        if (decIdx >= 0 && decIdx < rl.Page.DecToEnc.Length &&
                            rl.Page.DecToEnc[decIdx] < 0)
                            needsReencode = true;
                    }

                    if (needsReencode)
                    {
                        // Stage for full page re-encode: get or create working decoded copy
                        if (!pageReencodes.TryGetValue(rl.Page, out var workDec))
                        {
                            workDec = rl.Page.Decoded.ToArray();
                            pageReencodes[rl.Page] = workDec;
                        }
                        for (int i = 0; i < copyLen; i++)
                        {
                            int decIdx = rl.ContentDecStart + field.Offset + i;
                            if (decIdx >= 0 && decIdx < workDec.Length)
                                workDec[decIdx] = serialized[i];
                        }
                        anyChange = true;
                    }
                    else
                    {
                        // All bytes are in literal blocks — direct patch
                        bool fieldPatched = false;
                        for (int i = 0; i < copyLen; i++)
                        {
                            int decIdx = rl.ContentDecStart + field.Offset + i;
                            if (decIdx < 0 || decIdx >= rl.Page.DecToEnc.Length) continue;
                            int encIdx = rl.Page.DecToEnc[decIdx];
                            if (encIdx < 0) continue;
                            int filePos = rl.Page.PageDataStart + encIdx;
                            if (filePos < 0 || filePos >= fileBytes.Length) continue;
                            fileBytes[filePos] = serialized[i];
                            fieldPatched = true;
                        }
                        if (fieldPatched) anyChange = true;
                    }
                }
            }

            if (anyChange) patched++;
        }

        // Apply page re-encodings
        foreach (var (pageInfo, workDec) in pageReencodes)
        {
            byte[] newComp = RleEncode(workDec);
            if (newComp.Length > pageInfo.CompLen)
            {
                warnings.Add(
                    $"Cannot save: the new value is too long for the available space in this TPS page " +
                    $"(re-encoded {newComp.Length} bytes, page holds {pageInfo.CompLen}).");
                continue;
            }
            Array.Copy(newComp, 0, fileBytes, pageInfo.PageDataStart, newComp.Length);
            if (newComp.Length < pageInfo.CompLen)
            {
                // Zero the unused tail of the original compressed region
                Array.Clear(fileBytes, pageInfo.PageDataStart + newComp.Length,
                    pageInfo.CompLen - newComp.Length);
                // Update the 2-byte page size in the page header (LE16 at pageAddr+4)
                int headerSize  = pageInfo.PageDataStart - pageInfo.PageAddr;
                int newPageSize = headerSize + newComp.Length;
                fileBytes[pageInfo.PageAddr + 4] = (byte)(newPageSize & 0xFF);
                fileBytes[pageInfo.PageAddr + 5] = (byte)(newPageSize >> 8);
            }
        }

        if (patched > 0)
            File.WriteAllBytes(filePath, fileBytes);

        return new SaveResult(patched, warnings);
    }

    // ---- record location builder --------------------------------------------

    private static Dictionary<int, LocBase> BuildRecordLocations(TpsFile tpsFile, int tableNumber)
    {
        var map = new Dictionary<int, LocBase>();

        foreach (var block in tpsFile.GetBlocks())
        foreach (var page in block.GetPages())
        {
            IReadOnlyList<TpsRecord> records;
            try { records = page.GetRecords(ErrorHandlingOptions.Default); }
            catch { continue; }

            int headerSize    = (int)page.Size - page.CompressedData.Length;
            int pageDataStart = (int)page.AbsoluteAddress + headerSize;
            var compData      = page.CompressedData.ToArray();

            int[]? decToEnc = null;
            byte[]? decoded = null;
            try
            {
                (decoded, decToEnc) = RleDecode(compData);
                if (decoded.Length == compData.Length &&
                    decoded.AsSpan().SequenceEqual(compData))
                {
                    decoded = null; decToEnc = null;
                }
            }
            catch { decoded = null; decToEnc = null; }

            if (decoded != null && decToEnc != null)
            {
                var pageInfo = new PageRleInfo
                {
                    PageAddr      = (int)page.AbsoluteAddress,
                    PageDataStart = pageDataStart,
                    CompLen       = compData.Length,
                    DecToEnc      = decToEnc,
                    Decoded       = decoded,
                };
                int decPos = 0;
                foreach (var rec in records)
                {
                    if (rec.PayloadType != RecordPayloadType.Data) { SkipRecordDec(rec, ref decPos); continue; }
                    if (rec.GetPayload() is not DataRecordPayload drp) { SkipRecordDec(rec, ref decPos); continue; }

                    int pt = rec.PayloadTotalLength;
                    int pi = rec.PayloadInheritedBytes;
                    int ph = rec.PayloadHeaderLength;
                    bool isAnchor = rec.OwnsPayloadTotalLength;

                    int preamble       = isAnchor ? 5 : 1;
                    int storedPayload  = pt - pi;
                    int advance        = preamble + storedPayload;
                    int contentDecStart = decPos + preamble + Math.Max(0, ph - pi);

                    if (drp.TableNumber == tableNumber && contentDecStart < decoded.Length)
                        map[drp.RecordNumber] = new RleLoc(pageInfo, contentDecStart);

                    decPos += advance;
                }
            }
            else
            {
                foreach (var rec in records)
                {
                    if (rec.PayloadType != RecordPayloadType.Data) continue;
                    if (rec.GetPayload() is not DataRecordPayload drp) continue;
                    if (drp.TableNumber != tableNumber) continue;

                    var content = drp.Content.ToArray();
                    if (content.Length == 0) continue;

                    int compOffset = IndexOf(compData, content);
                    if (compOffset < 0) continue;

                    map[drp.RecordNumber] = new DirectLoc(pageDataStart + compOffset);
                }
            }
        }

        return map;
    }

    private static void SkipRecordDec(TpsRecord rec, ref int decPos)
    {
        int pt = rec.PayloadTotalLength;
        int pi = rec.PayloadInheritedBytes;
        bool isAnchor = rec.OwnsPayloadTotalLength;
        decPos += isAnchor ? 5 + pt : 1 + (pt - pi);
    }

    // ---- TPS RLE decoder ----------------------------------------------------
    // Format: [litCount][literal bytes][runCount][runCount copies of last literal], repeat.
    // Counts: 1 byte if < 128; 2 bytes (lo|0x80, hi) where value = (lo & 0x7F) | (hi << 7).

    private static (byte[] decoded, int[] decToEnc) RleDecode(byte[] comp)
    {
        var dec  = new List<byte>(comp.Length * 4);
        var offs = new List<int>(comp.Length * 4);
        int pos  = 0;
        byte lastLit = 0;

        int ReadCount()
        {
            if (pos >= comp.Length) return 0;
            int b = comp[pos++];
            if (b < 128) return b;
            if (pos >= comp.Length) return b & 0x7F;
            int hi = comp[pos++];
            return (b & 0x7F) | (hi << 7);
        }

        while (pos < comp.Length)
        {
            int litCount = ReadCount();
            int litBase  = pos;
            for (int i = 0; i < litCount; i++) { dec.Add(comp[pos]); offs.Add(pos); pos++; }
            if (litCount > 0) lastLit = comp[litBase + litCount - 1];

            if (pos >= comp.Length) break;

            int runCount = ReadCount();
            for (int i = 0; i < runCount; i++) { dec.Add(lastLit); offs.Add(-1); }
        }

        return (dec.ToArray(), offs.ToArray());
    }

    // ---- TPS RLE encoder ----------------------------------------------------
    // Mirrors the greedy Clarion strategy: only start a run when 3+ consecutive identical
    // bytes are found (seed byte counted as the last literal, then runCount additional copies).

    private static byte[] RleEncode(byte[] dec)
    {
        var out2 = new List<byte>(dec.Length);
        int pos  = 0;

        void EmitCount(int n)
        {
            if (n < 128) { out2.Add((byte)n); }
            else { out2.Add((byte)(0x80 | (n & 0x7F))); out2.Add((byte)(n >> 7)); }
        }

        while (pos < dec.Length)
        {
            int litStart = pos;

            // Collect literals until a profitable run (3+ consecutive identical bytes) or end
            while (pos < dec.Length)
            {
                // 3+ consecutive identical starting at pos → seed at pos, break
                if (pos + 2 < dec.Length && dec[pos] == dec[pos + 1] && dec[pos + 1] == dec[pos + 2])
                {
                    pos++; // include seed in literal block
                    break;
                }
                // Only 2 identical at very end — include both as literals (no run possible)
                if (pos + 1 < dec.Length && dec[pos] == dec[pos + 1] && pos + 2 >= dec.Length)
                {
                    pos += 2;
                    break;
                }
                pos++;
            }

            int litCount = pos - litStart;
            EmitCount(litCount);
            for (int i = litStart; i < pos; i++) out2.Add(dec[i]);

            // Count run copies after the seed
            int runCount = 0;
            if (pos > 0 && pos < dec.Length)
            {
                byte runVal = dec[pos - 1];
                while (pos < dec.Length && dec[pos] == runVal) { runCount++; pos++; }
            }

            // Omit trailing runCount=0 at end of data (matches original Clarion encoding)
            if (runCount > 0 || pos < dec.Length)
                EmitCount(runCount);
        }

        return out2.ToArray();
    }

    // ---- helpers ------------------------------------------------------------

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

    // ---- field serialization ------------------------------------------------

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
        catch { return null; }
    }

    private static int ToClarionDate(object value)
    {
        var dt = value switch
        {
            DateTime d => DateOnly.FromDateTime(d),
            DateOnly d => d,
            string s   => DateOnly.Parse(s),
            _          => DateOnly.FromDateTime(Convert.ToDateTime(value))
        };
        return dt.DayNumber - new DateOnly(1800, 12, 28).DayNumber;
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

        for (int i = 0; i < signNibble; i++)
        {
            int digitIndex = digits.Length - 1 - i;
            byte d = digitIndex >= 0 ? (byte)(digits[digitIndex] - '0') : (byte)0;
            int nibblePos  = signNibble - 1 - i;
            int byteIndex  = nibblePos / 2;
            if (nibblePos % 2 == 0)
                buf[byteIndex] = (byte)((buf[byteIndex] & 0x0F) | (d << 4));
            else
                buf[byteIndex] = (byte)((buf[byteIndex] & 0xF0) | d);
        }

        buf[bytes - 1] = (byte)((buf[bytes - 1] & 0xF0) | (neg ? 0x0D : 0x0F));
        return buf;
    }
}
