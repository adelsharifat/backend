using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CMIS.Core.Db;
using CMIS.Core.Security.Acl;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;

namespace CMIS.Core.Security;

/// <inheritdoc />
/// <remarks>
/// One instance per request: the grant set is loaded from <c>CM.GetPermissions</c> at most once
/// and then cached in the instance, and so are the row-level lookups. The next request — and
/// therefore the next user — starts clean.
/// </remarks>
public sealed class PermissionService(DAL dal, AclRegistry registry, IHttpContextAccessor http)
    : IPermissionService
{
    private Task<AclPermissionSet>? _set;
    private readonly Dictionary<string, int[]> _allowedItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _allowedItem = new(StringComparer.OrdinalIgnoreCase);

    public Task<AclPermissionSet> GetSetAsync(CancellationToken cancellationToken = default) =>
        _set ??= LoadAsync(cancellationToken);

    public async Task<AclGrant> ResolveAsync(string permission, CancellationToken cancellationToken = default) =>
        (await GetSetAsync(cancellationToken)).Resolve(permission);

    public async Task<bool> IsAllowedAsync(string permission, CancellationToken cancellationToken = default) =>
        (await ResolveAsync(permission, cancellationToken)).IsGranted();

    public async Task<bool> IsHiddenAsync(string permission, CancellationToken cancellationToken = default) =>
        await ResolveAsync(permission, cancellationToken) == AclGrant.Hidden;

    public async Task<IReadOnlyDictionary<string, int?>> GetAccessMapAsync(CancellationToken cancellationToken = default) =>
        (await GetSetAsync(cancellationToken)).ToAccessMap(registry);

    public async Task<int[]> GetAllowedItemsAsync(string permission, CancellationToken cancellationToken = default)
    {
        if (_allowedItems.TryGetValue(permission, out var cached)) return cached;

        var (userId, projectId) = GetIdentity();
        if (userId <= 0) return _allowedItems[permission] = [];

        var table = await dal.QueryTableAsync("CM.GetAllowedItems", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = ProjectParameter(projectId) },
            new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = ToStoredName(permission) });

        // Ordinal access on purpose: the legacy reader used the lowercase column "value", and
        // the procedure only ever returns one column.
        return _allowedItems[permission] = table.Rows
            .Cast<DataRow>()
            .Select(row => Convert.ToInt32(row[0]))
            .ToArray();
    }

    public async Task<int?> GetAllowedItemAsync(string permission, CancellationToken cancellationToken = default)
    {
        if (_allowedItem.TryGetValue(permission, out var cached)) return cached;

        var (userId, projectId) = GetIdentity();
        if (userId <= 0) return _allowedItem[permission] = null;

        var value = new SqlParameter("@Value", SqlDbType.Int) { Direction = ParameterDirection.Output };

        // ExecuteNonQuery: the procedure communicates through the output parameter only, so
        // there is no result set to consume.
        await dal.ExecuteAsync("CM.GetAllowedItem", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = ProjectParameter(projectId) },
            new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = ToStoredName(permission) },
            value);

        return _allowedItem[permission] =
            value.Value is null or DBNull ? null : Convert.ToInt32(value.Value);
    }

    private async Task<AclPermissionSet> LoadAsync(CancellationToken cancellationToken)
    {
        var (userId, projectId) = GetIdentity();

        // No user, no grants. Every caller then resolves to Deny, which is the safe direction.
        if (userId <= 0) return new AclPermissionSet(projectId, null);

        var table = await dal.QueryTableAsync("CM.GetPermissions", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = ProjectParameter(projectId) });

        foreach (var column in new[] { "Name", "ProjectId", "Allow" })
            if (!table.Columns.Contains(column))
                throw new InvalidOperationException($"CM.GetPermissions must return a '{column}' column.");

        var rows = table.Rows.Cast<DataRow>().Select(row => new AclGrantRow(
            Name: Convert.ToString(row["Name"])?.Trim() ?? string.Empty,
            ProjectId: row.IsNull("ProjectId") ? null : Convert.ToInt32(row["ProjectId"]),
            Allow: row.IsNull("Allow") ? null : Convert.ToBoolean(row["Allow"])));

        return new AclPermissionSet(projectId, rows);
    }

    /// <summary>User id and project id of the access token. Missing claims come back as 0.</summary>
    private (int UserId, int ProjectId) GetIdentity()
    {
        var principal = http.HttpContext?.User;

        var userId = int.TryParse(principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : 0;
        var projectId = int.TryParse(principal?.FindFirstValue("project"), out var project) ? project : 0;

        return (userId, projectId);
    }

    private static object ProjectParameter(int projectId) => projectId > 0 ? projectId : DBNull.Value;

    /// <summary>
    /// The ACL table stores names in lower case (<c>pipe.company</c>) and the legacy client
    /// lower-cased before every lookup. Doing the same keeps this compatible with rows written
    /// by the WinForms application.
    /// </summary>
    private static string ToStoredName(string permission) => permission.Trim().ToLowerInvariant();
}
