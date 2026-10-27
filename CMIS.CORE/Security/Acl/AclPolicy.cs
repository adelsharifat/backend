using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;

namespace CMIS.Core.Security.Acl;

/// <summary>
/// Guards an action with a declared permission:
/// <c>[RequireAcl(PipeAcl.PMS_THK_EDIT)]</c>. The name is checked against
/// <see cref="AclRegistry"/> before the policy is built, so a typo fails loudly instead of
/// silently denying (or, worse, silently allowing) every caller.
/// </summary>
/// <remarks>
/// This replaces the hand-written <c>if (!HasPipePermission(...)) return Forbid();</c> calls.
/// Authorization belongs in the pipeline, not repeated in each action body.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireAclAttribute : AuthorizeAttribute
{
    public RequireAclAttribute(string permission) =>
        Policy = AclPolicyProvider.PolicyPrefix + permission;
}

/// <summary>Requirement produced for every <see cref="RequireAclAttribute"/>.</summary>
public sealed class AclRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Asks <see cref="IPermissionService"/> whether the current user has the permission.</summary>
public sealed class AclAuthorizationHandler(IPermissionService permissions)
    : AuthorizationHandler<AclRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AclRequirement requirement)
    {
        // Hide and Deny both fail here: hiding is a presentation decision, never a way in.
        if (await permissions.IsAllowedAsync(requirement.Permission))
            context.Succeed(requirement);
    }
}

/// <summary>
/// Turns <c>acl:PIPE.PMS_THK</c> into a real policy on demand, so permissions do not have to be
/// registered one by one in <c>AddAuthorization(options => ...)</c> — the registry already has
/// them.
/// </summary>
public sealed class AclPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>Prefix that marks a policy name as an ACL permission.</summary>
    public const string PolicyPrefix = "acl:";

    private readonly ConcurrentDictionary<string, AuthorizationPolicy?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DefaultAuthorizationPolicyProvider _fallback;
    private readonly AclRegistry _registry;

    public AclPolicyProvider(
        Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options,
        AclRegistry registry)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
        _registry = registry;
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(PolicyPrefix, StringComparison.OrdinalIgnoreCase))
            return _fallback.GetPolicyAsync(policyName);

        if (_cache.TryGetValue(policyName, out var cached))
            return Task.FromResult(cached);

        // Accepts the qualified name or a bare name declared by exactly one module, and always
        // resolves to the qualified one: the grant lookup is keyed by "SCHEMA.NAME".
        var definition = _registry.Find(policyName[PolicyPrefix.Length..])
            ?? throw new InvalidOperationException(
                $"Permission '{policyName[PolicyPrefix.Length..]}' is used by [RequireAcl] but is not declared " +
                "in any [AclSchema] class, or is declared by more than one module (qualify it as SCHEMA.NAME).");

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new AclRequirement(definition.QualifiedName))
            .Build();

        _cache[policyName] = policy;
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}
