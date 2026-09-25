using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CMIS.Core.Jwt;
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

        // 3️⃣ Register the auth service (uses DAL internally)
        services.AddScoped<IAuthService, AuthService>();

        // 4️⃣ Register any other module services here (e.g., IWarehouseService)
        // services.AddScoped<IWarehouseService, WarehouseService>();

        return services;
    }
}
