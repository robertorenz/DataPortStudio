using System.Data;
using System.IO;
using System.Text;

namespace DataPortStudio.Services;

/// <summary>
/// Reads and writes classic Clarion (.dat) files — the original Clarion ISAM format, NOT TopSpeed (.tps).
///
/// Clean-room implementation of the file layout documented in Clarion Technical Bulletin 117
/// (public): a fixed 85-byte header, one 27-byte descriptor per field, then fixed-length records
/// (each prefixed by a 5-byte record header) starting at the header's <c>offset</c>. Output is a
/// plain <see cref="DataTable"/> with the same shape as <see cref="TpsService"/>, so the tree,
/// viewer, Objects tab, Clarion date/time detection and copy-to-SQL all reuse unchanged.
///
/// Editing: cell edits, row deletes and row inserts are all supported and written back to the binary
/// file. Keys/indexes (.K??/.I??) and memos (.MEM) are NOT updated — index files will be stale after
/// edits (same limitation as TPS editing). Read those files back in Clarion to rebuild indexes.
/// </summary>
public static class DatService
{
    private const string Extension = ".dat";
    private const ushort Signature = 0x3343;   // "C3" — Clarion Professional Developer v2+
    private const int HeaderLength = 85;
    private const int FieldDescriptorLength = 27;
    private const int RecordHeaderLength = 5;   // rhd (1) + rptr (4)

    /// <summary>Hidden column added to every DataTable storing the file-slot index for each row.</summary>
    public const string SlotColumn = "__DAT_SLOT__";

    // Classic Clarion data is single-byte (typically code page 437/1252); Latin1 never throws.
    private static readonly Encoding TextEncoding = Encoding.Latin1;

    private enum FieldType : byte
    {
        Long = 1, Real = 2, String = 3, StringPicture = 4,
        Byte = 5, Short = 6, Group = 7, Decimal = 8
    }

    private sealed record Field(FieldType Type, string Name, int Offset, int Length, int Significance, int Decimals);

    /// <summary>Lists the .dat files in a folder as table names (file name without extension), sorted.</summary>
    public static List<string> ListTables(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new List<string>();

        var names = Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Confirms the folder exists and contains at least one .dat file.</summary>
    public static void TestConnection(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Choose a folder that contains .dat files.");
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        if (ListTables(folder).Count == 0)
            throw new FileNotFoundException($"No .dat files were found in {folder}.");
    }

    private static string ResolvePath(string folder, string tableName)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("No Clarion DAT folder is set for this connection.");

        var direct = Path.Combine(folder, tableName + Extension);
        if (File.Exists(direct)) return direct;

        var match = Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), tableName, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new FileNotFoundException($"'{tableName}{Extension}' was not found in {folder}.");
    }

    /// <summary>
    /// Reads a .dat file into a DataTable. <paramref name="rowLimit"/> caps the rows materialized
    /// (0 = structure only). Columns always come from the field descriptors, so empty files still
    /// produce a typed, copyable schema.
    /// </summary>
    public static DataTable ReadTable(string folder, string tableName, int rowLimit)
    {
        var path = ResolvePath(folder, tableName);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderLength)
            throw new InvalidOperationException($"'{tableName}{Extension}' is too small to be a Clarion DAT file.");

        var sig = ReadU16(bytes, 0);
        if (sig != Signature)
            throw new InvalidOperationException(
                $"'{tableName}{Extension}' is not a recognized Clarion DAT file (signature 0x{sig:X4}, expected 0x{Signature:X4}).");

        int numFields = ReadU16(bytes, 13);
        int recLen = ReadU16(bytes, 19);
        long dataOffset = ReadU32(bytes, 21);

        var fields = ReadFieldDescriptors(bytes, numFields);

        var table = new DataTable(tableName);
        BuildColumns(table, fields);

        // Hidden slot-index column — stores which file slot each row came from (used by SaveTable).
        var slotCol = table.Columns.Add(SlotColumn, typeof(int));
        slotCol.DefaultValue = -1;

        if (rowLimit > 0 && recLen > RecordHeaderLength && dataOffset > 0)
        {
            var count = 0;
            var slotIndex = 0;
            for (long pos = dataOffset; pos + recLen <= bytes.Length && count < rowLimit; pos += recLen, slotIndex++)
            {
                var status = bytes[pos];
                // Skip deleted (bit 4) and empty/free (no status bits set) slots.
                if (status == 0 || (status & 0x10) != 0) continue;

                var fieldBase = (int)(pos + RecordHeaderLength);
                var dr = table.NewRow();
                foreach (var f in fields)
                    dr[f.Name] = ReadValue(bytes, fieldBase + f.Offset, f, table.Columns[f.Name]!.DataType);
                dr[SlotColumn] = slotIndex;
                table.Rows.Add(dr);
                count++;
            }
        }

        table.AcceptChanges();
        return table;
    }

    private static List<Field> ReadFieldDescriptors(byte[] b, int numFields)
    {
        var fields = new List<Field>(numFields);
        for (var i = 0; i < numFields; i++)
        {
            var p = HeaderLength + i * FieldDescriptorLength;
            if (p + FieldDescriptorLength > b.Length) break;

            var type = (FieldType)b[p];
            var rawName = TextEncoding.GetString(b, p + 1, 16);
            var name = StripPrefix(rawName);
            int offset = ReadU16(b, p + 17);
            int length = ReadU16(b, p + 19);
            int significance = b[p + 21];
            int decimals = b[p + 22];
            fields.Add(new Field(type, name, offset, length, significance, decimals));
        }
        return fields;
    }

    /// <summary>Field names carry the file prefix ("PHN:NAME"); show just the field part.</summary>
    private static string StripPrefix(string rawName)
    {
        var name = rawName.Trim();
        var colon = name.IndexOf(':');
        return colon >= 0 ? name[(colon + 1)..].Trim() : name;
    }

    private static void BuildColumns(DataTable table, List<Field> fields)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var idx = 0; idx < fields.Count; idx++)
        {
            var f = fields[idx];
            var name = string.IsNullOrEmpty(f.Name) ? "Field" : f.Name;
            var unique = name;
            var i = 2;
            while (!used.Add(unique)) unique = $"{name}_{i++}";

            var dc = table.Columns.Add(unique, ClrType(f));
            if (dc.DataType == typeof(string) && f.Length > 0) dc.MaxLength = f.Length;
            if (dc.DataType == typeof(decimal))
            {
                dc.ExtendedProperties["prec"] = Math.Max(1, f.Significance + f.Decimals);
                dc.ExtendedProperties["scale"] = f.Decimals;
            }
            // Re-key the field to the (possibly de-duplicated) column name for the value pass.
            if (!string.Equals(unique, f.Name, StringComparison.Ordinal))
                fields[idx] = f with { Name = unique };
        }
    }

    private static Type ClrType(Field f) => f.Type switch
    {
        FieldType.Long => typeof(int),
        FieldType.Real => f.Length == 4 ? typeof(float) : typeof(double),
        FieldType.Byte => typeof(byte),
        FieldType.Short => typeof(short),
        FieldType.Decimal => typeof(decimal),
        _ => typeof(string) // String / StringPicture / Group
    };

    private static object ReadValue(byte[] b, int pos, Field f, Type columnType)
    {
        if (pos < 0 || pos + f.Length > b.Length) return DBNull.Value;

        switch (f.Type)
        {
            case FieldType.Long:
                return ReadI32(b, pos);
            case FieldType.Real:
                return f.Length == 4 ? BitConverter.ToSingle(b, pos) : BitConverter.ToDouble(b, pos);
            case FieldType.Byte:
                return b[pos];
            case FieldType.Short:
                return BitConverter.ToInt16(b, pos);
            case FieldType.Decimal:
                return ReadPackedDecimal(b, pos, f);
            default: // String / StringPicture / Group
                return TextEncoding.GetString(b, pos, f.Length).TrimEnd('\0', ' ');
        }
    }

    /// <summary>
    /// Decodes a Clarion packed-BCD DECIMAL: two digits per byte, most-significant first, with
    /// <c>Decimals</c> fractional places and <c>Significance</c> integer digits. A non-zero leading
    /// (sign) nibble marks the value negative.
    /// </summary>
    private static decimal ReadPackedDecimal(byte[] b, int pos, Field f)
    {
        // Unpack all nibbles, most-significant first.
        var nibbles = new int[f.Length * 2];
        for (var i = 0; i < f.Length; i++)
        {
            nibbles[i * 2] = (b[pos + i] & 0xF0) >> 4;
            nibbles[i * 2 + 1] = b[pos + i] & 0x0F;
        }

        var digits = f.Significance + f.Decimals;
        if (digits <= 0) digits = nibbles.Length;          // fall back to all nibbles
        if (digits > nibbles.Length) digits = nibbles.Length;

        // The least-significant 'digits' nibbles hold the number; the nibble just above is the sign.
        var start = nibbles.Length - digits;
        decimal value = 0;
        for (var i = start; i < nibbles.Length; i++)
            value = value * 10 + nibbles[i];

        if (f.Decimals > 0)
            value /= (decimal)Math.Pow(10, f.Decimals);

        if (start > 0 && nibbles[start - 1] != 0)
            value = -value;

        return value;
    }

    /// <summary>
    /// Writes a set of DataTable changes (Modified / Deleted / Added rows) back to the .dat file.
    /// <para>UPDATE: overwrites field bytes in the existing slot.</para>
    /// <para>DELETE: sets the deleted flag (bit 4) on the slot's status byte.</para>
    /// <para>INSERT: fills the first free slot found, or appends a new slot at end-of-file.</para>
    /// <para>The file's numRecs header field is updated accordingly.</para>
    /// <para>Note: key/index files (.K??/.I??) are NOT modified — rebuild them in Clarion.</para>
    /// </summary>
    public static void SaveTable(string folder, string tableName, DataTable changes)
    {
        var path = ResolvePath(folder, tableName);
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length < HeaderLength)
            throw new InvalidOperationException($"'{tableName}{Extension}' is too small to be a valid Clarion DAT file.");

        int numFields = ReadU16(bytes, 13);
        int recLen    = ReadU16(bytes, 19);
        long dataOffset = ReadU32(bytes, 21);
        var fields = ReadFieldDescriptors(bytes, numFields);

        if (recLen <= RecordHeaderLength || dataOffset <= 0)
            throw new InvalidOperationException($"Cannot write to '{tableName}{Extension}' — invalid record layout.");

        int dataBodyLen = recLen - RecordHeaderLength; // usable field bytes per record
        int deltaRecs = 0;

        foreach (DataRow row in changes.Rows)
        {
            switch (row.RowState)
            {
                case DataRowState.Modified:
                {
                    var slot = (int)row[SlotColumn, DataRowVersion.Original];
                    var bytePos = (int)(dataOffset + (long)slot * recLen);
                    if (bytePos + recLen > bytes.Length) break;
                    WriteFields(bytes, bytePos + RecordHeaderLength, fields, row, DataRowVersion.Current);
                    break;
                }

                case DataRowState.Deleted:
                {
                    var slot = (int)row[SlotColumn, DataRowVersion.Original];
                    var bytePos = (int)(dataOffset + (long)slot * recLen);
                    if (bytePos >= bytes.Length) break;
                    bytes[bytePos] |= 0x10; // set deleted bit
                    deltaRecs--;
                    break;
                }

                case DataRowState.Added:
                {
                    // Look for a free (status == 0) slot to reuse.
                    long freeSlot = -1;
                    long totalSlots = ((long)bytes.Length - dataOffset) / recLen;
                    for (long s = 0; s < totalSlots; s++)
                    {
                        var sp = (int)(dataOffset + s * recLen);
                        if (bytes[sp] == 0) { freeSlot = s; break; }
                    }

                    if (freeSlot >= 0)
                    {
                        // Reuse the free slot.
                        var bytePos = (int)(dataOffset + freeSlot * recLen);
                        bytes[bytePos] = 0x01;
                        // Clear rptr (bytes 1-4 of record header).
                        bytes[bytePos + 1] = bytes[bytePos + 2] = bytes[bytePos + 3] = bytes[bytePos + 4] = 0;
                        // Blank field area with spaces (strings) / zeros (numeric).
                        for (var i = 0; i < dataBodyLen; i++) bytes[bytePos + RecordHeaderLength + i] = (byte)' ';
                        WriteFields(bytes, bytePos + RecordHeaderLength, fields, row, DataRowVersion.Current);
                    }
                    else
                    {
                        // Append a brand-new slot at the end of the file.
                        var newBytes = new byte[bytes.Length + recLen];
                        Array.Copy(bytes, newBytes, bytes.Length);
                        var bytePos = bytes.Length;
                        newBytes[bytePos] = 0x01;
                        for (var i = 0; i < dataBodyLen; i++) newBytes[bytePos + RecordHeaderLength + i] = (byte)' ';
                        WriteFields(newBytes, bytePos + RecordHeaderLength, fields, row, DataRowVersion.Current);
                        bytes = newBytes;
                    }
                    deltaRecs++;
                    break;
                }
            }
        }

        // Update the numRecs field in the header (bytes 5-8, little-endian U32).
        long currentNum = ReadU32(bytes, 5);
        var newNum = (uint)Math.Max(0, currentNum + deltaRecs);
        bytes[5] = (byte) (newNum        & 0xFF);
        bytes[6] = (byte)((newNum >>  8) & 0xFF);
        bytes[7] = (byte)((newNum >> 16) & 0xFF);
        bytes[8] = (byte)((newNum >> 24) & 0xFF);

        File.WriteAllBytes(path, bytes);
    }

    private static void WriteFields(byte[] bytes, int fieldBase, List<Field> fields, DataRow row, DataRowVersion ver)
    {
        foreach (var f in fields)
        {
            if (!row.Table.Columns.Contains(f.Name)) continue;
            var value = row[f.Name, ver];
            WriteValue(bytes, fieldBase + f.Offset, f, value);
        }
    }

    private static void WriteValue(byte[] bytes, int pos, Field f, object value)
    {
        if (pos < 0 || pos + f.Length > bytes.Length) return;

        switch (f.Type)
        {
            case FieldType.Long:
            {
                var v = value is DBNull ? 0 : Convert.ToInt32(value);
                var b = BitConverter.GetBytes(v);
                Array.Copy(b, 0, bytes, pos, 4);
                break;
            }
            case FieldType.Short:
            {
                var v = value is DBNull ? (short)0 : Convert.ToInt16(value);
                var b = BitConverter.GetBytes(v);
                Array.Copy(b, 0, bytes, pos, 2);
                break;
            }
            case FieldType.Byte:
                bytes[pos] = value is DBNull ? (byte)0 : Convert.ToByte(value);
                break;
            case FieldType.Real:
                if (f.Length == 4)
                {
                    var v = value is DBNull ? 0f : Convert.ToSingle(value);
                    Array.Copy(BitConverter.GetBytes(v), 0, bytes, pos, 4);
                }
                else
                {
                    var v = value is DBNull ? 0.0 : Convert.ToDouble(value);
                    Array.Copy(BitConverter.GetBytes(v), 0, bytes, pos, 8);
                }
                break;
            case FieldType.Decimal:
                WritePackedDecimal(bytes, pos, f, value is DBNull ? 0m : Convert.ToDecimal(value));
                break;
            default: // String / StringPicture / Group
            {
                var s = value is DBNull ? "" : (value.ToString() ?? "");
                var encoded = TextEncoding.GetBytes(s);
                // Pad with spaces, then copy the encoded string (no null terminator).
                for (var i = 0; i < f.Length; i++) bytes[pos + i] = (byte)' ';
                Array.Copy(encoded, 0, bytes, pos, Math.Min(encoded.Length, f.Length));
                break;
            }
        }
    }

    private static void WritePackedDecimal(byte[] bytes, int pos, Field f, decimal value)
    {
        var nibbles = new int[f.Length * 2]; // all zeros

        var digits = f.Significance + f.Decimals;
        if (digits <= 0) digits = nibbles.Length;
        if (digits > nibbles.Length) digits = nibbles.Length;

        var negative = value < 0;
        if (negative) value = -value;

        // Scale to integer representation.
        if (f.Decimals > 0) value *= (decimal)Math.Pow(10, f.Decimals);
        var intValue = (long)Math.Round(value, MidpointRounding.AwayFromZero);

        // Write digit nibbles LSB → MSB into the last 'digits' positions.
        var start = nibbles.Length - digits;
        for (var i = nibbles.Length - 1; i >= start && intValue > 0; i--)
        {
            nibbles[i] = (int)(intValue % 10);
            intValue /= 10;
        }
        if (negative && start > 0) nibbles[start - 1] = 0xD;

        // Pack nibbles back into bytes.
        for (var i = 0; i < f.Length; i++)
            bytes[pos + i] = (byte)((nibbles[i * 2] << 4) | nibbles[i * 2 + 1]);
    }

    /// <summary>Structure/info panel for a Clarion DAT table.</summary>
    public static Task<TableStructure> GetStructureAsync(string folder, string tableName, string connectionName = "")
    {
        var path = ResolvePath(folder, tableName);
        var b = File.ReadAllBytes(path);

        int numFields = b.Length >= HeaderLength ? ReadU16(b, 13) : 0;
        long numRecs = b.Length >= HeaderLength ? ReadU32(b, 5) : 0;
        int recLen = b.Length >= HeaderLength ? ReadU16(b, 19) : 0;
        var fields = ReadFieldDescriptors(b, numFields);

        const int w = -18;
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{"Connection",w}{connectionName}");
        info.AppendLine($"{"File",w}{Path.GetFileName(path)}");
        info.AppendLine($"{"Folder",w}{folder}");
        info.AppendLine($"{"Fields",w}{numFields}");
        info.AppendLine($"{"Records",w}{numRecs:N0}");
        info.AppendLine($"{"Record length",w}{recLen} bytes");
        info.AppendLine();
        info.AppendLine("Fields:");
        foreach (var f in fields)
        {
            var detail = f.Type == FieldType.Decimal
                ? $"DECIMAL({f.Significance + f.Decimals},{f.Decimals})"
                : f.Type.ToString().ToUpperInvariant();
            info.AppendLine($"  • {f.Name}  ({detail})");
        }

        var ddl = "-- Classic Clarion DAT is a fixed binary ISAM format — it has no SQL DDL.\n" +
                  $"-- '{tableName}' supports cell edits, row adds and row deletes.\n" +
                  "-- Note: key/index files (.K??/.I??) are NOT updated — rebuild them in Clarion.";

        return Task.FromResult(new TableStructure(ddl, info.ToString().TrimEnd(),
            "Clarion DAT keys/indexes live in separate .K??/.I?? files and aren't read here."));
    }

    // ---- little-endian primitives ---------------------------------------
    private static ushort ReadU16(byte[] b, int p) => (ushort)(b[p] | (b[p + 1] << 8));
    private static int ReadI32(byte[] b, int p) => b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);
    private static long ReadU32(byte[] b, int p) => (uint)ReadI32(b, p);
}
