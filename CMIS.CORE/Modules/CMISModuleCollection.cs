namespace CMIS.Core.Modules;

public sealed class CMISModuleCollection
{
    private readonly List<ICMISModule> _modules = [];
    public IReadOnlyList<ICMISModule> Modules => _modules;

    public CMISModuleCollection Add(ICMISModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _modules.Add(module);
        return this;
    }

    public CMISModuleCollection Add<TModule>() where TModule : ICMISModule, new() => Add(new TModule());
}
