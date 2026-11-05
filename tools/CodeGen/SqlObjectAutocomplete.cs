using System.Data;
using Microsoft.Data.SqlClient;

internal static class SqlObjectAutocomplete
{
    private const string StoredProceduresQuery = """
        SELECT s.name, p.name
        FROM sys.procedures AS p
        INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
        WHERE (@schema IS NULL OR s.name = @schema)
        ORDER BY s.name, p.name;
        """;

    private const string TableTypesQuery = """
        SELECT s.name, tt.name
        FROM sys.table_types AS tt
        INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
        WHERE tt.is_user_defined = 1
          AND (@schema IS NULL OR s.name = @schema)
        ORDER BY s.name, tt.name;
        """;

    public static Task<string?> SelectStoredProcedureAsync(string connectionString, string? schemaFilter = null) =>
        SelectAsync(connectionString, schemaFilter, StoredProceduresQuery, "stored procedure", "stored procedures");

    public static Task<string?> SelectTableTypeAsync(string connectionString, string? schemaFilter = null) =>
        SelectAsync(connectionString, schemaFilter, TableTypesQuery, "TVP table type", "user-defined table types");

    private static async Task<string?> SelectAsync(
        string connectionString,
        string? schemaFilter,
        string query,
        string prompt,
        string collectionLabel)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine($"Interactive {prompt} selection requires a terminal. Pass the name as an argument.");
            return null;
        }

        var objects = new List<string>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value =
                string.IsNullOrWhiteSpace(schemaFilter) ? DBNull.Value : schemaFilter;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                objects.Add(reader.GetString(0) + "." + reader.GetString(1));
        }

        if (objects.Count == 0)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(schemaFilter)
                ? $"No visible {collectionLabel} were found in this database."
                : $"No visible {collectionLabel} were found in schema '{schemaFilter}'.");
            return null;
        }

        Console.Write($"{prompt}: ");
        var input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input))
            return input;

        Console.WriteLine();
        Console.WriteLine($"Available {collectionLabel}:");
        for (var i = 0; i < objects.Count; i++)
            Console.WriteLine($"  {i + 1,3}. {objects[i]}");

        Console.WriteLine();
        Console.Write("Enter number or name (empty to cancel): ");
        input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input))
            return null;

        if (int.TryParse(input, out var selectedNumber))
        {
            if (selectedNumber >= 1 && selectedNumber <= objects.Count)
                return objects[selectedNumber - 1];

            Console.Error.WriteLine($"Selection must be between 1 and {objects.Count}.");
            return null;
        }

        return input;
    }
}
