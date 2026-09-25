using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace CMIS.Core.Modules;

/// <summary>
/// Contract implemented by each CMIS module to register its own services.
/// Controllers are registered automatically by the central module loader.
/// </summary>
public interface ICMISModule
{
    string Name { get; }
    string Version { get; }
    Assembly Assembly { get; }
    void Register(IServiceCollection services, IConfiguration configuration);
}
