using System.Data;
using System.IO;
using System.Text;

namespace DataPortStudio.Services;

/// <summary>
/// Reads classic Clarion (.dat) files — the original Clarion ISAM format, NOT TopSpeed (.tps).
///
/// Clean-room implementation of the file layout documented in Clarion Technical Bulletin 117
/// (public): a fixed 85-byte header, one 27-byte descriptor per field, then fixed-length records
/// (each prefixed by a 5-byte record header) starting at the header's <c>offset</c>. Output is a
/// plain <see cref="DataTable"/> with the same shape as <see cref="TpsService"/>, so the tree,
/// viewer, Objects tab, Clarion date/time detection and TPS-style copy-to-SQL all reuse unchanged.
///
/// Dates and times are NOT a field type here — Clarion stores them as <c>LONG</c> values and formats
/// them with a picture, so they arrive as integers and the viewer's ClarionDetector renders them.
/// Read-only; keys/indexes (.K??/.I??) and memos (.MEM) are not read.
/// </summary>
public static class DatService
{
    private const string Extension = ".dat";
    private const ushort Signature = 0x3343;   // "C3" — Clarion Professional Developer v2+
    private const int HeaderLength = 85;
    private const int FieldDescriptorLength = 27;
    private const int RecordHeaderLength = 5;   // rhd (1) + rptr (4)

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

        if (rowLimit > 0 && recLen > RecordHeaderLength && dataOffset > 0)
        {
            var count = 0;
            for (long pos = dataOffset; pos + recLen <= bytes.Length && count < rowLimit; pos += recLen)
            {
                var status = bytes[pos];
                // Skip deleted (bit 4) and empty/free (no status bits set) slots.
                if (status == 0 || (status & 0x10) != 0) continue;

                var fieldBase = (int)(pos + RecordHeaderLength);
                var dr = table.NewRow();
                foreach (var f in fields)
                    dr[f.Name] = ReadValue(bytes, fieldBase + f.Offset, f, table.Columns[f.Name]!.DataType);
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
                  $"-- '{tableName}' is read-only; use Copy to write it to a SQL database.";

        return Task.FromResult(new TableStructure(ddl, info.ToString().TrimEnd(),
            "Clarion DAT keys/indexes live in separate .K??/.I?? files and aren't read here."));
    }

    // ---- little-endian primitives ---------------------------------------
    private static ushort ReadU16(byte[] b, int p) => (ushort)(b[p] | (b[p + 1] << 8));
    private static int ReadI32(byte[] b, int p) => b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);
    private static long ReadU32(byte[] b, int p) => (uint)ReadI32(b, p);
}
