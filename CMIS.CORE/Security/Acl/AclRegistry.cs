using System.Reflection;

namespace CMIS.Core.Security.Acl;

/// <summary>
/// The catalogue of every permission the application can grant, built once at startup by
/// reflecting over the assemblies that are actually loaded.
/// </summary>
/// <remarks>
/// This is the port of <c>Security.Acl.DefinedPermissions</c> (and its later rewrite
/// <c>GuardPolicy</c>) from the WinForms application, with two deliberate differences:
/// <list type="bullet">
/// <item>it scans an explicit list of assemblies instead of <c>AppDomain.CurrentDomain</c>, so a
/// module that is not registered cannot inject permissions;</item>
/// <item>duplicate names throw at startup instead of silently overwriting, because two modules
/// claiming <c>PIPE.PMS_THK</c> would make the ACL editor and the resolver disagree.</item>
/// </list>
/// A permission that is not declared here can never be granted, and a permission declared here
/// is always resolvable — that is what keeps the database and the code from drifting apart.
/// </remarks>
public sealed class AclRegistry
{
    private readonly Dictionary<string, AclDefinition> _byName;

    /// <summary>An empty catalogue, for hosts that run without modules.</summary>
    public static AclRegistry Empty { get; } = new(Array.Empty<Assembly>());

    public AclRegistry(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var collections = new List<AclCollection>();
        _byName = new Dictionary<string, AclDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies.Where(a => a is not null).Distinct())
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                var schema = type.GetCustomAttribute<AclSchemaAttribute>();
                if (schema is null) continue;

                var permissions = new List<AclDefinition>();
                foreach (var field in type
                    .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                    .OrderBy(f => f.MetadataToken))
                {
                    var attribute = field.GetCustomAttribute<AclPermissionAttribute>();
                    if (attribute is null) continue;

                    var name = field.GetRawConstantValue() as string;
                    if (string.IsNullOrWhiteSpace(name))
                        throw new InvalidOperationException(
                            $"[AclPermission] on {type.FullName}.{field.Name} must declare the permission name.");

                    var definition = new AclDefinition(
                        Schema: schema.Name,
                        Name: name,
                        Caption: attribute.Caption,
                        Description: attribute.Description,
                        Kind: attribute.Kind,
                        ItemsSource: attribute.ItemsSource);

                    if (!_byName.TryAdd(definition.QualifiedName, definition))
                        throw new InvalidOperationException(
                            $"Permission '{definition.QualifiedName}' is declared by both " +
                            $"'{_byName[definition.QualifiedName].Schema}' and '{type.FullName}'.");

                    permissions.Add(definition);
                }

                if (permissions.Count > 0)
                    collections.Add(new AclCollection(schema.Name, schema.Caption ?? schema.Name, permissions));
            }
        }

        Collections = collections;
        Permissions = collections.SelectMany(c => c.Permissions).ToArray();
    }

    /// <summary>Every module, in declaration order — the shape an ACL editor renders.</summary>
    public IReadOnlyList<AclCollection> Collections { get; }

    /// <summary>Flat list of every declared permission.</summary>
    public IReadOnlyList<AclDefinition> Permissions { get; }

    /// <summary>Definition of one permission, or <c>null</c> when the name is not declared.</summary>
    /// <remarks>
    /// Accepts the qualified name (<c>PIPE.PMS_THK</c>) or a bare name (<c>PMS_THK</c>) that is
    /// declared by exactly one module. The bare form is what module code naturally has —
    /// <c>[RequireAcl(PipeAcl.PMS_THK)]</c> — so it must work. An ambiguous bare name returns
    /// <c>null</c> instead of guessing: two modules claiming <c>COMPANY</c> are a design problem,
    /// and silently picking one would guard the wrong permission.
    /// </remarks>
    public AclDefinition? Find(string? permission)
    {
        if (string.IsNullOrWhiteSpace(permission)) return null;

        var name = permission.Trim();

        if (_byName.TryGetValue(name, out var qualified))
            return qualified;

        // A qualified name that was not found is simply unknown: never fall back to a bare match.
        if (name.Contains('.')) return null;

        AclDefinition? match = null;
        foreach (var candidate in Permissions)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) return null;
            match = candidate;
        }

        return match;
    }

    /// <summary>Everything declared for one module prefix.</summary>
    public AclCollection? FindCollection(string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? null
            : Collections.FirstOrDefault(c => string.Equals(c.Schema, schema.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A module with an optional dependency missing still contributes the types it can load.
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
