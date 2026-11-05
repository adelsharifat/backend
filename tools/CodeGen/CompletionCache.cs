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

    public static async Task<(int Procedures, int TableTypes)> SyncAsync(string connectionString)
    {
        var procedures = new List<string>();
        var tableTypes = new List<string>();

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
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
            2,
            DateTimeOffset.UtcNow,
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

    private sealed record CacheDocument(int SchemaVersion, DateTimeOffset UpdatedAtUtc, string[] Procedures, string[] TableTypes);
}
