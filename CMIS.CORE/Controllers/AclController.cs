using CMIS.Core.Security;
using CMIS.Core.Security.Acl;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CMIS.Core.Controllers;

/// <summary>What the current user may do, already resolved to Allow / Deny / Hide.</summary>
/// <param name="ProjectId">Project the grants were read for. <c>null</c> means "no project".</param>
/// <param name="Access">
/// Permission name -> level: <c>1</c> allow, <c>0</c> deny, <c>null</c> hide. Every permission
/// declared in an <c>[AclSchema]</c> class is present, so a missing key on the client always
/// means "this permission does not exist", never "not loaded yet".
/// </param>
public sealed record AclPermissionsResponse(int? ProjectId, IReadOnlyDictionary<string, int?> Access);

/// <summary>One permission in the catalogue, for an ACL editor.</summary>
public sealed record AclPermissionResponse(
    string Name,
    string Caption,
    string? Description,
    string Kind,
    string? ItemsSource);

/// <summary>The catalogue, grouped by module.</summary>
public sealed record AclCollectionResponse(string Schema, string Caption, IReadOnlyList<AclPermissionResponse> Permissions);

/// <summary>Result of a single permission check, useful for diagnostics and parity tests.</summary>
public sealed record AclCheckResponse(string Permission, string Grant, int? Access);

/// <summary>
/// Read-only view of the permission system: what the caller may do, what can be granted at all,
/// and the row-level ids attached to an Items permission.
/// </summary>
/// <remarks>
/// Grants are never written here. The ACL table is owned by the WinForms administration tool
/// (<c>SecurityManagement</c> / <c>frmAclManagement</c>, stored procedures <c>CM.SaveUserAcl</c>
/// and <c>CM.SaveGroupAcl</c>), and duplicating that write path before the web editor exists
/// would create two ways to corrupt the same rows.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class AclController(IPermissionService permissions, AclRegistry registry) : ControllerBase
{
    /// <summary>GET api/acl/permissions — the payload the web client's ACL store loads at sign-in.</summary>
    [HttpGet("permissions")]
    public async Task<IActionResult> GetPermissions(CancellationToken cancellationToken)
    {
        var set = await permissions.GetSetAsync(cancellationToken);
        var access = await permissions.GetAccessMapAsync(cancellationToken);
        return Ok(new AclPermissionsResponse(set.ProjectId, access));
    }

    /// <summary>GET api/acl/registry — every grantable permission, grouped by module.</summary>
    [HttpGet("registry")]
    public IActionResult GetRegistry() =>
        Ok(registry.Collections.Select(collection => new AclCollectionResponse(
            collection.Schema,
            collection.Caption,
            collection.Permissions.Select(permission => new AclPermissionResponse(
                permission.QualifiedName,
                permission.Caption,
                permission.Description,
                permission.Kind.ToString(),
                permission.ItemsSource)).ToArray())).ToArray());

    /// <summary>GET api/acl/check/PIPE.PMS_THK — one resolved grant.</summary>
    [HttpGet("check/{permission}")]
    public async Task<IActionResult> Check(string permission, CancellationToken cancellationToken)
    {
        if (registry.Find(permission) is null) return NotFound();
        var grant = await permissions.ResolveAsync(permission, cancellationToken);
        return Ok(new AclCheckResponse(permission, grant.ToString(), grant.ToAccessLevel()));
    }

    /// <summary>
    /// GET api/acl/items/PIPE.COMPANY — ids the caller may use, for filtering rows
    /// (<c>CM.GetAllowedItems</c>).
    /// </summary>
    [HttpGet("items/{permission}")]
    public async Task<IActionResult> GetItems(string permission, CancellationToken cancellationToken)
    {
        if (registry.Find(permission) is not { Kind: AclPermissionKind.Items })
            return NotFound();

        return Ok(new { Permission = permission, Values = await permissions.GetAllowedItemsAsync(permission, cancellationToken) });
    }

    /// <summary>GET api/acl/item/PIPE.SOMETHING — the single selected value (<c>CM.GetAllowedItem</c>).</summary>
    [HttpGet("item/{permission}")]
    public async Task<IActionResult> GetItem(string permission, CancellationToken cancellationToken)
    {
        if (registry.Find(permission) is not { Kind: AclPermissionKind.Value })
            return NotFound();

        return Ok(new { Permission = permission, Value = await permissions.GetAllowedItemAsync(permission, cancellationToken) });
    }
}
