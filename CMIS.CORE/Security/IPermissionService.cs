using CMIS.Core.Security.Acl;

namespace CMIS.Core.Security;

/// <summary>
/// Answers permission questions for the <b>current request</b> (user and project come from the
/// access token).
/// </summary>
/// <remarks>
/// This replaces the static <c>Security.LoginInfo</c> of the WinForms application. That class
/// kept the permission list and a result cache in <c>static</c> fields, which is safe in a
/// desktop process with one signed-in user and is a cross-user data leak in a web server. Here
/// the instance is scoped to the request and nothing is shared between users.
/// </remarks>
public interface IPermissionService
{
    /// <summary>Grants of the current user, already filtered by the current project.</summary>
    Task<AclPermissionSet> GetSetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The name the ACL table and the grant set are keyed by: <c>PIPE.COMPANY</c> for the module
    /// constant <c>PipeAcl.COMPANY</c> (<c>"COMPANY"</c>).
    /// </summary>
    /// <remarks>
    /// A bare name is what module code naturally holds, but <see cref="AclPermissionSet"/> resolves
    /// by qualified name — and a name without a dot has no module wildcard either, so resolving
    /// <c>"COMPANY"</c> as-is answers <c>Deny</c> for a user who is allowed <c>PIPE.COMPANY</c>.
    /// Callers that hand a permission straight to the set (or to
    /// <c>CM.GetAllowedItems</c>) must qualify it first; this returns the input unchanged when the
    /// name is not declared, so an unknown permission still resolves to deny.
    /// </remarks>
    string Qualify(string permission);

    /// <summary>Allow / Deny / Hide / Inherit-resolved grant for one permission.</summary>
    Task<AclGrant> ResolveAsync(string permission, CancellationToken cancellationToken = default);

    /// <summary>True only for an allow. Deny and Hide are both "no".</summary>
    Task<bool> IsAllowedAsync(string permission, CancellationToken cancellationToken = default);

    /// <summary>True when the grant is <c>NULL</c>, so the client must not render the feature.</summary>
    Task<bool> IsHiddenAsync(string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Allowed ids of an <see cref="AclPermissionKind.Items"/> permission, for filtering rows
    /// (<c>CM.GetAllowedItems</c>). Empty means "nothing allowed", never "everything".
    /// </summary>
    Task<int[]> GetAllowedItemsAsync(string permission, CancellationToken cancellationToken = default);

    /// <summary>Selected value of an <see cref="AclPermissionKind.Value"/> permission (<c>CM.GetAllowedItem</c>).</summary>
    Task<int?> GetAllowedItemAsync(string permission, CancellationToken cancellationToken = default);

    /// <summary>Effective level of every declared permission, ready for the client's ACL store.</summary>
    Task<IReadOnlyDictionary<string, int?>> GetAccessMapAsync(CancellationToken cancellationToken = default);
}
