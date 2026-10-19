using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FirebirdSql.Data.FirebirdClient;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>One step of a query plan.</summary>
public sealed class PlanNode
{
    public string Operation { get; set; } = "";
    /// <summary>The table / index the step works on, when there is one.</summary>
    public string? Object { get; set; }
    /// <summary>The step's predicate or condition (seek keys, filter, join condition…).</summary>
    public string? Detail { get; set; }
    public double? EstRows { get; set; }
    /// <summary>Estimated cost of this step and everything under it.</summary>
    public double? Cost { get; set; }
    /// <summary>Estimated cost of this step alone.</summary>
    public double? SelfCost { get; set; }
    /// <summary>This step's share of the statement — of cost when the engine reports one, else of rows.</summary>
    public double? Percent { get; set; }
    /// <summary>A step worth looking at: a large share of the statement's cost.</summary>
    public bool IsHot { get; set; }
    public List<PlanNode> Children { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<KeyValuePair<string, string>> Properties { get; } = [];

    public IEnumerable<PlanNode> DescendantsAndSelf() => Children.SelectMany(c => c.DescendantsAndSelf()).Prepend(this);
}

/// <summary>The plan of one statement.</summary>
public sealed class PlanStatement
{
    public string Sql { get; init; } = "";
    public PlanNode? Root { get; set; }
    public string? Error { get; set; }
    public double? TotalCost { get; set; }
    /// <summary>Ready-to-run suggestions (SQL Server's missing-index hints).</summary>
    public List<string> Suggestions { get; } = [];
}

public sealed class ExecutionPlan
{
    public DatabaseEngine Engine { get; init; }
    public List<PlanStatement> Statements { get; } = [];
    /// <summary>True when the engine reports costs; otherwise shares are of estimated rows.</summary>
    public bool HasCosts { get; set; }
    /// <summary>The plan exactly as the engine returned it.</summary>
    public string Raw { get; set; } = "";
    /// <summary>File extension for <see cref="Raw"/> (".sqlplan" opens in SSMS).</summary>
    public string RawExtension { get; set; } = ".txt";

    public int WarningCount => Statements.Sum(s =>
        (s.Root?.DescendantsAndSelf().Sum(n => n.Warnings.Count) ?? 0) + s.Suggestions.Count + (s.Error is null ? 0 : 1));
}

/// <summary>
/// Asks the engine how it would run a query — without running it — and turns the answer into a
/// common tree of <see cref="PlanNode"/>s with costs, shares and warnings about slow patterns
/// (full scans, per-row lookups, sorts spilled to disk, missing indexes…).
/// </summary>
public static class ExecutionPlanService
{
    /// <summary>A step with at least this share of its statement is highlighted.</summary>
    public const double HotPercent = 25;

    /// <summary>Scans of tables at least this big are worth a warning.</summary>
    private const double BigTable = 1000;

    private static string L(string key) => LocalizationManager.Instance[key];

    public static bool IsSupported(DatabaseEngine e) => TableCopyService.IsRelational(e);

    public static async Task<ExecutionPlan> ExplainAsync(DbConnection conn, DatabaseEngine engine, string sql,
        CancellationToken ct = default)
    {
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        var plan = new ExecutionPlan { Engine = engine };
        switch (engine)
        {
            case DatabaseEngine.SqlServer: await SqlServerAsync(conn, sql, plan, ct); break;
            case DatabaseEngine.PostgreSql: await PerStatementAsync(sql, plan, s => PostgresAsync(conn, s, plan, ct)); break;
            case DatabaseEngine.MySql or DatabaseEngine.MariaDb: await PerStatementAsync(sql, plan, s => MySqlAsync(conn, s, plan, ct)); break;
            case DatabaseEngine.Sqlite: await PerStatementAsync(sql, plan, s => SqliteAsync(conn, s, plan, ct)); break;
            case DatabaseEngine.Oracle: await PerStatementAsync(sql, plan, s => OracleAsync(conn, s, plan, ct)); break;
            case DatabaseEngine.Firebird: await PerStatementAsync(sql, plan, s => FirebirdAsync(conn, s, plan, ct)); break;
            default: throw new NotSupportedException($"Execution plans aren't available for {engine.DisplayName()}.");
        }
        foreach (var s in plan.Statements) Finish(s, plan.HasCosts);
        return plan;
    }

    private static async Task PerStatementAsync(string sql, ExecutionPlan plan, Func<string, Task<PlanStatement>> explain)
    {
        foreach (var stmt in SqlSafetyAnalyzer.SplitStatements(sql))
        {
            PlanStatement result;
            try { result = await explain(stmt); }
            catch (Exception ex) { result = new PlanStatement { Sql = stmt, Error = ex.Message }; }
            plan.Statements.Add(result);
        }
    }

    /// <summary>Fills self-costs, shares and the hot flag once the tree is built.</summary>
    private static void Finish(PlanStatement s, bool hasCosts)
    {
        if (s.Root is null) return;
        foreach (var n in s.Root.DescendantsAndSelf())
            if (n.SelfCost is null && n.Cost is { } c)
                n.SelfCost = Math.Max(0, c - n.Children.Sum(ch => ch.Cost ?? 0));

        double total;
        Func<PlanNode, double?> weight;
        if (hasCosts)
        {
            total = s.TotalCost ??= s.Root.Cost ?? s.Root.DescendantsAndSelf().Sum(n => n.SelfCost ?? 0);
            weight = n => n.SelfCost;
        }
        else
        {
            total = s.Root.DescendantsAndSelf().Sum(n => n.EstRows ?? 0);
            weight = n => n.EstRows;
        }
        if (total <= 0) return;
        foreach (var n in s.Root.DescendantsAndSelf())
        {
            if (weight(n) is not { } w) continue;
            n.Percent = Math.Round(w / total * 100, 1);
            n.IsHot = n.Percent >= HotPercent;
        }
    }

    private static string Num(double v) => v.ToString(v >= 100 ? "N0" : "0.##", CultureInfo.CurrentCulture);
    private static double? D(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // ── SQL Server ──────────────────────────────────────────────────────────

    private static readonly XNamespace Sp = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static async Task SqlServerAsync(DbConnection conn, string sql, ExecutionPlan plan, CancellationToken ct)
    {
        plan.HasCosts = true;
        plan.RawExtension = ".sqlplan";
        var xmls = new List<string>();
        await ExecAsync(conn, "SET SHOWPLAN_XML ON", ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            do
            {
                while (await r.ReadAsync(ct))
                    if (!r.IsDBNull(0)) xmls.Add(r.GetString(0));
            } while (await r.NextResultAsync(ct));
        }
        finally
        {
            await ExecAsync(conn, "SET SHOWPLAN_XML OFF", CancellationToken.None);
        }

        plan.Raw = xmls.Count == 1 ? xmls[0] : string.Join(Environment.NewLine, xmls);
        foreach (var xml in xmls)
        {
            var doc = XDocument.Parse(xml);
            foreach (var stmt in doc.Descendants(Sp + "StmtSimple"))
            {
                var qp = stmt.Element(Sp + "QueryPlan");
                if (qp?.Element(Sp + "RelOp") is not { } rootOp) continue; // SET, DECLARE… have no plan
                var ps = new PlanStatement
                {
                    Sql = ((string?)stmt.Attribute("StatementText") ?? "").TrimStart().TrimStart(';').Trim(),
                    TotalCost = D((string?)stmt.Attribute("StatementSubTreeCost")),
                    Root = SqlServerNode(rootOp),
                };
                ps.Suggestions.AddRange(MissingIndexes(qp));
                if (qp.Element(Sp + "Warnings") is { } w) ps.Root.Warnings.AddRange(SqlServerWarnings(w));
                plan.Statements.Add(ps);
            }
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Elements under <paramref name="op"/> that belong to it, not to a nested operator.</summary>
    private static IEnumerable<XElement> Own(XElement op, string name) =>
        op.Descendants(Sp + name).Where(d => d.Ancestors(Sp + "RelOp").First() == op);

    private static PlanNode SqlServerNode(XElement op)
    {
        string? A(string name) => (string?)op.Attribute(name);
        var physical = A("PhysicalOp") ?? "?";
        var logical = A("LogicalOp");
        var node = new PlanNode
        {
            Operation = logical is null || logical == physical ? physical : $"{physical} ({logical})",
            EstRows = D(A("EstimateRows")),
            Cost = D(A("EstimatedTotalSubtreeCost")),
        };

        if (Own(op, "Object").FirstOrDefault() is { } obj)
        {
            string? O(string n) => (string?)obj.Attribute(n);
            var table = string.Join(".", new[] { O("Schema"), O("Table") }.Where(x => !string.IsNullOrEmpty(x)));
            node.Object = O("Index") is { } idx ? $"{table} · {idx}" : table;
            if (string.IsNullOrEmpty(node.Object)) node.Object = null;
        }

        var details = new List<string>();
        foreach (var seek in Own(op, "SeekKeys"))
            foreach (var range in seek.Elements())
            {
                var cols = range.Element(Sp + "RangeColumns")?.Elements(Sp + "ColumnReference")
                    .Select(c => (string?)c.Attribute("Column")).ToList() ?? [];
                var exprs = range.Element(Sp + "RangeExpressions")?.Elements(Sp + "ScalarOperator")
                    .Select(e => (string?)e.Attribute("ScalarString")).ToList() ?? [];
                var op2 = (string?)range.Attribute("ScanType") switch
                {
                    "EQ" => "=", "GT" => ">", "GE" => ">=", "LT" => "<", "LE" => "<=", var x => x ?? "="
                };
                for (var i = 0; i < Math.Min(cols.Count, exprs.Count); i++)
                    details.Add($"{L("PL_Seek")}: {cols[i]} {op2} {exprs[i]}");
            }
        foreach (var pred in Own(op, "Predicate"))
            if (pred.Element(Sp + "ScalarOperator")?.Attribute("ScalarString")?.Value is { } where)
                details.Add($"{L("PL_Where")}: {where}");
        if (details.Count > 0) node.Detail = string.Join("\n", details);

        foreach (var name in new[] { "EstimateRows", "EstimateIO", "EstimateCPU", "EstimatedTotalSubtreeCost",
                                     "TableCardinality", "EstimatedRowsRead", "AvgRowSize", "Parallel", "EstimatedExecutionMode" })
            if (A(name) is { } v) node.Properties.Add(new(name, v));

        // Slow patterns
        var tableRows = D(A("TableCardinality")) ?? D(A("EstimatedRowsRead")) ?? node.EstRows ?? 0;
        if (physical is "Table Scan" or "Clustered Index Scan" or "Index Scan" && tableRows >= BigTable)
            node.Warnings.Add(string.Format(L("PL_WarnScan"), Num(tableRows)));
        if (physical is "Key Lookup" or "RID Lookup")
            node.Warnings.Add(L("PL_WarnLookup"));
        if (op.Element(Sp + "Warnings") is { } w) node.Warnings.AddRange(SqlServerWarnings(w));

        foreach (var child in op.Descendants(Sp + "RelOp").Where(d => d.Ancestors(Sp + "RelOp").First() == op))
            node.Children.Add(SqlServerNode(child));
        return node;
    }

    private static IEnumerable<string> SqlServerWarnings(XElement warnings)
    {
        foreach (var w in warnings.Elements())
            yield return w.Name.LocalName switch
            {
                "NoJoinPredicate" => L("PL_WarnNoJoinPredicate"),
                "ColumnsWithNoStatistics" => L("PL_WarnNoStats"),
                "PlanAffectingConvert" => string.Format(L("PL_WarnConvert"), (string?)w.Attribute("Expression")),
                "SpillToTempDb" => L("PL_WarnSpill"),
                "UnmatchedIndexes" => L("PL_WarnUnmatchedIndex"),
                var other => other,
            };
        if ((string?)warnings.Attribute("NoJoinPredicate") == "true" || (string?)warnings.Attribute("NoJoinPredicate") == "1")
            yield return L("PL_WarnNoJoinPredicate");
    }

    /// <summary>SQL Server's missing-index hints, as CREATE INDEX statements.</summary>
    private static IEnumerable<string> MissingIndexes(XElement queryPlan)
    {
        foreach (var group in queryPlan.Descendants(Sp + "MissingIndexGroup"))
        {
            var impact = D((string?)group.Attribute("Impact")) ?? 0;
            foreach (var mi in group.Elements(Sp + "MissingIndex"))
            {
                List<string> Cols(params string[] usages) => mi.Elements(Sp + "ColumnGroup")
                    .Where(g => usages.Contains((string?)g.Attribute("Usage")))
                    .SelectMany(g => g.Elements(Sp + "Column").Select(c => (string?)c.Attribute("Name") ?? ""))
                    .ToList();
                var keys = Cols("EQUALITY", "INEQUALITY");
                var include = Cols("INCLUDE");
                if (keys.Count == 0) continue;
                var schema = (string?)mi.Attribute("Schema") ?? "[dbo]";
                var table = (string?)mi.Attribute("Table") ?? "";
                var name = "[IX_" + table.Trim('[', ']') + "_" + string.Join("_", keys.Select(k => k.Trim('[', ']'))) + "]";
                yield return $"CREATE INDEX {name} ON {schema}.{table} ({string.Join(", ", keys)})" +
                             (include.Count > 0 ? $" INCLUDE ({string.Join(", ", include)})" : "") +
                             $";  -- {string.Format(L("PL_Impact"), impact.ToString("0", CultureInfo.CurrentCulture))}";
            }
        }
    }

    // ── PostgreSQL ──────────────────────────────────────────────────────────

    private static async Task<PlanStatement> PostgresAsync(DbConnection conn, string sql, ExecutionPlan plan, CancellationToken ct)
    {
        plan.HasCosts = true;
        plan.RawExtension = ".json";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN (FORMAT JSON) " + sql;
        var json = Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? "[]";
        plan.Raw += (plan.Raw.Length > 0 ? Environment.NewLine : "") + json;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement[0].GetProperty("Plan");
        return new PlanStatement { Sql = sql, Root = PostgresNode(root) };
    }

    private static readonly string[] PgConditions =
        ["Index Cond", "Recheck Cond", "Hash Cond", "Merge Cond", "Join Filter", "Filter", "Sort Key", "Group Key"];

    private static PlanNode PostgresNode(JsonElement e)
    {
        string? S(string name) => e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ToString())),
            _ => v.ToString(),
        } : null;
        double? N(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var type = S("Node Type") ?? "?";
        var join = S("Join Type");
        var node = new PlanNode
        {
            Operation = join is not null && (type.Contains("Join") || type == "Nested Loop") ? $"{type} ({join})" : type,
            EstRows = N("Plan Rows"),
            Cost = N("Total Cost"),
        };
        var relation = S("Relation Name");
        if (relation is not null)
            node.Object = (S("Schema") is { } schema ? schema + "." : "") + relation;
        if (S("Index Name") is { } idx)
            node.Object = node.Object is null ? idx : $"{node.Object} · {idx}";

        var details = PgConditions.Select(c => S(c) is { } v ? $"{c}: {v}" : null).Where(x => x is not null).ToList();
        if (details.Count > 0) node.Detail = string.Join("\n", details);

        foreach (var p in e.EnumerateObject())
            if (p.Name != "Plans" && p.Value.ValueKind != JsonValueKind.Array && p.Value.ValueKind != JsonValueKind.Object)
                node.Properties.Add(new(p.Name, p.Value.ToString()));

        if (type == "Seq Scan" && (S("Filter") is not null || (node.EstRows ?? 0) >= BigTable))
            node.Warnings.Add(S("Filter") is not null ? L("PL_WarnSeqScanFilter") : string.Format(L("PL_WarnScan"), Num(node.EstRows ?? 0)));
        if (type == "Nested Loop" && S("Join Filter") is not null && (node.EstRows ?? 0) >= BigTable)
            node.Warnings.Add(L("PL_WarnNestedLoop"));

        if (e.TryGetProperty("Plans", out var plans))
            foreach (var child in plans.EnumerateArray()) node.Children.Add(PostgresNode(child));
        return node;
    }

    // ── MySQL / MariaDB ─────────────────────────────────────────────────────

    private static async Task<PlanStatement> MySqlAsync(DbConnection conn, string sql, ExecutionPlan plan, CancellationToken ct)
    {
        plan.RawExtension = ".txt";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN " + sql;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var root = new PlanNode { Operation = L("PL_QueryPlan") };
        var raw = new StringBuilder(string.Join("\t", names)).AppendLine();
        while (await r.ReadAsync(ct))
        {
            var row = names.Select((n, i) => (n, v: r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)))
                .ToDictionary(x => x.n, x => x.v, StringComparer.OrdinalIgnoreCase);
            raw.AppendLine(string.Join("\t", names.Select(n => row[n] ?? "NULL")));
            string? V(string k) => row.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : null;

            var access = V("type");
            var node = new PlanNode
            {
                Operation = access switch
                {
                    "ALL" => L("PL_MyFullScan"),
                    "index" => L("PL_MyIndexScan"),
                    "range" => L("PL_MyRange"),
                    "ref" or "eq_ref" or "ref_or_null" => L("PL_MyLookup"),
                    "const" or "system" => L("PL_MyConst"),
                    null => V("select_type") ?? "?",
                    _ => access,
                } + (access is null ? "" : $"  [{access}]"),
                Object = V("table") is { } t ? (V("key") is { } k ? $"{t} · {k}" : t) : null,
                Detail = V("Extra"),
                EstRows = D(V("rows")),
            };
            foreach (var n in names) if (V(n) is { } v) node.Properties.Add(new(n, v));

            if (access == "ALL" && (node.EstRows ?? 0) >= BigTable)
                node.Warnings.Add(V("possible_keys") is null
                    ? string.Format(L("PL_WarnScanNoIndex"), Num(node.EstRows ?? 0))
                    : string.Format(L("PL_WarnScan"), Num(node.EstRows ?? 0)));
            var extra = V("Extra") ?? "";
            if (extra.Contains("Using filesort")) node.Warnings.Add(L("PL_WarnFilesort"));
            if (extra.Contains("Using temporary")) node.Warnings.Add(L("PL_WarnTemporary"));
            if (extra.Contains("Using join buffer")) node.Warnings.Add(L("PL_WarnJoinBuffer"));
            root.Children.Add(node);
        }
        plan.Raw += raw.AppendLine().ToString();
        return new PlanStatement { Sql = sql, Root = root };
    }

    // ── SQLite ──────────────────────────────────────────────────────────────

    private static readonly Regex SqliteTable = new(@"^(?:SCAN|SEARCH)\s+(?:TABLE\s+)?(\S+)", RegexOptions.IgnoreCase);

    private static async Task<PlanStatement> SqliteAsync(DbConnection conn, string sql, ExecutionPlan plan, CancellationToken ct)
    {
        plan.RawExtension = ".txt";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var root = new PlanNode { Operation = L("PL_QueryPlan") };
        var byId = new Dictionary<long, (PlanNode Node, int Depth)> { [0] = (root, -1) };
        var raw = new StringBuilder();
        while (await r.ReadAsync(ct))
        {
            long id = r.GetInt64(0), parent = r.GetInt64(1);
            var detail = r.GetString(3);
            var (parentNode, parentDepth) = byId.TryGetValue(parent, out var p) ? p : (root, -1);
            raw.AppendLine(new string(' ', 2 * (parentDepth + 1)) + detail);
            var node = new PlanNode { Operation = detail };
            if (SqliteTable.Match(detail) is { Success: true } m) node.Object = m.Groups[1].Value;
            var upper = detail.ToUpperInvariant();
            if (upper.StartsWith("SCAN ") && !upper.Contains(" USING "))
                node.Warnings.Add(string.Format(L("PL_WarnSqliteScan"), node.Object));
            if (upper.StartsWith("USE TEMP B-TREE"))
                node.Warnings.Add(L("PL_WarnTempSort"));
            byId[id] = (node, parentDepth + 1);
            parentNode.Children.Add(node);
        }
        plan.Raw += raw.AppendLine().ToString();
        return new PlanStatement { Sql = sql, Root = root };
    }

    // ── Oracle ──────────────────────────────────────────────────────────────

    private static async Task<PlanStatement> OracleAsync(DbConnection conn, string sql, ExecutionPlan plan, CancellationToken ct)
    {
        plan.HasCosts = true;
        plan.RawExtension = ".txt";
        var id = "DPS" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        await ExecAsync(conn, $"EXPLAIN PLAN SET STATEMENT_ID = '{id}' FOR {sql.TrimEnd(';', ' ', '\r', '\n')}", ct);
        try
        {
            var nodes = new Dictionary<int, PlanNode>();
            PlanNode? root = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, parent_id, operation, options, object_owner, object_name, cardinality, cost, " +
                                  "access_predicates, filter_predicates FROM plan_table " +
                                  $"WHERE statement_id = '{id}' ORDER BY id";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                    double? N(int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
                    var operation = S(2) ?? "?";
                    var options = S(3);
                    var node = new PlanNode
                    {
                        Operation = options is null ? operation : $"{operation} {options}",
                        Object = S(5) is { } name ? (S(4) is { } owner ? $"{owner}.{name}" : name) : null,
                        EstRows = N(6),
                        Cost = N(7),
                    };
                    var details = new[] { (L("PL_Access"), S(8)), (L("PL_Filter"), S(9)) }
                        .Where(x => x.Item2 is not null).Select(x => $"{x.Item1}: {x.Item2}").ToList();
                    if (details.Count > 0) node.Detail = string.Join("\n", details);
                    if (node.Operation == "TABLE ACCESS FULL" && (node.EstRows ?? 0) >= BigTable)
                        node.Warnings.Add(string.Format(L("PL_WarnScan"), Num(node.EstRows ?? 0)));
                    if (node.Operation.Contains("CARTESIAN")) node.Warnings.Add(L("PL_WarnNoJoinPredicate"));

                    var nodeId = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
                    nodes[nodeId] = node;
                    if (r.IsDBNull(1)) root ??= node;
                    else if (nodes.TryGetValue(Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture), out var parent))
                        parent.Children.Add(node);
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT plan_table_output FROM TABLE(DBMS_XPLAN.DISPLAY('PLAN_TABLE', '{id}', 'TYPICAL'))";
                var raw = new StringBuilder();
                try
                {
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) raw.AppendLine(r.IsDBNull(0) ? "" : r.GetString(0));
                }
                catch { /* DBMS_XPLAN not granted — the tree is still there */ }
                plan.Raw += raw.AppendLine().ToString();
            }
            return new PlanStatement { Sql = sql, Root = root };
        }
        finally
        {
            try { await ExecAsync(conn, $"DELETE FROM plan_table WHERE statement_id = '{id}'", CancellationToken.None); }
            catch { /* plan_table is a session temp table on modern Oracle anyway */ }
        }
    }

    // ── Firebird ────────────────────────────────────────────────────────────

    private static readonly Regex FbTable = new(@"Table ""([^""]+)""(?: as ""?([^""\s]+)""?)?", RegexOptions.IgnoreCase);

    private static async Task<PlanStatement> FirebirdAsync(DbConnection conn, string sql, ExecutionPlan plan, CancellationToken ct)
    {
        plan.RawExtension = ".txt";
        await using var cmd = (FbCommand)conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.PrepareAsync(ct);
        string text;
        try { text = await cmd.GetCommandExplainedPlanAsync(ct); }
        catch { text = await cmd.GetCommandPlanAsync(ct); } // Firebird 2.5: the legacy one-line plan
        text = (text ?? "").Replace("\r", "");
        plan.Raw += text + Environment.NewLine;

        var root = new PlanNode { Operation = L("PL_QueryPlan") };
        var stack = new List<(int Indent, PlanNode Node)> { (-1, root) };
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            var label = line.Trim();
            if (label.StartsWith("->")) label = label[2..].Trim();
            var node = new PlanNode { Operation = label };
            if (FbTable.Match(label) is { Success: true } m) node.Object = m.Groups[1].Value;
            if (label.Contains("Full Scan", StringComparison.OrdinalIgnoreCase) || label.Contains(" NATURAL", StringComparison.OrdinalIgnoreCase))
                node.Warnings.Add(string.Format(L("PL_WarnSqliteScan"), node.Object ?? ""));
            while (stack.Count > 1 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            stack[^1].Node.Children.Add(node);
            stack.Add((indent, node));
        }
        return new PlanStatement { Sql = sql, Root = root };
    }
}
