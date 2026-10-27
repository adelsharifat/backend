namespace CMIS.Core.Security.Acl;

/// <summary>
/// The four states a permission can have for one user in one project. This is the same
/// vocabulary as the ACL editor of the WinForms application (Allow / Deny / Hide / Inherit).
/// </summary>
/// <remarks>
/// The database stores three of them: <c>true</c> = Allow, <c>false</c> = Deny and
/// <c>NULL</c> = Hide. <see cref="Inherit"/> is the absence of a row, so it has no database
/// value and no wire value: the server resolves it before anything reaches a client.
/// </remarks>
public enum AclGrant
{
    /// <summary>No row for this user/project. The module wildcard or the default decides.</summary>
    Inherit = 0,

    /// <summary>Row with <c>Allow = 1</c>.</summary>
    Allow = 1,

    /// <summary>Row with <c>Allow = 0</c>.</summary>
    Deny = 2,

    /// <summary>Row with <c>Allow = NULL</c>. The caller must not show the feature at all.</summary>
    Hidden = 3,
}

public static class AclGrantExtensions
{
    /// <summary>Maps the nullable <c>Allow</c> column to a grant.</summary>
    public static AclGrant FromAllow(bool? allow) => allow switch
    {
        true => AclGrant.Allow,
        false => AclGrant.Deny,
        null => AclGrant.Hidden,
    };

    /// <summary>
    /// The value the HTTP API and the web client use: <c>1</c> allow, <c>0</c> deny,
    /// <c>null</c> hide. These are the exact numbers in <c>core/acl/access.ts</c>, so the client
    /// needs no translation table.
    /// </summary>
    /// <remarks>
    /// <see cref="AclGrant.Inherit"/> maps to <c>0</c> on purpose. Reaching this method with an
    /// unresolved grant means the caller skipped resolution, and an unresolved permission must
    /// never widen access.
    /// </remarks>
    public static int? ToAccessLevel(this AclGrant grant) => grant switch
    {
        AclGrant.Allow => 1,
        AclGrant.Deny => 0,
        AclGrant.Hidden => null,
        _ => 0,
    };

    /// <summary>True only for <see cref="AclGrant.Allow"/>: Deny and Hidden are both "no".</summary>
    public static bool IsGranted(this AclGrant grant) => grant == AclGrant.Allow;
}
