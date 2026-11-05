using System.Data;
using Microsoft.Data.SqlClient;

internal static class SqlObjectAutocomplete
{
    private const int MaxSuggestions = 8;

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
        SelectAsync(connectionString, schemaFilter, TableTypesQuery, "table type", "user-defined table types");

    private static async Task<string?> SelectAsync(
        string connectionString,
        string? schemaFilter,
        string query,
        string objectLabel,
        string collectionLabel)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine($"Interactive {objectLabel} selection requires a terminal.");
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

        var prompt = objectLabel switch
        {
            "stored procedure" => "Stored procedure: ",
            "table type" => "TVP table type: ",
            _ => $"{objectLabel}: "
        };
        const string hint = "Type to filter | Up/Down navigate | Enter select | Esc cancel";
        var visibleRows = Math.Clamp(Console.WindowHeight - 3, 1, MaxSuggestions);
        var width = Math.Max(1, Console.WindowWidth - 1);
        var originalCursorVisible = Console.CursorVisible;
        var filter = string.Empty;
        string? selectedObject = null;
        var cancelled = false;

        // Use the terminal's alternate screen buffer for the interactive UI. This isolates
        // the picker from previous command output and avoids cursor-row drift when the
        // terminal scrolls while the picker is being redrawn.
        try
        {
            Console.Write("\u001b[?1049h\u001b[2J\u001b[H");
            Console.CursorVisible = false;

            var selectedIndex = 0;

            List<string> Filtered()
            {
                if (filter.Length == 0)
                    return objects;

                return objects
                    .Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(name => name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var matches = Filtered();
            Render(matches, selectedIndex);

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape ||
                    (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                {
                    cancelled = true;
                    break;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (matches.Count == 0)
                    {
                        Console.Beep();
                        continue;
                    }

                    selectedObject = matches[selectedIndex];
                    break;
                }

                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (matches.Count > 0)
                        selectedIndex = (selectedIndex - 1 + matches.Count) % matches.Count;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (matches.Count > 0)
                        selectedIndex = (selectedIndex + 1) % matches.Count;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (filter.Length > 0)
                        filter = filter[..^1];
                    selectedIndex = 0;
                }
                else if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                {
                    filter += key.KeyChar;
                    selectedIndex = 0;
                }
                else
                {
                    continue;
                }

                matches = Filtered();
                if (selectedIndex >= matches.Count)
                    selectedIndex = 0;

                Render(matches, selectedIndex);
            }
        }
        finally
        {
            Console.Write("\u001b[?1049l");
            Console.CursorVisible = originalCursorVisible;
        }

        if (selectedObject is not null)
            Console.WriteLine(prompt + selectedObject);
        else if (cancelled)
            Console.WriteLine("Selection cancelled.");

        return selectedObject;

        void Render(IReadOnlyList<string> items, int activeIndex)
        {
            var startIndex = Math.Clamp(activeIndex - visibleRows + 1, 0, Math.Max(0, items.Count - visibleRows));

            WriteRow(0, prompt + filter);
            for (var i = 0; i < visibleRows; i++)
            {
                var itemIndex = startIndex + i;
                var line = itemIndex < items.Count
                    ? (itemIndex == activeIndex ? " > " : "   ") + items[itemIndex]
                    : string.Empty;
                WriteRow(i + 1, line);
            }

            var visibleCount = Math.Min(visibleRows, Math.Max(0, items.Count - startIndex));
            var status = items.Count == 0
                ? $"   No matching {collectionLabel}."
                : $"   Showing {startIndex + 1}-{startIndex + visibleCount} of {items.Count}";
            WriteRow(visibleRows + 1, status);
            WriteRow(visibleRows + 2, hint);

            var cursorColumn = Math.Clamp(prompt.Length + filter.Length, 0, width - 1);
            Console.Write($"\u001b[1;{cursorColumn + 1}H");
        }

        void WriteRow(int row, string value)
        {
            if (value.Length > width)
                value = value[..width];

            Console.Write($"\u001b[{row + 1};1H\u001b[2K");
            Console.Write(value);
        }
    }
}
