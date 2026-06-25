using FirebirdSql.Data.FirebirdClient;
using DataPortStudio.Models;

namespace DataPortStudio.Services.Security;

/// <summary>
/// User / role management for Firebird 3+ (SEC$USERS + RDB$ROLES). Table-level privileges are
/// per-object in Firebird and aren't exposed in the grid yet; role membership is fully supported.
/// </summary>
public sealed class FirebirdSecurityProvider : SecurityProvider
{
    public FirebirdSecurityProvider(ConnectionProfile p) : base(p) { }

    public override SecurityCapabilities Capabilities => new()
    {
        HasHost = false,
        SupportsLock = false,
        SupportsRoles = true,
        SupportsDatabaseScope = false,
        GlobalScopeLabel = "Privileges",
        UserNoun = "User",
        Note = "Firebird users are server-level (requires SYSDBA). Per-table privileges are managed " +
               "with GRANT/REVOKE in a query window; role membership is editable here.",
        GlobalPrivileges = Array.Empty<string>(),
        DatabasePrivileges = Array.Empty<string>(),
    };

    private async Task ExecAsync(string sql)
    {
        await using var conn = new FbConnection(Cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public override async Task<List<SecurityPrincipal>> GetPrincipalsAsync()
    {
        var list = new List<SecurityPrincipal>();
        await using var conn = new FbConnection(Cs);
        await conn.OpenAsync();

        // Users (Firebird 3+ exposes SEC$USERS; older servers may not).
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SEC$USER_NAME FROM SEC$USERS ORDER BY SEC$USER_NAME";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var name = r.GetString(0).Trim();
                list.Add(new SecurityPrincipal
                {
                    Name = name,
                    CanLogin = true,
                    IsBuiltIn = name.Equals("SYSDBA", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch { /* SEC$USERS unavailable (pre-FB3 or insufficient rights) */ }

        // Roles.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT RDB$ROLE_NAME FROM RDB$ROLES WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0 ORDER BY RDB$ROLE_NAME";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var name = r.GetString(0).Trim();
                list.Add(new SecurityPrincipal { Name = name, IsRole = true, CanLogin = false });
            }
        }
        return list;
    }

    public override Task<List<string>> GetDatabasesAsync() => Task.FromResult(new List<string>());

    public override async Task<HashSet<string>> GetMembershipsAsync(SecurityPrincipal p)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new FbConnection(Cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT RDB$RELATION_NAME FROM RDB$USER_PRIVILEGES " +
            "WHERE RDB$USER = @u AND RDB$OBJECT_TYPE = 13 AND RDB$PRIVILEGE = 'M'";
        cmd.Parameters.AddWithValue("@u", p.Name);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0).Trim());
        return set;
    }

    public override Task GrantRoleAsync(string role, SecurityPrincipal p) =>
        ExecAsync($"GRANT {role} TO {p.Name}");

    public override Task RevokeRoleAsync(string role, SecurityPrincipal p) =>
        ExecAsync($"REVOKE {role} FROM {p.Name}");

    public override Task CreateUserAsync(string name, string? host, string? password) =>
        ExecAsync(string.IsNullOrEmpty(password)
            ? throw new InvalidOperationException("Firebird requires a password to create a user.")
            : $"CREATE USER {name} PASSWORD '{password.Replace("'", "''")}'");

    public override Task CreateRoleAsync(string name) => ExecAsync($"CREATE ROLE {name}");

    public override Task DropAsync(SecurityPrincipal p) =>
        ExecAsync(p.IsRole ? $"DROP ROLE {p.Name}" : $"DROP USER {p.Name}");

    public override Task SetPasswordAsync(SecurityPrincipal p, string password) =>
        ExecAsync($"ALTER USER {p.Name} PASSWORD '{password.Replace("'", "''")}'");

    public override Task SetLockedAsync(SecurityPrincipal p, bool locked) =>
        throw new NotSupportedException("Firebird does not support locking accounts.");

    public override Task<List<PrivilegeItem>> GetPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database) =>
        Task.FromResult(new List<PrivilegeItem>());

    public override Task ApplyPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database, IReadOnlyList<PrivilegeItem> items) =>
        Task.CompletedTask;
}
