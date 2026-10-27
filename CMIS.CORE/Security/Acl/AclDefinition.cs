namespace CMIS.Core.Security.Acl;

/// <summary>
/// One grantable permission, as declared in code. Carries only metadata: no user, no project,
/// no value. The same definition is used by the resolver, by the ACL editor tree and by the
/// JSON catalogue the web client reads.
/// </summary>
public sealed record AclDefinition(
    string Schema,
    string Name,
    string Caption,
    string? Description,
    AclPermissionKind Kind,
    string? ItemsSource)
{
    /// <summary>
    /// <c>SCHEMA.NAME</c>, for example <c>PIPE.PMS_THK</c>. This is the string stored in the
    /// ACL table and the key of the client's access map.
    /// </summary>
    public string QualifiedName => $"{Schema}.{Name}";
}

/// <summary>All permissions of one module, the group header of an ACL editor.</summary>
public sealed record AclCollection(
    string Schema,
    string Caption,
    IReadOnlyList<AclDefinition> Permissions);
