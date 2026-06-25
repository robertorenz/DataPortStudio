using System.Data;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace DataPortStudio.Services;

/// <summary>Reads MongoDB databases, collections, and documents (read-only viewer).</summary>
public static class MongoService
{
    private static readonly JsonWriterSettings Json = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    private static MongoClient Client(string uri) => new(uri);

    public static async Task TestConnectionAsync(string uri)
    {
        var client = Client(uri);
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
    }

    public static async Task<List<string>> ListDatabasesAsync(string uri)
    {
        var cursor = await Client(uri).ListDatabaseNamesAsync();
        var names = await cursor.ToListAsync();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public static async Task<List<string>> ListCollectionsAsync(string uri, string database)
    {
        var cursor = await Client(uri).GetDatabase(database).ListCollectionNamesAsync();
        var names = await cursor.ToListAsync();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public static async Task<DataTable> LoadCollectionAsync(string uri, string database, string collection, int limit)
    {
        var coll = Client(uri).GetDatabase(database).GetCollection<BsonDocument>(collection);
        var docs = await coll.Find(new BsonDocument()).Limit(limit).ToListAsync();
        return ToDataTable(docs, collection);
    }

    /// <summary>Flattens documents into a string-typed table: top-level fields become columns,
    /// nested documents/arrays are shown as compact JSON. Pure (no I/O) for easy testing.</summary>
    public static DataTable ToDataTable(IReadOnlyList<BsonDocument> docs, string name)
    {
        var table = new DataTable(name);

        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in docs)
            foreach (var el in d.Elements)
                if (seen.Add(el.Name)) columns.Add(el.Name);

        if (columns.Remove("_id")) columns.Insert(0, "_id"); // _id first

        foreach (var c in columns) table.Columns.Add(c, typeof(string));

        foreach (var d in docs)
        {
            var row = table.NewRow();
            foreach (var c in columns)
                row[c] = d.TryGetValue(c, out var v) ? Render(v) : DBNull.Value;
            table.Rows.Add(row);
        }
        return table;
    }

    private static object Render(BsonValue v) => v.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => DBNull.Value,
        BsonType.Document => v.AsBsonDocument.ToJson(Json),
        BsonType.Array => v.AsBsonArray.ToJson(Json),
        BsonType.ObjectId => v.AsObjectId.ToString(),
        BsonType.String => v.AsString,
        BsonType.Boolean => v.AsBoolean ? "true" : "false",
        BsonType.DateTime => v.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        BsonType.Int32 => v.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonType.Int64 => v.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonType.Double => v.AsDouble.ToString(CultureInfo.InvariantCulture),
        BsonType.Decimal128 => v.AsDecimal.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    public static async Task<TableStructure> GetStructureAsync(string uri, string database, string collection, string connectionName = "")
    {
        var loc = LocalizationManager.Instance;
        var coll = Client(uri).GetDatabase(database).GetCollection<BsonDocument>(collection);
        var count = await coll.CountDocumentsAsync(new BsonDocument());
        var sample = await coll.Find(new BsonDocument()).Limit(50).ToListAsync();

        // Field name → first-seen BSON type, in document order.
        var fields = new List<(string Name, string Type)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in sample)
            foreach (var el in d.Elements)
                if (seen.Add(el.Name)) fields.Add((el.Name, el.Value.BsonType.ToString()));

        const int w = -18;
        var info = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{loc["Info_Connection"],w}{connectionName}");
        info.AppendLine($"{loc["Info_Database"],w}{database}");
        info.AppendLine($"{loc["Info_Collection"],w}{collection}");
        info.AppendLine($"{loc["Info_Documents"],w}{count:N0}");
        info.AppendLine($"{loc["Info_SampledFields"],w}{fields.Count}  (from {sample.Count} doc(s))");
        info.AppendLine();
        info.AppendLine(loc["Info_Fields"]);
        foreach (var (fn, ft) in fields)
            info.AppendLine($"  • {fn}  ({ft})");

        var ddl = "// MongoDB is schemaless — collections have no fixed DDL.\n" +
                  $"// '{collection}' currently holds {count:N0} document(s).";

        return new TableStructure(ddl, info.ToString().TrimEnd(), loc["Mongo_NoIndexes"]);
    }
}
