using System.Text.Json;

internal static class CmisCli
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CMIS", "CodeGen");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        if (args[0].Equals("module", StringComparison.OrdinalIgnoreCase))
            return HandleModule(args.Skip(1).ToArray());

        if (args[0].Equals("generate", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("g", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Missing generator name. Example: cmis generate tvp --schema PIPE");
                return 2;
            }

            if (args[1].Equals("tvp", StringComparison.OrdinalIgnoreCase))
                return await Generator.RunAsync(args.Skip(2).ToArray());

            if (args[1].Equals("dal", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("The DAL generator is not implemented in this CLI foundation yet.");
                Console.Error.WriteLine("The command shape is reserved: cmis generate dal <ProcedureName> [--module <Module>].");
                return 2;
            }

            Console.Error.WriteLine($"Unknown generator '{args[1]}'.");
            return 2;
        }

        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintHelp();
        return 2;
    }

    private static int HandleModule(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("""
                Module commands:
                  cmis module set <MODULE>
                  cmis module current
                """);
            return 0;
        }

        if (args[0].Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            var module = ReadActiveModule();
            Console.WriteLine(module is null
                ? "No active module is set. Run: cmis module set PIPE"
                : $"Active module: {module}");
            return module is null ? 1 : 0;
        }

        if (args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("Usage: cmis module set <MODULE>");
                return 2;
            }

            var module = args[1].Trim().ToUpperInvariant();
            if (!module.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                Console.Error.WriteLine("Module names may contain only letters, digits, and underscores.");
                return 2;
            }

            Directory.CreateDirectory(SettingsDirectory);
            var settings = new CliSettings(module);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            Console.WriteLine($"Active module set to {module}.");
            return 0;
        }

        Console.Error.WriteLine($"Unknown module command '{args[0]}'.");
        return 2;
    }

    private static string? ReadActiveModule()
    {
        if (!File.Exists(SettingsPath))
            return null;

        try
        {
            var settings = JsonSerializer.Deserialize<CliSettings>(File.ReadAllText(SettingsPath));
            return string.IsNullOrWhiteSpace(settings?.ActiveModule)
                ? null
                : settings.ActiveModule;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"Could not read CLI settings: {SettingsPath}");
            return null;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        CMIS developer CLI

        Usage:
          cmis module set <MODULE>
          cmis module current
          cmis generate tvp [options]
          cmis generate dal <ProcedureName> [--module <MODULE>]
          cmis g <generator> [arguments...]

        Global options:
          -h, --help       Show help

        The active module is stored per Windows user. An explicit --module on a
        generation command overrides it for that invocation only.
        """);

    private sealed record CliSettings(string ActiveModule);
}
