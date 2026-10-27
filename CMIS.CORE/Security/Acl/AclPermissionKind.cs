namespace CMIS.Core.Security.Acl;

/// <summary>
/// What a permission grants besides allowing or denying the action itself. Mirrors
/// <c>Security.Acl.DefinedPermission.Type</c> in the WinForms application.
/// </summary>
public enum AclPermissionKind
{
    /// <summary>A plain switch. Nothing is stored beyond allow/deny/hide.</summary>
    Static = 0,

    /// <summary>
    /// One selected value, for example a default unit. Read with
    /// <c>CM.GetAllowedItem</c> (legacy <c>Type.Value</c>).
    /// </summary>
    Value = 1,

    /// <summary>
    /// A set of allowed ids used to filter rows, for example the companies a user may see.
    /// Read with <c>CM.GetAllowedItems</c> (legacy <c>Type.Items</c>).
    /// </summary>
    Items = 2,
}
