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

    public static async Task<string?> SelectStoredProcedureAsync(string connectionString, string? schemaFilter = null)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Interactive procedure selection requires a terminal. Pass the procedure name as an argument.");
            return null;
        }

        var procedures = new List<string>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(StoredProceduresQuery, connection);
            command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value =
                string.IsNullOrWhiteSpace(schemaFilter) ? DBNull.Value : schemaFilter;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                procedures.Add(reader.GetString(0) + "." + reader.GetString(1));
        }

        if (procedures.Count == 0)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(schemaFilter)
                ? "No visible stored procedures were found in this database."
                : $"No visible stored procedures were found in schema '{schemaFilter}'.");
            return null;
        }

        const string prompt = "Stored procedure: ";
        const string hint = "Type to filter | Up/Down navigate | Enter select | Esc cancel";
        var promptTop = Console.CursorTop;
        Console.Write(prompt);
        var promptLeft = Console.CursorLeft;
        var suggestionsTop = Console.CursorTop;
        var filter = string.Empty;
        var selectedIndex = 0;

        List<string> Filtered()
        {
            if (filter.Length == 0)
                return procedures;

            return procedures
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
                ClearSuggestions();
                Console.SetCursorPosition(0, suggestionsTop);
                Console.WriteLine("Selection cancelled.");
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
                Console.SetCursorPosition(promptLeft, promptTop);
                Console.Write(prompt + selected);
                Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - Console.CursorLeft - 1)));
                ClearSuggestions();
                Console.SetCursorPosition(0, suggestionsTop);
                Console.WriteLine();
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

            Console.SetCursorPosition(promptLeft, promptTop);
            Console.Write(prompt + filter);
            Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - Console.CursorLeft - 1)));
            Console.SetCursorPosition(promptLeft + prompt.Length + filter.Length, promptTop);
            Render(matches, selectedIndex);
        }

        void Render(IReadOnlyList<string> items, int activeIndex)
        {
            var width = Math.Max(20, Console.WindowWidth - 1);
            Console.SetCursorPosition(0, suggestionsTop);
            for (var i = 0; i < MaxSuggestions; i++)
            {
                var line = i < items.Count ? (i == activeIndex ? " > " : "   ") + items[i] : string.Empty;
                if (line.Length >= width)
                    line = line[..(width - 1)];
                Console.Write(line.PadRight(width - 1));
                Console.WriteLine();
            }

            var status = items.Count == 0
                ? "   No matching stored procedures."
                : $"   {Math.Min(items.Count, MaxSuggestions)} of {items.Count} suggestion(s)";
            if (status.Length >= width)
                status = status[..(width - 1)];
            Console.Write(status.PadRight(width - 1));
            Console.WriteLine();
            Console.Write(hint);
            Console.Write(new string(' ', Math.Max(0, width - hint.Length - 1)));
            Console.SetCursorPosition(promptLeft + prompt.Length + filter.Length, promptTop);
        }

        void ClearSuggestions()
        {
            var width = Math.Max(20, Console.WindowWidth - 1);
            Console.SetCursorPosition(0, suggestionsTop);
            for (var i = 0; i < MaxSuggestions + 2; i++)
            {
                Console.Write(new string(' ', width - 1));
                Console.WriteLine();
            }
        }
    }
}
