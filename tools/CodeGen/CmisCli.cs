using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

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

        if (args[0].Equals("connection", StringComparison.OrdinalIgnoreCase))
            return await HandleConnectionAsync(args.Skip(1).ToArray());

        if (args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || args[1] is "--help" or "-h")
            {
                PrintGenerateHelp();
                return 0;
            }

            if (args[1].Equals("tvp", StringComparison.OrdinalIgnoreCase))
            {
                string? savedConnection;
                try
                {
                    savedConnection = ConnectionStringStore.Load();
                }
                catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Console.Error.WriteLine($"Could not read the saved connection ({ex.GetType().Name}). Run 'cmis connection set' again.");
                    return 2;
                }

                return await Generator.RunAsync(args.Skip(2).ToArray(), savedConnection);
            }

            if (args[1].Equals("dal", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("The DAL generator is not implemented yet.");
                Console.Error.WriteLine("Reserved command: cmis generate dal <ProcedureName> [--module <MODULE>].");
                return 2;
            }

            Console.Error.WriteLine($"Unknown generator '{args[1]}'.");
            return 2;
        }

        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintHelp();
        return 2;
    }

    private static async Task<int> HandleConnectionAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintConnectionHelp();
            return 0;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "set":
                {
                    if (args.Length != 1)
                    {
                        Console.Error.WriteLine("Usage: cmis connection set");
                        return 2;
                    }

                    Console.WriteLine("Enter SQL Server connection string. Input is hidden:");
                    var value = ReadHiddenInput();
                    if (value is null)
                    {
                        Console.WriteLine("Cancelled.");
                        return 1;
                    }

                    value = value.Trim();
                    if (value.Length == 0)
                    {
                        Console.Error.WriteLine("Connection string cannot be empty.");
                        return 2;
                    }

                    var builder = new SqlConnectionStringBuilder(value);
                    if (string.IsNullOrWhiteSpace(builder.DataSource))
                    {
                        Console.Error.WriteLine("Connection string must include Server/Data Source.");
                        return 2;
                    }

                    ConnectionStringStore.Save(builder.ConnectionString);
                    Console.WriteLine("Connection string saved securely for the current Windows user.");
                    return 0;
                }

                case "current":
                {
                    if (args.Length != 1)
                    {
                        Console.Error.WriteLine("Usage: cmis connection current");
                        return 2;
                    }

                    var value = ConnectionStringStore.Load();
                    if (value is null)
                    {
                        Console.WriteLine("No connection string is configured. Run: cmis connection set");
                        return 1;
                    }

                    var builder = new SqlConnectionStringBuilder(value);
                    Console.WriteLine($"Server: {builder.DataSource}");
                    Console.WriteLine($"Database: {(string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "(not specified)" : builder.InitialCatalog)}");
                    Console.WriteLine($"Authentication: {(builder.IntegratedSecurity ? "Windows" : "SQL/connection-string credentials")}");
                    Console.WriteLine("Credentials: stored securely; secret values are not displayed.");
                    return 0;
                }

                case "test":
                {
                    if (args.Length != 1)
                    {
                        Console.Error.WriteLine("Usage: cmis connection test");
                        return 2;
                    }

                    var value = ConnectionStringStore.Load();
                    if (value is null)
                    {
                        Console.Error.WriteLine("No connection string is configured. Run: cmis connection set");
                        return 1;
                    }

                    await using var connection = new SqlConnection(value);
                    try
                    {
                        await connection.OpenAsync();
                        Console.WriteLine($"Connection successful: {connection.DataSource} / {connection.Database}");
                        return 0;
                    }
                    catch (SqlException ex)
                    {
                        Console.Error.WriteLine($"SQL connection failed (error {ex.Number}). Check the server, database, network, and credentials.");
                        return 1;
                    }
                }

                case "clear":
                {
                    if (args.Length != 1)
                    {
                        Console.Error.WriteLine("Usage: cmis connection clear");
                        return 2;
                    }

                    var existed = ConnectionStringStore.Exists;
                    ConnectionStringStore.Clear();
                    Console.WriteLine(existed ? "Saved connection string removed." : "No saved connection string was found.");
                    return 0;
                }

                default:
                    Console.Error.WriteLine($"Unknown connection command '{args[0]}'.");
                    PrintConnectionHelp();
                    return 2;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Connection command failed ({ex.GetType().Name}). Check the input and local file permissions.");
            return 1;
        }
    }

    private static string? ReadHiddenInput()
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Hidden input requires an interactive terminal.");

        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                    value.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar) && value.Length < 16384)
                value.Append(key.KeyChar);
        }
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

            try
            {
                var settings = ReadSettings();
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                    settings with { ActiveModule = module },
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Active module set to {module}.");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Could not save CLI settings ({ex.GetType().Name}).");
                return 1;
            }
        }

        Console.Error.WriteLine($"Unknown module command '{args[0]}'.");
        return 2;
    }

    private static CliSettings ReadSettings()
    {
        if (!File.Exists(SettingsPath))
            return new CliSettings(null);

        return JsonSerializer.Deserialize<CliSettings>(File.ReadAllText(SettingsPath))
            ?? new CliSettings(null);
    }

    private static string? ReadActiveModule()
    {
        try
        {
            return ReadSettings().ActiveModule;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Could not read CLI settings ({ex.GetType().Name}).");
            return null;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        CMIS developer CLI

        Usage:
          cmis module set <MODULE>
          cmis module current
          cmis connection set
          cmis connection current
          cmis connection test
          cmis connection clear
          cmis generate tvp [options]
          cmis generate dal <ProcedureName> [--module <MODULE>]

        Global options:
          -h, --help       Show help

        Settings are stored per Windows user. Connection strings are protected with
        Windows DPAPI (CurrentUser); passwords are never displayed.
        """);

    private static void PrintGenerateHelp() => Console.WriteLine("""
        Generate commands:
          cmis generate tvp [options]
          cmis generate dal <ProcedureName> [--module <MODULE>]

        The TVP generator can be used independently. The DAL generator is not implemented yet.
        """);

    private static void PrintConnectionHelp() => Console.WriteLine("""
        Connection commands:
          cmis connection set       Read and save a connection string (input hidden)
          cmis connection current   Show server/database/authentication without secrets
          cmis connection test      Test the saved SQL Server connection
          cmis connection clear     Remove the saved connection string

        Encrypted file: %LOCALAPPDATA%\CMIS\CodeGen\connection.bin
        Encryption: Windows DPAPI, CurrentUser.
        General settings: %LOCALAPPDATA%\CMIS\CodeGen\settings.json
        """);

    private sealed record CliSettings(string? ActiveModule);
}
