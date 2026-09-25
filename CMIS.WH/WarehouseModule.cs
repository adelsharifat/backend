using CMIS.Core.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace CMIS.WH;

/// <summary>
/// Assembly marker for the Warehouse module.
/// Add warehouse-specific controllers, services, models, and database access here.
/// </summary>
public sealed class WarehouseModule : ICMISModule
{
    public const string ModuleName = "Warehouse";
    public const string ModuleVersion = "1.0.3";

    public string Name => ModuleName;
    public string Version => ModuleVersion;
    public Assembly Assembly => typeof(WarehouseModule).Assembly;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Register Warehouse services here as they are added.
    }
}
