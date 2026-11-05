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
        const int suggestionRows = MaxSuggestions + 2;

        // Always redraw relative to the live cursor. Absolute rows become stale when the
        // terminal scrolls while the suggestion list is being rendered.
        Console.Write(prompt);
        Console.WriteLine();
        Console.SetCursorPosition(0, Console.CursorTop - 1);

        var filter = string.Empty;
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
                Finish("Selection cancelled.");
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                if (matches.Count == 0)
                {
                    Console.Beep();
                    continue;
                }

                var selected = matches[selectedIndex];
                Finish(prompt + selected);
                return selected;
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

        void Render(IReadOnlyList<string> items, int activeIndex)
        {
            var width = Math.Max(2, Console.WindowWidth - 1);
            var startIndex = Math.Clamp(activeIndex - MaxSuggestions + 1, 0, Math.Max(0, items.Count - MaxSuggestions));

            WriteLine(prompt + filter, width);
            for (var i = 0; i < MaxSuggestions; i++)
            {
                var itemIndex = startIndex + i;
                var line = itemIndex < items.Count
                    ? (itemIndex == activeIndex ? " > " : "   ") + items[itemIndex]
                    : string.Empty;
                WriteLine(line, width);
            }

            var visibleCount = Math.Min(MaxSuggestions, Math.Max(0, items.Count - startIndex));
            var status = items.Count == 0
                ? $"   No matching {collectionLabel}."
                : $"   Showing {startIndex + 1}-{startIndex + visibleCount} of {items.Count}";
            WriteLine(status, width);
            WriteLine(hint, width);

            // Return to the prompt using the cursor's current row, not a saved row that
            // may have moved when the terminal scrolled during the writes above.
            Console.SetCursorPosition(
                Math.Clamp(prompt.Length + filter.Length, 0, width - 1),
                Math.Max(0, Console.CursorTop - suggestionRows - 1));
        }

        void Finish(string message)
        {
            var width = Math.Max(2, Console.WindowWidth - 1);
            Console.SetCursorPosition(0, Console.CursorTop);
            WriteLine(message, width);
            for (var i = 0; i < suggestionRows; i++)
                WriteLine(string.Empty, width);
        }

        static void WriteLine(string value, int width)
        {
            if (value.Length >= width)
                value = value[..(width - 1)];
            Console.Write(value.PadRight(width - 1));
            Console.WriteLine();
        }
    }
}
