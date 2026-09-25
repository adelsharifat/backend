using CMIS.Core.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CMIS.Core.Extensions;

public static class CMISModuleExtensions
{
    public static IServiceCollection AddCMISModules(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<CMISModuleCollection> configure)
    {
        var modules = new CMISModuleCollection();
        configure(modules);
        var mvc = services.AddControllers();

        foreach (var module in modules.Modules)
        {
            module.Register(services, configuration);
            mvc.AddApplicationPart(module.Assembly);
        }

        return services;
    }
}
