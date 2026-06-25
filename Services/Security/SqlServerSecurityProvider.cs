using Microsoft.Data.SqlClient;
using DataPortStudio.Models;

namespace DataPortStudio.Services.Security;

/// <summary>
/// Server-level login / role management for SQL Server, with server-scope permissions and
/// per-database permissions (granted to the database user mapped to the login).
/// </summary>
public sealed class SqlServerSecurityProvider : SecurityProvider
{
    public SqlServerSecurityProvider(ConnectionProfile p) : base(p) { }

    private static readonly string[] ServerPrivs =
    {
        "CONNECT SQL", "VIEW SERVER STATE", "VIEW ANY DATABASE", "VIEW ANY DEFINITION",
        "ALTER ANY LOGIN", "ALTER ANY DATABASE", "ALTER ANY SERVER ROLE", "ALTER ANY CONNECTION",
        "ALTER SERVER STATE", "CREATE ANY DATABASE", "SHUTDOWN", "CONTROL SERVER",
    };

    private static readonly string[] DbPrivs =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE", "REFERENCES", "ALTER", "CONTROL",
        "VIEW DEFINITION", "CREATE TABLE", "CREATE VIEW", "CREATE PROCEDURE", "CREATE FUNCTION",
        "BACKUP DATABASE",
    };

    public override SecurityCapabilities Capabilities => new()
    {
        HasHost = false,
        SupportsLock = true,           // ALTER LOGIN DISABLE/ENABLE
        SupportsRoles = true,
        SupportsDatabaseScope = true,
        GlobalScopeLabel = "Server",
        UserNoun = "Login",
        Note = "Server logins and roles. Database-scoped privileges are granted to the database " +
               "user mapped to the login (created automatically if missing).",
        GlobalPrivileges = ServerPrivs,
        DatabasePrivileges = DbPrivs,
    };

    private static string Q(string id) => "[" + id.Replace("]", "]]") + "]";
    private static string S(string s) => s.Replace("'", "''");

    private async Task ExecAsync(string sql, string? database = null)
    {
        var cs = database is null ? Cs : SqlServerService.WithDatabase(Cs, database);
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync();
    }

    public override async Task<List<SecurityPrincipal>> GetPrincipalsAsync()
    {
        var list = new List<SecurityPrincipal>();
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT name, type, CAST(ISNULL(is_disabled,0) AS int)
              FROM sys.server_principals
              WHERE type IN ('S','U','G','R') AND name NOT LIKE '##%'
              ORDER BY type, name", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            var type = r.GetString(1).Trim();
            var disabled = !r.IsDBNull(2) && Convert.ToInt32(r.GetValue(2)) != 0;
            var isRole = type == "R";
            list.Add(new SecurityPrincipal
            {
                Name = name,
                IsRole = isRole,
                CanLogin = !isRole,
                Locked = disabled,
                IsBuiltIn = name is "sa" or "public" || name.StartsWith("NT ", StringComparison.OrdinalIgnoreCase),
            });
        }
        return list;
    }

    public override async Task<List<string>> GetDatabasesAsync()
    {
        var list = new List<string>();
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    public override async Task<HashSet<string>> GetMembershipsAsync(SecurityPrincipal p)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT r.name
              FROM sys.server_role_members m
              JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
              JOIN sys.server_principals u ON u.principal_id = m.member_principal_id
              WHERE u.name = @n", conn);
        cmd.Parameters.AddWithValue("@n", p.Name);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }

    public override Task GrantRoleAsync(string role, SecurityPrincipal p) =>
        ExecAsync($"ALTER SERVER ROLE {Q(role)} ADD MEMBER {Q(p.Name)}");

    public override Task RevokeRoleAsync(string role, SecurityPrincipal p) =>
        ExecAsync($"ALTER SERVER ROLE {Q(role)} DROP MEMBER {Q(p.Name)}");

    public override Task CreateUserAsync(string name, string? host, string? password) =>
        ExecAsync(string.IsNullOrEmpty(password)
            ? $"CREATE LOGIN {Q(name)} FROM WINDOWS"
            : $"CREATE LOGIN {Q(name)} WITH PASSWORD = '{S(password)}'");

    public override Task CreateRoleAsync(string name) => ExecAsync($"CREATE SERVER ROLE {Q(name)}");

    public override Task DropAsync(SecurityPrincipal p) =>
        ExecAsync(p.IsRole ? $"DROP SERVER ROLE {Q(p.Name)}" : $"DROP LOGIN {Q(p.Name)}");

    public override Task SetPasswordAsync(SecurityPrincipal p, string password) =>
        ExecAsync($"ALTER LOGIN {Q(p.Name)} WITH PASSWORD = '{S(password)}'");

    public override Task SetLockedAsync(SecurityPrincipal p, bool locked) =>
        ExecAsync($"ALTER LOGIN {Q(p.Name)} {(locked ? "DISABLE" : "ENABLE")}");

    public override async Task<List<PrivilegeItem>> GetPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database)
    {
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scope == PrivilegeScopeKind.Global)
        {
            await using var conn = new SqlConnection(Cs);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                @"SELECT perm.permission_name
                  FROM sys.server_permissions perm
                  JOIN sys.server_principals p ON p.principal_id = perm.grantee_principal_id
                  WHERE p.name = @n AND perm.state = 'G'", conn);
            cmd.Parameters.AddWithValue("@n", p.Name);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) granted.Add(r.GetString(0));
            return BuildItems(ServerPrivs, granted);
        }

        // Database scope — look at the database user mapped to this login (by name).
        await using (var conn = new SqlConnection(SqlServerService.WithDatabase(Cs, database!)))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                @"SELECT dp.permission_name
                  FROM sys.database_permissions dp
                  JOIN sys.database_principals u ON u.principal_id = dp.grantee_principal_id
                  WHERE u.name = @n AND dp.state = 'G' AND dp.class = 0", conn);
            cmd.Parameters.AddWithValue("@n", p.Name);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) granted.Add(r.GetString(0));
        }
        return BuildItems(DbPrivs, granted);
    }

    public override async Task ApplyPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database, IReadOnlyList<PrivilegeItem> items)
    {
        var toGrant = items.Where(i => i.IsDirty && i.Granted).Select(i => i.Keyword).ToList();
        var toRevoke = items.Where(i => i.IsDirty && !i.Granted).Select(i => i.Keyword).ToList();

        if (scope == PrivilegeScopeKind.Global)
        {
            foreach (var k in toGrant) await ExecAsync($"GRANT {k} TO {Q(p.Name)}");
            foreach (var k in toRevoke) await ExecAsync($"REVOKE {k} FROM {Q(p.Name)}");
            return;
        }

        // Ensure a database user exists for the login, then grant/revoke in that database.
        await EnsureDbUserAsync(p, database!);
        foreach (var k in toGrant) await ExecAsync($"GRANT {k} TO {Q(p.Name)}", database);
        foreach (var k in toRevoke) await ExecAsync($"REVOKE {k} FROM {Q(p.Name)}", database);
    }

    private async Task EnsureDbUserAsync(SecurityPrincipal p, string database)
    {
        await using var conn = new SqlConnection(SqlServerService.WithDatabase(Cs, database));
        await conn.OpenAsync();
        await using var check = new SqlCommand(
            "SELECT COUNT(*) FROM sys.database_principals WHERE name = @n", conn);
        check.Parameters.AddWithValue("@n", p.Name);
        if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0) return;
        await using var create = new SqlCommand($"CREATE USER {Q(p.Name)} FOR LOGIN {Q(p.Name)}", conn);
        await create.ExecuteNonQueryAsync();
    }
}
