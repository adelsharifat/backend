using System.Text.Json;
using Microsoft.Data.SqlClient;

internal static class CompletionCache
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CMIS", "CodeGen", "completions.json");

    private const string ProceduresQuery = """
        SELECT s.name + N'.' + p.name
        FROM sys.procedures AS p
        INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
        ORDER BY s.name, p.name;
        """;

    private const string TableTypesQuery = """
        SELECT s.name + N'.' + tt.name
        FROM sys.table_types AS tt
        INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
        WHERE tt.is_user_defined = 1
        ORDER BY s.name, tt.name;
        """;

    private const string LatestObjectModifyDateQuery = """
        SELECT MAX(objects.modify_date)
        FROM
        (
            SELECT o.modify_date
            FROM sys.procedures AS p
            INNER JOIN sys.objects AS o ON o.object_id = p.object_id

            UNION ALL

            SELECT o.modify_date
            FROM sys.table_types AS tt
            INNER JOIN sys.objects AS o ON o.object_id = tt.type_table_object_id
            WHERE tt.is_user_defined = 1
        ) AS objects;
        """;

    public static async Task<(int Procedures, int TableTypes)> SyncAsync(string connectionString)
    {
        var procedures = new List<string>();
        var tableTypes = new List<string>();
        DateTime databaseCheckedAt;

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            // Use SQL Server's own clock because sys.objects.modify_date uses server-local time.
            await using (var command = new SqlCommand("SELECT GETDATE();", connection))
                databaseCheckedAt = (DateTime)(await command.ExecuteScalarAsync())!;

            await using (var command = new SqlCommand(ProceduresQuery, connection))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    procedures.Add(reader.GetString(0));

            await using (var command = new SqlCommand(TableTypesQuery, connection))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    tableTypes.Add(reader.GetString(0));
        }

        var cache = new CacheDocument(
            3,
            DateTimeOffset.UtcNow,
            databaseCheckedAt,
            procedures.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            tableTypes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());

        var directory = Path.GetDirectoryName(CachePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = CachePath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return (cache.Procedures.Length, cache.TableTypes.Length);
    }

    public static async Task EnsureFreshAsync(string connectionString)
    {
        var cache = await ReadCacheAsync();
        if (cache is null || cache.SchemaVersion < 3)
        {
            Console.WriteLine("Completion cache is missing or outdated. Syncing SQL metadata...");
            await SyncAsync(connectionString);
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(LatestObjectModifyDateQuery, connection);
        var value = await command.ExecuteScalarAsync();
        var latestModifyDate = value is DBNull or null ? (DateTime?)null : (DateTime)value;

        if (latestModifyDate.HasValue && latestModifyDate.Value > cache.DatabaseCheckedAt)
        {
            Console.WriteLine("SQL procedure/TVP metadata has changed. Refreshing completion cache...");
            await SyncAsync(connectionString);
        }
    }

    public static Task<string?> SelectStoredProcedureAsync(string? partialName, string? schemaFilter = null) =>
        SelectAsync(ReadCacheAsync, partialName, schemaFilter, false, "stored procedure", "stored procedures");

    public static Task<string?> SelectTableTypeAsync(string? partialName, string? schemaFilter = null) =>
        SelectAsync(ReadCacheAsync, partialName, schemaFilter, true, "TVP table type", "user-defined table types");

    private static async Task<string?> SelectAsync(
        Func<Task<CacheDocument?>> readCache,
        string? partialName,
        string? schemaFilter,
        bool tableTypes,
        string prompt,
        string collectionLabel)
    {
        var cache = await readCache();
        if (cache is null)
        {
            Console.Error.WriteLine("The local SQL metadata cache is missing or invalid. Run 'cmis sync' first.");
            return null;
        }

        var source = tableTypes ? cache.TableTypes : cache.Procedures;
        var hasQualifiedQuery = !string.IsNullOrWhiteSpace(partialName) && partialName.Contains('.');
        var candidates = source
            .Where(name => hasQualifiedQuery || string.IsNullOrWhiteSpace(schemaFilter) ||
                name.StartsWith(schemaFilter + ".", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var query = partialName?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var exact = candidates.FirstOrDefault(name => name.Equals(query, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            candidates = candidates
                .Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (candidates.Length == 0)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(query)
                ? $"No cached {collectionLabel} found."
                : $"No {collectionLabel} matching '{query}' were found in the local cache.");
            return null;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            if (candidates.Length == 1)
                return candidates[0];

            Console.Error.WriteLine(
                $"'{query}' matches {candidates.Length} {collectionLabel}. Pass a more specific name or run the command in an interactive terminal.");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine(string.IsNullOrWhiteSpace(query)
            ? $"Available {collectionLabel} from local cache:"
            : $"Matching {collectionLabel} for '{query}':");

        for (var i = 0; i < candidates.Length; i++)
            Console.WriteLine($"  {i + 1,3}. {candidates[i]}");

        Console.WriteLine();
        Console.Write($"Select {prompt} (number, full name, or Enter to cancel): ");
        var selection = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(selection))
            return null;

        if (int.TryParse(selection, out var selectedNumber))
        {
            if (selectedNumber >= 1 && selectedNumber <= candidates.Length)
                return candidates[selectedNumber - 1];

            Console.Error.WriteLine($"Selection must be between 1 and {candidates.Length}.");
            return null;
        }

        var selectedName = candidates.FirstOrDefault(name =>
            name.Equals(selection, StringComparison.OrdinalIgnoreCase));
        if (selectedName is not null)
            return selectedName;

        Console.Error.WriteLine("Enter one of the listed numbers or the complete schema-qualified name.");
        return null;
    }

    private static async Task<CacheDocument?> ReadCacheAsync()
    {
        if (!File.Exists(CachePath))
            return null;

        try
        {
            var cache = JsonSerializer.Deserialize<CacheDocument>(await File.ReadAllTextAsync(CachePath));
            return cache is { SchemaVersion: >= 2, Procedures: not null, TableTypes: not null }
                ? cache
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record CacheDocument(
        int SchemaVersion,
        DateTimeOffset UpdatedAtUtc,
        DateTime DatabaseCheckedAt,
        string[] Procedures,
        string[] TableTypes);
}
