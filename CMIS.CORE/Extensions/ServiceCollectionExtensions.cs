using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authorization;
using CMIS.Core.Jwt;
using CMIS.Core.Security;
using CMIS.Core.Security.Acl;
using CMIS.Core.Services;
using CMIS.Core.Db;

namespace CMIS.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreInfrastructure(this IServiceCollection services, IConfiguration cfg)
    {
        // 1️⃣ JWT options (unchanged)
        services.Configure<JwtOptions>(cfg.GetSection("Jwt"));

        // 2️⃣ Register the shared, inheritable ADO.NET data-access base class.
        services.AddSingleton<DAL>();

        // 3️⃣ ACL. The catalogue starts empty: AddCMISModules replaces it with one built from the
        //    modules that are actually registered, so a module that is not in the host can never
        //    contribute a grantable permission.
        services.TryAddSingleton(AclRegistry.Empty);
        services.AddHttpContextAccessor();
        services.AddAuthorization();
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, AclPolicyProvider>());
        services.AddScoped<IAuthorizationHandler, AclAuthorizationHandler>();
        services.AddScoped<IPermissionService, PermissionService>();

        // 4️⃣ Register the auth service (uses DAL internally)
        services.AddScoped<IAuthService, AuthService>();

        // 5️⃣ Register any other module services here (e.g., IWarehouseService)
        // services.AddScoped<IWarehouseService, WarehouseService>();

        return services;
    }
}

