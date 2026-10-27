namespace CMIS.Core.Security.Acl;

/// <summary>One row of <c>CM.GetPermissions</c>, before project filtering.</summary>
/// <param name="Name">Permission name as stored, for example <c>pipe.pms_thk</c>.</param>
/// <param name="ProjectId">Project the grant belongs to; <c>null</c> means "no project".</param>
/// <param name="Allow"><c>true</c> allow, <c>false</c> deny, <c>null</c> hide.</param>
public sealed record AclGrantRow(string Name, int? ProjectId, bool? Allow);

/// <summary>
/// The grants of one user in one project, plus the resolution rules. This is the object that
/// answers "can this user do X", and the only place those rules are written down.
/// </summary>
/// <remarks>
/// Port of <c>Security.LoginInfo.HasAccess</c> / <c>IsHiddenAccess</c>. The rules, in order:
/// <list type="number">
/// <item>an exact match for the permission wins, <b>even when it is a deny</b>;</item>
/// <item>otherwise the module wildcard <c>SCHEMA.*</c> decides;</item>
/// <item>otherwise the answer is deny — no grant is never an implicit allow.</item>
/// </list>
/// The legacy code also collapsed "hide" (<c>Allow = NULL</c>) into "deny" in every caller; here
/// the third state is preserved all the way to the client, which is the whole point of
/// <see cref="AclGrant"/>.
/// </remarks>
public sealed class AclPermissionSet
{
    private readonly Dictionary<string, AclGrant> _grants;

    public AclPermissionSet(int projectId, IEnumerable<AclGrantRow>? rows)
    {
        // Legacy treated 0 as "no project" (`if (projectId == 0) projectId = null`).
        ProjectId = projectId <= 0 ? null : projectId;
        _grants = new Dictionary<string, AclGrant>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows ?? Array.Empty<AclGrantRow>())
        {
            if (string.IsNullOrWhiteSpace(row.Name)) continue;
            // Rows for other projects are not this user's business. A row with a null project
            // only applies to a null-project query, exactly like the legacy `item.ProjectId == projectId`.
            if (row.ProjectId != ProjectId) continue;

            _grants[row.Name.Trim()] = AclGrantExtensions.FromAllow(row.Allow);
        }
    }

    /// <summary>The project the grants were loaded for; <c>null</c> means "no project".</summary>
    public int? ProjectId { get; }

    /// <summary>Every explicit grant of this user/project. Absence means <see cref="AclGrant.Inherit"/>.</summary>
    public IReadOnlyDictionary<string, AclGrant> Grants => _grants;

    /// <summary>Resolves one permission through the exact → wildcard → deny chain.</summary>
    public AclGrant Resolve(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission)) return AclGrant.Deny;

        var name = permission.Trim();

        if (_grants.TryGetValue(name, out var exact))
            return exact;

        // Module wildcard: PIPE.PMS_THK falls back to PIPE.*, never to another module's "*".
        var dot = name.IndexOf('.');
        if (dot > 0 && _grants.TryGetValue($"{name[..dot]}.*", out var any))
            return any;

        return AclGrant.Deny;
    }

    /// <summary>True only for an explicit or wildcard Allow.</summary>
    public bool IsAllowed(string permission) => Resolve(permission).IsGranted();

    /// <summary>True only for <c>Allow = NULL</c>: the client must not render the feature at all.</summary>
    public bool IsHidden(string permission) => Resolve(permission) == AclGrant.Hidden;

    /// <summary>
    /// Effective level of every permission the application declares, in the exact shape the web
    /// client's ACL store expects: <c>{ "PIPE.PMS_THK": 1, "PIPE.COMPANY": 0, "PIPE.X": null }</c>.
    /// </summary>
    /// <remarks>
    /// Inheritance is resolved here, so the client never implements the wildcard rule and can
    /// never disagree with the server about it.
    /// </remarks>
    public IReadOnlyDictionary<string, int?> ToAccessMap(AclRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var map = new Dictionary<string, int?>(registry.Permissions.Count, StringComparer.Ordinal);
        foreach (var permission in registry.Permissions)
            map[permission.QualifiedName] = Resolve(permission.QualifiedName).ToAccessLevel();

        return map;
    }
}
