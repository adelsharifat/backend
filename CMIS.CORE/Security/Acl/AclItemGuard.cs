using CMIS.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CMIS.Core.Security.Acl;

/// <summary>
/// Row-level half of a permission, enforced where it matters: on the **server**.
/// </summary>
/// <remarks>
/// A permission of kind <see cref="AclPermissionKind.Items"/> does not only say yes or no, it carries
/// the ids a user may use (`CM.GetAllowedItems`, the legacy `LoginInfo.GetAllowedItems`). The client
/// uses them to fill a combo, but a combo is not a security control: the id that comes back in a
/// request can be edited by hand.
///
/// The legacy application had the same gap and closed it with `Acl.AllowItem` inside each form. Here
/// it is one method that every endpoint taking a scoped id calls before touching the database:
///
/// <code>
/// var guard = await permissions.CheckAllowedItemsAsync(PipeAcl.COMPANY, [companyId], cancellationToken);
/// if (!guard.Allowed) return this.AclItemsDenied(PipeAcl.COMPANY, guard.Denied);
/// </code>
///
/// Two rules are inherited from the legacy resolution and both are deliberate:
/// <list type="bullet">
/// <item>a permission that is **denied or hidden** allows nothing, whatever the item rows say — the
/// feature is off, so its ids are not usable either;</item>
/// <item>an empty allowed list means "nothing allowed", never "everything". A user whose grant came
/// only from the module wildcard (<c>PIPE.*</c>) and who has no item rows for the permission
/// therefore gets a 403 here, which is exactly what the legacy combos did: they listed nothing.</item>
/// </list>
/// </remarks>
public static class AclItemGuard
{
    /// <summary>Outcome of a check: `Allowed`, plus the offending ids when it is not.</summary>
    public sealed record Result(bool Allowed, IReadOnlyList<int> Denied)
    {
        /// <summary>Everything that was asked for is allowed (including "nothing was asked for").</summary>
        public static readonly Result Ok = new(true, Array.Empty<int>());
    }

    /// <summary>
    /// Checks a set of ids against the allowed items of <paramref name="permission"/>.
    /// </summary>
    /// <remarks>
    /// Ids that are <c>0</c> or negative are ignored: they mean "not selected yet" and are rejected by
    /// the endpoint's own validation, not by this guard.
    /// </remarks>
    public static async Task<Result> CheckAllowedItemsAsync(
        this IPermissionService permissions,
        string permission,
        IEnumerable<int>? ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var requested = (ids ?? Array.Empty<int>()).Where(id => id > 0).Distinct().ToArray();
        if (requested.Length == 0) return Result.Ok;

        var set = await permissions.GetSetAsync(cancellationToken);
        // Deny and Hide both allow nothing, and the item rows are not even read.
        if (!set.IsAllowed(permission)) return new Result(false, requested);

        var allowed = await permissions.GetAllowedItemsAsync(permission, cancellationToken);
        var denied = requested.Where(id => !allowed.Contains(id)).ToArray();

        return denied.Length == 0 ? Result.Ok : new Result(false, denied);
    }
}

/// <summary>Standard 403 answers for a failed row-level check.</summary>
/// <remarks>
/// The body is an <see cref="ApiError"/>, so the web client shows the reason
/// (`{ code, message }` → `toApiError` → toast) instead of an empty 403.
/// </remarks>
public static class AclGuardResults
{
    /// <summary>The permission itself is not granted to this user.</summary>
    public static IActionResult AclDenied(this ControllerBase controller, string permission) =>
        new ObjectResult(new ApiError(
            "acl_denied",
            $"Permission '{permission}' is not granted for this user."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };

    /// <summary>Some of the submitted ids are outside the allowed set of the permission.</summary>
    public static IActionResult AclItemsDenied(
        this ControllerBase controller,
        string permission,
        IReadOnlyList<int> denied) =>
        new ObjectResult(new ApiError(
            "acl_item_denied",
            $"Value(s) {string.Join(", ", denied)} are not allowed by '{permission}'."))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
}
