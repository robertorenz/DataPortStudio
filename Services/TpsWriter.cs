using System.IO;
using System.Text;
using TpsParser;

namespace DataPortStudio.Services;

public static class TpsWriter
{
    public record TpsFieldChange(string FieldName, object? NewValue);
    public record TpsRowEdit(int RecordNumber, IReadOnlyList<TpsFieldChange> Changes);
    public record SaveResult(int Patched, IReadOnlyList<string> Warnings, string? DiagnosticLogPath = null);

    private sealed class PageRleInfo
    {
        public int PageAddr;                    // absolute file offset of page header
        public int PageDataStart;               // absolute file offset of compressed data
        public int CompLen;                     // original compressed data length
        public int[] DecToEnc = [];             // decoded index → encoded index in compData (-1 = run byte)
        public byte[] Decoded = [];             // full decoded page data
        public int AnchorContentDecStart = -1;  // decoded start of the anchor record's field data
    }

    private abstract record LocBase;
    // FieldDataBase: first field.Offset whose byte is stored (not inherited from anchor).
    // For anchors and most deltas (pi ≤ ph) this is 0, meaning all field bytes are stored.
    private record DirectLoc(int ContentFileOffset, int FieldDataBase = 0) : LocBase;

    // FieldDataBase = max(0, pi − ph): the first field.Offset whose byte is actually stored
    // in this record's decoded slice (lower offsets are inherited from the page anchor).
    private record RleLoc(PageRleInfo Page, int ContentDecStart, int FieldDataBase) : LocBase;

    // ---- public entry point --------------------------------------------------

    public static SaveResult SaveChanges(
        string filePath,
        TableDefinition def,
        int tableNumber,
        IEnumerable<TpsRowEdit> edits)
    {
        var editList  = edits.ToList();
        var logPath   = Path.Combine(Path.GetTempPath(), "DataPortStudio_tps_debug.txt");
        var diagLog   = new System.Text.StringBuilder();
        diagLog.AppendLine($"=== TPS Save Debug ===  file={filePath}  edits={editList.Count}");
        foreach (var e in editList)
            diagLog.AppendLine($"  edit rec={e.RecordNumber} fields=[{string.Join(", ", e.Changes.Select(c => $"{c.FieldName}='{c.NewValue}'"))}]");

        if (editList.Count == 0) { File.WriteAllText(logPath, diagLog.ToString()); return new SaveResult(0, Array.Empty<string>(), logPath); }

        var fileBytes = File.ReadAllBytes(filePath);
        var warnings  = new List<string>();

        using var ms  = new MemoryStream(fileBytes.ToArray());
        var tpsFile   = new TpsFile(ms);
        var locations = BuildRecordLocations(tpsFile, tableNumber);
        diagLog.AppendLine($"locations map has {locations.Count} entries: [{string.Join(", ", locations.Keys.Take(10))}...]");

        // Pages that need full RLE re-encoding: page → working copy of decoded bytes
        var pageReencodes = new Dictionary<PageRleInfo, byte[]>();
        // Records staged for re-encoding per page; only counted as patched if re-encoding succeeds
        var reencodePendingPerPage = new Dictionary<PageRleInfo, HashSet<int>>();
        // Records whose changes have been committed (direct or successful re-encode)
        var changedRecordNumbers = new HashSet<int>();
        // Per-page verbose write log — only emitted if a no-op is detected
        var rleWriteLog = new Dictionary<PageRleInfo, List<string>>();

        foreach (var edit in editList)
        {
            if (!locations.TryGetValue(edit.RecordNumber, out var loc))
            {
                diagLog.AppendLine($"  rec{edit.RecordNumber}: NOT IN LOCATIONS MAP");
                warnings.Add($"Record {edit.RecordNumber}: could not locate in file.");
                continue;
            }
            diagLog.AppendLine($"  rec{edit.RecordNumber}: loc={loc}");

            bool directPatched = false;
            foreach (var change in edit.Changes)
            {
                var field = def.Fields.FirstOrDefault(f =>
                    string.Equals(f.Name, change.FieldName, StringComparison.OrdinalIgnoreCase));
                if (field is null)
                {
                    warnings.Add($"Record {edit.RecordNumber}: field '{change.FieldName}' not found in definition " +
                        $"(available: {string.Join(", ", def.Fields.Select(f => f.Name))}).");
                    continue;
                }

                var serialized = SerializeField(field, change.NewValue);
                if (serialized is null)
                {
                    warnings.Add($"Record {edit.RecordNumber} {field.Name}: could not serialize value '{change.NewValue}'.");
                    continue;
                }

                int copyLen = Math.Min(serialized.Length, field.Length);

                if (loc is DirectLoc dl)
                {
                    int directBytesWritten = 0;
                    for (int i = 0; i < copyLen; i++)
                    {
                        int relOff = field.Offset + i;
                        if (relOff < dl.FieldDataBase) continue; // byte is inherited from anchor — skip
                        int filePos = dl.ContentFileOffset + (relOff - dl.FieldDataBase);
                        if (filePos < 0 || filePos >= fileBytes.Length) continue;
                        fileBytes[filePos] = serialized[i];
                        directBytesWritten++;
                    }
                    if (directBytesWritten > 0)
                        directPatched = true;
                    else
                        warnings.Add(
                            $"Record {edit.RecordNumber} field '{field.Name}': 0 bytes written to file " +
                            $"(contentFileOffset={dl.ContentFileOffset} fdb={dl.FieldDataBase} " +
                            $"fieldOffset={field.Offset} fieldLen={field.Length} fileLen={fileBytes.Length}).");
                }
                else if (loc is RleLoc rl)
                {
                    // Always route ALL changes through workDec for RLE pages.
                    if (!pageReencodes.TryGetValue(rl.Page, out var workDec))
                    {
                        workDec = rl.Page.Decoded.ToArray();
                        pageReencodes[rl.Page] = workDec;
                    }

                    // Capture diagnostic info for this field write (used in no-op warning)
                    if (!rleWriteLog.TryGetValue(rl.Page, out var pageLog))
                        rleWriteLog[rl.Page] = pageLog = new List<string>();

                    int diagFirstDecIdx = -1, diagOldByte = -1, diagNewByte = -1, diagChangedBytes = 0;
                    bool anyOutOfRange = false;
                    int firstOobDecIdx = -1, firstOobRelOff = -1;

                    for (int i = 0; i < copyLen; i++)
                    {
                        int relOff = field.Offset + i;
                        int decIdx;
                        if (relOff < rl.FieldDataBase)
                        {
                            if (rl.Page.AnchorContentDecStart < 0) continue;
                            decIdx = rl.Page.AnchorContentDecStart + relOff;
                        }
                        else
                        {
                            decIdx = rl.ContentDecStart + (relOff - rl.FieldDataBase);
                        }
                        if (decIdx >= 0 && decIdx < workDec.Length)
                        {
                            if (diagFirstDecIdx < 0) { diagFirstDecIdx = decIdx; diagOldByte = workDec[decIdx]; diagNewByte = serialized[i]; }
                            if (workDec[decIdx] != serialized[i]) diagChangedBytes++;
                            workDec[decIdx] = serialized[i];
                        }
                        else if (!anyOutOfRange)
                        {
                            anyOutOfRange = true;
                            firstOobDecIdx = decIdx;
                            firstOobRelOff = relOff;
                        }
                    }

                    pageLog.Add(
                        $"  rec{edit.RecordNumber} '{field.Name}' newVal='{change.NewValue}' " +
                        $"offset={field.Offset} len={field.Length} copyLen={copyLen} " +
                        $"cds={rl.ContentDecStart} fdb={rl.FieldDataBase} " +
                        $"firstDecIdx={diagFirstDecIdx} oldByte={diagOldByte:X2} newByte={diagNewByte:X2} " +
                        $"changedBytes={diagChangedBytes}");

                    if (anyOutOfRange)
                        warnings.Add(
                            $"Record {edit.RecordNumber} field '{field.Name}': some bytes fell outside " +
                            $"the decoded page buffer (first: decIdx={firstOobDecIdx} relOff={firstOobRelOff} " +
                            $"cds={rl.ContentDecStart} fdb={rl.FieldDataBase} " +
                            $"anchorCds={rl.Page.AnchorContentDecStart} decoded.Length={workDec.Length}).");

                    if (!reencodePendingPerPage.TryGetValue(rl.Page, out var recSet))
                        reencodePendingPerPage[rl.Page] = recSet = new HashSet<int>();
                    recSet.Add(edit.RecordNumber);
                }
            }

            if (directPatched)
                changedRecordNumbers.Add(edit.RecordNumber);
        }

        // Apply page re-encodings; count records only on success
        foreach (var (pageInfo, workDec) in pageReencodes)
        {
            // Nothing actually changed in decoded data — no need to write, don't count as patched
            if (workDec.AsSpan().SequenceEqual(pageInfo.Decoded.AsSpan()))
            {
                diagLog.AppendLine($"  page {pageInfo.PageAddr:X6}: NO-OP (decoded unchanged)");
                if (reencodePendingPerPage.TryGetValue(pageInfo, out var noOpSet))
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append(
                        $"Page {pageInfo.PageAddr:X6} — decoded UNCHANGED after applying " +
                        $"{string.Join(", ", noOpSet.Select(r => $"rec {r}"))}. " +
                        $"anchorCds={pageInfo.AnchorContentDecStart} decoded.Length={pageInfo.Decoded.Length}.");
                    if (rleWriteLog.TryGetValue(pageInfo, out var pageWriteLog))
                    {
                        sb.Append("\nWrite details (changedBytes=0 means value already matched):");
                        foreach (var line in pageWriteLog) sb.Append("\n" + line);
                    }
                    warnings.Add(sb.ToString());
                }
                continue;
            }

            byte[] newComp = RleEncode(workDec);
            diagLog.AppendLine($"  page {pageInfo.PageAddr:X6}: re-encoded {newComp.Length} bytes (CompLen={pageInfo.CompLen})");
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
            // Re-encoding succeeded: mark these records as patched
            if (reencodePendingPerPage.TryGetValue(pageInfo, out var recSet))
                changedRecordNumbers.UnionWith(recSet);
        }

        int patched = changedRecordNumbers.Count;
        diagLog.AppendLine($"patched={patched} warnings={warnings.Count}");
        foreach (var w in warnings) diagLog.AppendLine($"  WARN: {w}");
        File.WriteAllText(logPath, diagLog.ToString());

        if (patched > 0)
            File.WriteAllBytes(filePath, fileBytes);

        return new SaveResult(patched, warnings, logPath);
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

                    int preamble        = isAnchor ? 5 : 1;
                    int storedPayload   = pt - pi;
                    int advance         = preamble + storedPayload;
                    int contentDecStart = decPos + preamble + Math.Max(0, ph - pi);
                    // FieldDataBase: first field.Offset whose byte is stored here (not inherited).
                    // For anchor (pi=0): always 0. For delta: max(0, pi − ph) because the stored
                    // field data begins at full-record byte max(pi, ph) = ph + max(0, pi − ph).
                    int fieldDataBase   = Math.Max(0, pi - ph);

                    if (drp.TableNumber == tableNumber)
                    {
                        if (isAnchor && pageInfo.AnchorContentDecStart < 0)
                            pageInfo.AnchorContentDecStart = contentDecStart;
                        if (contentDecStart < decoded.Length)
                            map[drp.RecordNumber] = new RleLoc(pageInfo, contentDecStart, fieldDataBase);
                    }

                    decPos += advance;
                }
            }
            else
            {
                // Non-RLE page: walk records sequentially just like the RLE path.
                // IndexOf(content) is unreliable for null/whitespace-heavy records because
                // the same byte pattern may appear at multiple positions in the page data.
                int decPos = 0;
                foreach (var rec in records)
                {
                    if (rec.PayloadType != RecordPayloadType.Data) { SkipRecordDec(rec, ref decPos); continue; }
                    if (rec.GetPayload() is not DataRecordPayload drp) { SkipRecordDec(rec, ref decPos); continue; }

                    int pt = rec.PayloadTotalLength;
                    int pi = rec.PayloadInheritedBytes;
                    int ph = rec.PayloadHeaderLength;
                    bool isAnch = rec.OwnsPayloadTotalLength;
                    int preamble = isAnch ? 5 : 1;
                    int contentDecStart = decPos + preamble + Math.Max(0, ph - pi);
                    int fieldDataBase   = Math.Max(0, pi - ph);
                    int advance         = preamble + (pt - pi);

                    if (drp.TableNumber == tableNumber && contentDecStart < compData.Length)
                        map[drp.RecordNumber] = new DirectLoc(pageDataStart + contentDecStart, fieldDataBase);

                    decPos += advance;
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
