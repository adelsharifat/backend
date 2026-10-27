using CMIS.Core.Modules;
using CMIS.Core.Security.Acl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // The ACL catalogue is built here, where the module list is finally known, and it is a
        // singleton: reflection runs once per process, and a duplicate permission name fails at
        // startup instead of at the first request that touches it.
        var assemblies = modules.Modules
            .Select(module => module.Assembly)
            .Append(typeof(AclRegistry).Assembly)
            .Distinct();

        services.Replace(ServiceDescriptor.Singleton(new AclRegistry(assemblies)));

        return services;
    }
}

