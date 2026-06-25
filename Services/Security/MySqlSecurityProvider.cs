using MySqlConnector;
using DataPortStudio.Models;

namespace DataPortStudio.Services.Security;

/// <summary>User / role / privilege management for MySQL and MariaDB (mysql.* catalog + GRANT/REVOKE).</summary>
public sealed class MySqlSecurityProvider : SecurityProvider
{
    public MySqlSecurityProvider(ConnectionProfile p) : base(p) { }

    private string ServerCs => MySqlService.WithoutDatabase(Cs);
    private bool? _maria;

    // Privilege keyword -> mysql.user / mysql.db column name.
    private static readonly (string Kw, string Col)[] PrivMap =
    {
        ("SELECT", "Select_priv"), ("INSERT", "Insert_priv"), ("UPDATE", "Update_priv"),
        ("DELETE", "Delete_priv"), ("CREATE", "Create_priv"), ("DROP", "Drop_priv"),
        ("ALTER", "Alter_priv"), ("INDEX", "Index_priv"), ("REFERENCES", "References_priv"),
        ("CREATE VIEW", "Create_view_priv"), ("SHOW VIEW", "Show_view_priv"),
        ("CREATE ROUTINE", "Create_routine_priv"), ("ALTER ROUTINE", "Alter_routine_priv"),
        ("EXECUTE", "Execute_priv"), ("TRIGGER", "Trigger_priv"), ("EVENT", "Event_priv"),
        ("CREATE TEMPORARY TABLES", "Create_tmp_table_priv"), ("LOCK TABLES", "Lock_tables_priv"),
        // global-only below
        ("CREATE USER", "Create_user_priv"), ("RELOAD", "Reload_priv"), ("PROCESS", "Process_priv"),
        ("SHOW DATABASES", "Show_db_priv"), ("SUPER", "Super_priv"), ("FILE", "File_priv"),
        ("REPLICATION CLIENT", "Repl_client_priv"), ("REPLICATION SLAVE", "Repl_slave_priv"),
    };

    private static readonly string[] DbPrivs =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "INDEX", "REFERENCES",
        "CREATE VIEW", "SHOW VIEW", "CREATE ROUTINE", "ALTER ROUTINE", "EXECUTE", "TRIGGER", "EVENT",
        "CREATE TEMPORARY TABLES", "LOCK TABLES",
    };

    public override SecurityCapabilities Capabilities => new()
    {
        HasHost = true,
        SupportsLock = true,
        SupportsRoles = true,
        SupportsDatabaseScope = true,
        GlobalScopeLabel = "Global (*.*)",
        UserNoun = "User",
        Note = "MySQL/MariaDB accounts are user@host. Privileges apply globally (*.*) or per database.",
        GlobalPrivileges = PrivMap.Select(p => p.Kw).ToArray(),
        DatabasePrivileges = DbPrivs,
    };

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "''");
    private static string Acct(SecurityPrincipal p) =>
        $"'{Esc(p.Name)}'@'{Esc(p.Host ?? "%")}'";

    private async Task<bool> IsMariaAsync()
    {
        if (_maria is { } v) return v;
        await using var conn = new MySqlConnection(ServerCs);
        await conn.OpenAsync();
        _maria = conn.ServerVersion?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ?? false;
        return _maria.Value;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new MySqlConnection(ServerCs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public override async Task<List<SecurityPrincipal>> GetPrincipalsAsync()
    {
        var maria = await IsMariaAsync();
        var roleNames = await GetRoleNameSetAsync(maria);

        var list = new List<SecurityPrincipal>();
        await using var conn = new MySqlConnection(ServerCs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // account_locked exists on MySQL 5.7.6+/MariaDB 10.4+; fall back if missing.
        var hasLocked = await ColumnExistsAsync(conn, "user", "account_locked");
        cmd.CommandText = hasLocked
            ? "SELECT User, Host, account_locked FROM mysql.user ORDER BY User, Host"
            : "SELECT User, Host FROM mysql.user ORDER BY User, Host";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            var host = r.GetString(1);
            var locked = hasLocked && !r.IsDBNull(2) &&
                         r.GetString(2).Equals("Y", StringComparison.OrdinalIgnoreCase);
            var isRole = roleNames.Contains(maria ? name : $"{name}@{host}") || roleNames.Contains(name);
            list.Add(new SecurityPrincipal
            {
                Name = name,
                Host = host,
                IsRole = isRole,
                CanLogin = !isRole,
                Locked = locked,
                IsBuiltIn = name is "root" or "mysql.sys" or "mysql.session" or "mysql.infoschema",
            });
        }
        return list;
    }

    private async Task<HashSet<string>> GetRoleNameSetAsync(bool maria)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new MySqlConnection(ServerCs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // MariaDB: explicit is_role flag. MySQL 8: anything that has been granted as a role.
            cmd.CommandText = maria
                ? "SELECT User FROM mysql.user WHERE is_role='Y'"
                : "SELECT DISTINCT FROM_USER FROM mysql.role_edges";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) set.Add(r.GetString(0));
        }
        catch { /* roles not supported on this version — leave empty */ }
        return set;
    }

    private static async Task<bool> ColumnExistsAsync(MySqlConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                          "WHERE TABLE_SCHEMA='mysql' AND TABLE_NAME=@t AND COLUMN_NAME=@c";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    public override Task<List<string>> GetDatabasesAsync() => MySqlService.GetDatabasesAsync(ServerCs);

    public override async Task<HashSet<string>> GetMembershipsAsync(SecurityPrincipal p)
    {
        var maria = await IsMariaAsync();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new MySqlConnection(ServerCs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            if (maria)
            {
                cmd.CommandText = "SELECT Role FROM mysql.roles_mapping WHERE User=@u AND Host=@h";
            }
            else
            {
                cmd.CommandText = "SELECT FROM_USER FROM mysql.role_edges WHERE TO_USER=@u AND TO_HOST=@h";
            }
            cmd.Parameters.AddWithValue("@u", p.Name);
            cmd.Parameters.AddWithValue("@h", p.Host ?? "%");
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) set.Add(r.GetString(0));
        }
        catch { /* roles unsupported */ }
        return set;
    }

    public override Task GrantRoleAsync(string role, SecurityPrincipal p) =>
        ExecAsync($"GRANT '{Esc(role)}' TO {Acct(p)}");

    public override Task RevokeRoleAsync(string role, SecurityPrincipal p) =>
        ExecAsync($"REVOKE '{Esc(role)}' FROM {Acct(p)}");

    public override Task CreateUserAsync(string name, string? host, string? password)
    {
        var acct = $"'{Esc(name)}'@'{Esc(string.IsNullOrWhiteSpace(host) ? "%" : host)}'";
        var sql = string.IsNullOrEmpty(password)
            ? $"CREATE USER {acct}"
            : $"CREATE USER {acct} IDENTIFIED BY '{Esc(password)}'";
        return ExecAsync(sql);
    }

    public override Task CreateRoleAsync(string name) => ExecAsync($"CREATE ROLE '{Esc(name)}'");

    public override Task DropAsync(SecurityPrincipal p) =>
        ExecAsync(p.IsRole ? $"DROP ROLE '{Esc(p.Name)}'" : $"DROP USER {Acct(p)}");

    public override Task SetPasswordAsync(SecurityPrincipal p, string password) =>
        ExecAsync($"ALTER USER {Acct(p)} IDENTIFIED BY '{Esc(password)}'");

    public override Task SetLockedAsync(SecurityPrincipal p, bool locked) =>
        ExecAsync($"ALTER USER {Acct(p)} ACCOUNT {(locked ? "LOCK" : "UNLOCK")}");

    public override async Task<List<PrivilegeItem>> GetPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database)
    {
        var keywords = scope == PrivilegeScopeKind.Global ? Capabilities.GlobalPrivileges : DbPrivs;
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var conn = new MySqlConnection(ServerCs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        if (scope == PrivilegeScopeKind.Global)
        {
            cmd.CommandText = "SELECT * FROM mysql.user WHERE User=@u AND Host=@h";
            cmd.Parameters.AddWithValue("@u", p.Name);
            cmd.Parameters.AddWithValue("@h", p.Host ?? "%");
        }
        else
        {
            cmd.CommandText = "SELECT * FROM mysql.db WHERE User=@u AND Host=@h AND Db=@d";
            cmd.Parameters.AddWithValue("@u", p.Name);
            cmd.Parameters.AddWithValue("@h", p.Host ?? "%");
            cmd.Parameters.AddWithValue("@d", database ?? "");
        }
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            // Map present columns -> value, then resolve each keyword via PrivMap.
            var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < r.FieldCount; i++)
                cols[r.GetName(i)] = r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString() ?? "";
            foreach (var (kw, col) in PrivMap)
                if (cols.TryGetValue(col, out var v) && v.Equals("Y", StringComparison.OrdinalIgnoreCase))
                    granted.Add(kw);
        }
        return BuildItems(keywords, granted);
    }

    public override async Task ApplyPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database, IReadOnlyList<PrivilegeItem> items)
    {
        var on = scope == PrivilegeScopeKind.Global ? "*.*" : $"{MySqlService.Quote(database ?? "")}.*";
        var toGrant = items.Where(i => i.IsDirty && i.Granted).Select(i => i.Keyword).ToList();
        var toRevoke = items.Where(i => i.IsDirty && !i.Granted).Select(i => i.Keyword).ToList();

        if (toGrant.Count > 0)
            await ExecAsync($"GRANT {string.Join(", ", toGrant)} ON {on} TO {Acct(p)}");
        if (toRevoke.Count > 0)
            await ExecAsync($"REVOKE {string.Join(", ", toRevoke)} ON {on} FROM {Acct(p)}");
    }
}
