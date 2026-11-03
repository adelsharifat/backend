using System.Reflection;
using CMIS.Core.Modules;
using CMIS.PIPE.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CMIS.PIPE;

public sealed class PipeModule : ICMISModule
{
    public const string ModuleName = "Pipe";
    public const string ModuleVersion = "1.0.0";

    public string Name => ModuleName;
    public string Version => ModuleVersion;
    public Assembly Assembly => typeof(PipeModule).Assembly;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PipeDal>();
    }
}
