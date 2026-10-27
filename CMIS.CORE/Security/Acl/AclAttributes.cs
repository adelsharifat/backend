namespace CMIS.Core.Security.Acl;

/// <summary>
/// Marks a class that declares the permission vocabulary of one module. The name becomes the
/// prefix of every permission in the class, so <c>[AclSchema("PIPE")]</c> plus
/// <c>const string PMS_THK = "PMS_THK"</c> produces <c>PIPE.PMS_THK</c> — exactly the string
/// already stored in the ACL table.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AclSchemaAttribute : Attribute
{
    public AclSchemaAttribute(string name) => Name = name;

    /// <summary>Module prefix, for example <c>PIPE</c>.</summary>
    public string Name { get; }

    /// <summary>Text shown as the group header in an ACL editor.</summary>
    public string? Caption { get; init; }
}

/// <summary>
/// Declares one grantable permission on a <c>public const string</c> field. The field value is
/// the permission name, so it must be the same text the database stores
/// (<c>PMS_THK</c>, <c>COMPANY</c>, <c>*</c>).
/// </summary>
/// <remarks>
/// This is the port of <c>Security.Acl.Access</c> from the WinForms application. The difference
/// is that the legacy attribute pointed at a C# method that built a DevExpress editor for the
/// admin tree; here <see cref="ItemsSource"/> is a relative API route the admin screen calls
/// instead, so a module still owns its own data without the core knowing about DevExpress.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class AclPermissionAttribute : Attribute
{
    public AclPermissionAttribute(string caption) => Caption = caption;

    /// <summary>Short human description of the action or tile being guarded.</summary>
    public string Caption { get; }

    /// <summary>Longer explanation, if the caption is not enough.</summary>
    public string? Description { get; init; }

    public AclPermissionKind Kind { get; init; } = AclPermissionKind.Static;

    /// <summary>
    /// For <see cref="AclPermissionKind.Items"/> and <see cref="AclPermissionKind.Value"/>:
    /// relative API route that returns the selectable values, for example
    /// <c>pipe/base-info/companies</c>. No reflection call, no editor type leaking into the core.
    /// </summary>
    public string? ItemsSource { get; init; }
}
