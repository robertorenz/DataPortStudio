// TpsTest — validates TpsWriter fix for null FString fields (delta record + inherited bytes)
// Usage: dotnet run -- [path-to-file.tps]
using TpsParser;
using System.Collections.Immutable;
using System.Text;

return TpsTest.Run(args);

static class TpsTest
{
    public static int Run(string[] args)
    {
        string tpsPath = args.Length > 0 ? args[0] : @"C:\ai\dataportstudio\queries.tps";
        Console.WriteLine($"=== TpsWriter diagnostic: {Path.GetFileName(tpsPath)} ===");
        if (!File.Exists(tpsPath)) { Console.Error.WriteLine($"File not found: {tpsPath}"); return 1; }

        var origBytes = File.ReadAllBytes(tpsPath);

        // ---- 1. Schema --------------------------------------------------------
        int tableNumber;
        ImmutableArray<FieldDefinition> tableFields;
        {
            using var ms = new MemoryStream(origBytes.ToArray());
            var tf = new TpsFile(ms);
            tableNumber = tf.GetTableDefinitions().Keys.First();
            tableFields = tf.GetTableDefinitions()[tableNumber].Fields;
            Console.WriteLine($"\nTable {tableNumber}: {tableFields.Length} fields");
            foreach (var f in tableFields)
                Console.WriteLine($"  [{f.Offset,3}] {f.Name,-20} ({f.TypeCode}) len={f.Length}");
        }

        // ---- 2. Location map (anchor / delta info) ----------------------------
        Console.WriteLine("\n--- Record location map (anchor / delta records) ---");
        var locations = BuildLocations(origBytes, tableNumber, verbose: true);
        Console.WriteLine($"Total located records: {locations.Count}");

        // ---- 3. Pick FString field to test ------------------------------------
        var fOpt = tableFields.FirstOrDefault(
            f => string.Equals(f.Name, "CLASSNAME", StringComparison.OrdinalIgnoreCase));
        if (fOpt.Name == null)
            fOpt = tableFields.FirstOrDefault(f => f.TypeCode == FieldTypeCode.FString);
        if (fOpt.Name == null) { Console.WriteLine("No FString field — skip write test."); return 0; }
        var fld = fOpt;
        Console.WriteLine($"\nTest field: [{fld.Offset}] {fld.Name} len={fld.Length}");

        // ---- 4. Find null vs non-null records --------------------------------
        List<int> nullRecs, nonNullRecs;
        {
            using var ms = new MemoryStream(origBytes.ToArray());
            var tbl = Table.MaterializeFromFile(new TpsFile(ms), tableNumber);
            nullRecs = tbl.Rows
                .Where(r => { r.Values.TryGetValue(fld.Name, out var v); return string.IsNullOrWhiteSpace(v?.ToString()); })
                .Select(r => r.RecordNumber).Take(5).ToList();
            nonNullRecs = tbl.Rows
                .Where(r => { r.Values.TryGetValue(fld.Name, out var v); return !string.IsNullOrWhiteSpace(v?.ToString()); })
                .Select(r => r.RecordNumber).Take(3).ToList();
        }
        Console.WriteLine($"Null {fld.Name} records (sample): [{string.Join(", ", nullRecs)}]");
        Console.WriteLine($"Non-null {fld.Name} records (control): [{string.Join(", ", nonNullRecs)}]");

        // ---- 5. Write→read round-trip ----------------------------------------
        Console.WriteLine("\n--- Write→read round-trip test ---");
        int pass = 0, fail = 0;

        foreach (int recNo in nullRecs.Concat(nonNullRecs))
        {
            string origVal;
            {
                using var ms = new MemoryStream(origBytes.ToArray());
                var tbl = Table.MaterializeFromFile(new TpsFile(ms), tableNumber);
                var row = tbl.Rows.FirstOrDefault(r => r.RecordNumber == recNo);
                origVal = row?.Values.GetValueOrDefault(fld.Name)?.ToString() ?? "";
            }

            string testVal = $"T{recNo}";
            bool isNull = string.IsNullOrWhiteSpace(origVal);

            string tmpPath = tpsPath + $".test{recNo}.tps";
            File.WriteAllBytes(tmpPath, origBytes);

            var (patched, warns) = DoSave(tmpPath, tableNumber, fld, recNo, testVal);

            string newVal;
            {
                using var ms = new MemoryStream(File.ReadAllBytes(tmpPath));
                var tbl = Table.MaterializeFromFile(new TpsFile(ms), tableNumber);
                newVal = tbl.Rows.FirstOrDefault(r => r.RecordNumber == recNo)
                    ?.Values.GetValueOrDefault(fld.Name)?.ToString() ?? "";
            }
            File.Delete(tmpPath);

            bool ok = newVal.TrimEnd() == testVal;
            if (ok) pass++; else fail++;
            Console.WriteLine(
                $"  Rec#{recNo,5} ({(isNull ? "null" : "val ")}) " +
                $"orig=[{origVal.TrimEnd()}] testVal=[{testVal}] read=[{newVal.TrimEnd()}]  " +
                $"patched={patched} warns={warns.Count}  {(ok ? "PASS" : "FAIL")}");
            foreach (var w in warns) Console.WriteLine($"    WARN: {w}");
        }

        Console.WriteLine($"\nResult: {pass} PASS, {fail} FAIL");

        // ---- 6. Synthetic null test: blank a record's FString in the encoded page,
        //         then verify our writer can restore it (simulates user's null scenario) ----
        Console.WriteLine("\n--- Synthetic null FString test ---");
        // Must pick records that are on RLE pages (IsRle=true) for this test to work
        var rleLocations = BuildLocations(origBytes, tableNumber, verbose: false);
        var rleNonNullRecs = nonNullRecs.Where(r => rleLocations.TryGetValue(r, out var l) && l.IsRle)
            .Concat(locations.Where(kv => kv.Value.IsRle)
                .Select(kv => kv.Key)
                .Where(r => {
                    using var ms = new MemoryStream(origBytes.ToArray());
                    var tbl = Table.MaterializeFromFile(new TpsFile(ms), tableNumber);
                    var v = tbl.Rows.FirstOrDefault(row => row.RecordNumber == r)
                        ?.Values.GetValueOrDefault(fld.Name)?.ToString() ?? "";
                    return !string.IsNullOrWhiteSpace(v);
                })
                .Take(3))
            .Distinct().Take(2).ToList();
        Console.WriteLine($"Synthetic test candidates (RLE, non-null): [{string.Join(", ", rleNonNullRecs)}]");
        int synPass = 0, synFail = 0;
        foreach (int recNo in rleNonNullRecs)
        {
            string synthPath = tpsPath + $".synth{recNo}.tps";
            File.WriteAllBytes(synthPath, origBytes);
            var blanked = BlankFieldInFile(synthPath, tableNumber, fld, recNo);
            if (!blanked) { Console.WriteLine($"  Rec#{recNo}: could not blank field — skip."); continue; }

            // Verify blank was effective
            string blankVal;
            {
                using var ms = new MemoryStream(File.ReadAllBytes(synthPath));
                var tbl = Table.MaterializeFromFile(new TpsFile(ms), tableNumber);
                blankVal = tbl.Rows.FirstOrDefault(r => r.RecordNumber == recNo)
                    ?.Values.GetValueOrDefault(fld.Name)?.ToString() ?? "";
            }
            Console.WriteLine($"  Rec#{recNo}: blanked to [{blankVal.TrimEnd()}]");

            // Now restore with our DoSave
            string restoreVal = "RESTORED";
            var (patched2, warns2) = DoSave(synthPath, tableNumber, fld, recNo, restoreVal);

            string readBack;
            {
                using var ms = new MemoryStream(File.ReadAllBytes(synthPath));
                var tbl = Table.MaterializeFromFile(new TpsFile(ms), tableNumber);
                readBack = tbl.Rows.FirstOrDefault(r => r.RecordNumber == recNo)
                    ?.Values.GetValueOrDefault(fld.Name)?.ToString() ?? "";
            }
            File.Delete(synthPath);

            bool ok = readBack.TrimEnd() == restoreVal;
            if (ok) synPass++; else synFail++;
            Console.WriteLine($"  Rec#{recNo}: null→[{restoreVal}] read=[{readBack.TrimEnd()}]  " +
                              $"patched={patched2} warns={warns2.Count}  {(ok ? "PASS" : "FAIL")}");
            foreach (var w in warns2) Console.WriteLine($"    WARN: {w}");
        }
        Console.WriteLine($"Synthetic result: {synPass} PASS, {synFail} FAIL");

        int total = fail + synFail;
        return total == 0 ? 0 : 1;
    }

    // Blank all bytes of a field for a record directly in the decoded page (simulate null)
    static bool BlankFieldInFile(string path, int tableNum, FieldDefinition field, int recNo)
    {
        var fileBytes = File.ReadAllBytes(path);
        var locs = BuildLocations(fileBytes, tableNum, verbose: false);
        if (!locs.TryGetValue(recNo, out var loc)) return false;
        if (!loc.IsRle || loc.Decoded == null) return false;

        var workDec = (byte[])loc.Decoded.Clone();
        int baseIdx = (loc.FieldDataBase > field.Offset)
            ? (loc.AnchorContentDecStart >= 0 ? loc.AnchorContentDecStart : -1)
            : loc.ContentDecStart;
        int fdbAdj = Math.Max(0, loc.FieldDataBase - field.Offset);
        if (baseIdx < 0) return false;

        for (int i = 0; i < field.Length; i++)
        {
            int relOff = field.Offset + i;
            int decIdx = (relOff < loc.FieldDataBase && loc.AnchorContentDecStart >= 0)
                ? loc.AnchorContentDecStart + relOff
                : loc.ContentDecStart + (relOff - loc.FieldDataBase);
            if (decIdx >= 0 && decIdx < workDec.Length)
                workDec[decIdx] = (byte)' '; // blank with spaces
        }

        if (workDec.AsSpan().SequenceEqual(loc.Decoded.AsSpan())) return false; // already blank

        byte[] newComp = RleEncode(workDec);
        if (newComp.Length > loc.CompLen) return false; // can't fit

        Array.Copy(newComp, 0, fileBytes, loc.PageDataStart, newComp.Length);
        if (newComp.Length < loc.CompLen)
        {
            Array.Clear(fileBytes, loc.PageDataStart + newComp.Length, loc.CompLen - newComp.Length);
            // Update page header so TpsParser reads the correct length (avoids Bad RLE Skip on zeros)
            int hdr = loc.PageDataStart - loc.PageAddr;
            int npg = hdr + newComp.Length;
            fileBytes[loc.PageAddr + 4] = (byte)(npg & 0xFF);
            fileBytes[loc.PageAddr + 5] = (byte)(npg >> 8);
        }
        File.WriteAllBytes(path, fileBytes);
        return true;
    }

    // =================== location map =========================================

    sealed class LocInfo
    {
        public bool IsRle;
        public int ContentDecStart;
        public int FieldDataBase;
        public int AnchorContentDecStart = -1;
        public byte[]? Decoded;
        public int PageDataStart;
        public int PageAddr;
        public int CompLen;
    }

    static Dictionary<int, LocInfo> BuildLocations(byte[] fileBytes, int tableNumber, bool verbose)
    {
        var map = new Dictionary<int, LocInfo>();
        using var ms = new MemoryStream(fileBytes.ToArray());
        var tpsFile = new TpsFile(ms);

        foreach (var block in tpsFile.GetBlocks())
        foreach (var page in block.GetPages())
        {
            IReadOnlyList<TpsRecord> records;
            try { records = page.GetRecords(ErrorHandlingOptions.Default); }
            catch { continue; }

            int headerSize    = (int)page.Size - page.CompressedData.Length;
            int pageDataStart = (int)page.AbsoluteAddress + headerSize;
            var compData      = page.CompressedData.ToArray();

            byte[]? decoded = null;
            try
            {
                (decoded, _) = RleDecode(compData);
                if (decoded.Length == compData.Length && decoded.AsSpan().SequenceEqual(compData))
                    decoded = null;
            }
            catch { decoded = null; }

            if (decoded != null)
            {
                int anchorCds = -1, decPos = 0;
                foreach (var rec in records)
                {
                    if (rec.PayloadType != RecordPayloadType.Data) { SkipDec(rec, ref decPos); continue; }
                    if (rec.GetPayload() is not DataRecordPayload drp) { SkipDec(rec, ref decPos); continue; }

                    int pt = rec.PayloadTotalLength, pi = rec.PayloadInheritedBytes, ph = rec.PayloadHeaderLength;
                    bool isAnch = rec.OwnsPayloadTotalLength;
                    int advance = (isAnch ? 5 : 1) + (pt - pi);
                    int cds     = decPos + (isAnch ? 5 : 1) + Math.Max(0, ph - pi);
                    int fdb     = Math.Max(0, pi - ph);

                    if (drp.TableNumber == tableNumber)
                    {
                        if (isAnch && anchorCds < 0) anchorCds = cds;
                        if (cds < decoded.Length)
                        {
                            map[drp.RecordNumber] = new LocInfo
                            {
                                IsRle = true, ContentDecStart = cds,
                                FieldDataBase = fdb, AnchorContentDecStart = anchorCds,
                                Decoded = decoded, PageDataStart = pageDataStart,
                                PageAddr = (int)page.AbsoluteAddress, CompLen = compData.Length
                            };
                            if (verbose && (isAnch || pi > 0 || fdb > 0))
                                Console.WriteLine($"  Rec#{drp.RecordNumber,5} " +
                                    $"{(isAnch ? "ANCHOR" : "delta ")} " +
                                    $"pi={pi,3} ph={ph,3} fdb={fdb,3} cds={cds,5} anchorCds={anchorCds,5}");
                        }
                    }
                    decPos += advance;
                }
            }
            else
            {
                // Non-RLE: sequential walk (same as RLE) — IndexOf is unreliable for null/whitespace records.
                int decPos = 0;
                foreach (var rec in records)
                {
                    if (rec.PayloadType != RecordPayloadType.Data) { SkipDec(rec, ref decPos); continue; }
                    if (rec.GetPayload() is not DataRecordPayload drp) { SkipDec(rec, ref decPos); continue; }
                    int pt = rec.PayloadTotalLength, pi = rec.PayloadInheritedBytes, ph = rec.PayloadHeaderLength;
                    bool isAnch = rec.OwnsPayloadTotalLength;
                    int preamble = isAnch ? 5 : 1;
                    int cds = decPos + preamble + Math.Max(0, ph - pi);
                    int fdb = Math.Max(0, pi - ph);
                    int advance = preamble + (pt - pi);
                    if (drp.TableNumber == tableNumber && cds < compData.Length)
                        map[drp.RecordNumber] = new LocInfo
                        {
                            IsRle = false, ContentDecStart = pageDataStart + cds,
                            FieldDataBase = fdb, AnchorContentDecStart = -1,
                            Decoded = null, PageDataStart = pageDataStart,
                            PageAddr = (int)page.AbsoluteAddress, CompLen = compData.Length
                        };
                    decPos += advance;
                }
            }
        }
        return map;
    }

    // =================== write logic ==========================================

    static (int Patched, IReadOnlyList<string> Warnings) DoSave(
        string filePath, int tableNum, FieldDefinition field, int recordNumber, string newValue)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        var warnings  = new List<string>();
        var locations = BuildLocations(fileBytes, tableNum, verbose: false);

        if (!locations.TryGetValue(recordNumber, out var loc))
        {
            warnings.Add($"Record {recordNumber}: could not locate in file.");
            return (0, warnings);
        }

        var serialized = SerializeFixed(newValue, field.Length);
        int copyLen    = Math.Min(serialized.Length, field.Length);

        if (!loc.IsRle)
        {
            for (int i = 0; i < copyLen; i++)
            {
                int relOff = field.Offset + i;
                if (relOff < loc.FieldDataBase) continue;
                int filePos = loc.ContentDecStart + (relOff - loc.FieldDataBase);
                if (filePos < 0 || filePos >= fileBytes.Length) continue;
                fileBytes[filePos] = serialized[i];
            }
            File.WriteAllBytes(filePath, fileBytes);
            return (1, warnings);
        }

        // RLE path
        if (loc.Decoded == null) { warnings.Add("Page decode failed."); return (0, warnings); }
        var workDec = (byte[])loc.Decoded.Clone();

        for (int i = 0; i < copyLen; i++)
        {
            int relOff = field.Offset + i;
            int decIdx;
            if (relOff < loc.FieldDataBase)
            {
                if (loc.AnchorContentDecStart < 0) continue;
                decIdx = loc.AnchorContentDecStart + relOff;
            }
            else
            {
                decIdx = loc.ContentDecStart + (relOff - loc.FieldDataBase);
            }
            if (decIdx >= 0 && decIdx < workDec.Length)
                workDec[decIdx] = serialized[i];
        }

        if (workDec.AsSpan().SequenceEqual(loc.Decoded.AsSpan()))
        {
            warnings.Add($"Record {recordNumber} {field.Name}: decoded bytes unchanged — field may be fully inherited with no anchor found.");
            return (0, warnings);
        }

        byte[] newComp = RleEncode(workDec);
        if (newComp.Length > loc.CompLen)
        {
            warnings.Add($"Re-encoded {newComp.Length} bytes, page holds {loc.CompLen} bytes — too large.");
            return (0, warnings);
        }
        Array.Copy(newComp, 0, fileBytes, loc.PageDataStart, newComp.Length);
        if (newComp.Length < loc.CompLen)
        {
            Array.Clear(fileBytes, loc.PageDataStart + newComp.Length, loc.CompLen - newComp.Length);
            int hdr = loc.PageDataStart - loc.PageAddr;
            int npg = hdr + newComp.Length;
            fileBytes[loc.PageAddr + 4] = (byte)(npg & 0xFF);
            fileBytes[loc.PageAddr + 5] = (byte)(npg >> 8);
        }
        File.WriteAllBytes(filePath, fileBytes);
        return (1, warnings);
    }

    // =================== RLE & helpers ========================================

    static (byte[] dec, int[] d2e) RleDecode(byte[] comp)
    {
        var dec  = new List<byte>(); var offs = new List<int>();
        int pos  = 0; byte lastLit = 0;
        int ReadCount() {
            if (pos >= comp.Length) return 0;
            int b = comp[pos++]; if (b < 128) return b;
            if (pos >= comp.Length) return b & 0x7F;
            return (b & 0x7F) | (comp[pos++] << 7);
        }
        while (pos < comp.Length) {
            int lc = ReadCount(), lb = pos;
            for (int i = 0; i < lc; i++) { dec.Add(comp[pos]); offs.Add(pos); pos++; }
            if (lc > 0) lastLit = comp[lb + lc - 1];
            if (pos >= comp.Length) break;
            int rc = ReadCount();
            for (int i = 0; i < rc; i++) { dec.Add(lastLit); offs.Add(-1); }
        }
        return (dec.ToArray(), offs.ToArray());
    }

    static byte[] RleEncode(byte[] dec)
    {
        var out2 = new List<byte>(); int pos = 0;
        void EmitCount(int n) { if (n < 128) out2.Add((byte)n); else { out2.Add((byte)(0x80|(n&0x7F))); out2.Add((byte)(n>>7)); } }
        while (pos < dec.Length) {
            int ls = pos;
            while (pos < dec.Length) {
                if (pos+2 < dec.Length && dec[pos]==dec[pos+1] && dec[pos+1]==dec[pos+2]) { pos++; break; }
                if (pos+1 < dec.Length && dec[pos]==dec[pos+1] && pos+2>=dec.Length) { pos+=2; break; }
                pos++;
            }
            int lc = pos-ls; EmitCount(lc); for (int i=ls; i<pos; i++) out2.Add(dec[i]);
            int rc = 0;
            if (pos > 0 && pos < dec.Length) { byte rv=dec[pos-1]; while (pos<dec.Length && dec[pos]==rv){rc++;pos++;} }
            if (rc>0||pos<dec.Length) EmitCount(rc);
        }
        return out2.ToArray();
    }

    static byte[] SerializeFixed(string s, int fieldLength)
    {
        var raw = Encoding.Latin1.GetBytes(s);
        var buf = new byte[fieldLength];
        Array.Fill(buf, (byte)' ');
        Array.Copy(raw, 0, buf, 0, Math.Min(raw.Length, fieldLength));
        return buf;
    }

    static void SkipDec(TpsRecord rec, ref int decPos)
    {
        int pt = rec.PayloadTotalLength, pi = rec.PayloadInheritedBytes;
        decPos += rec.OwnsPayloadTotalLength ? 5 + pt : 1 + (pt - pi);
    }

    static int IndexOf(byte[] hay, byte[] needle) {
        if (needle.Length == 0) return 0;
        int lim = hay.Length - needle.Length;
        for (int i = 0; i <= lim; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
