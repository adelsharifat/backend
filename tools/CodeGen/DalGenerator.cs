using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

internal static class DalGenerator
{
    private const string ParameterQuery = """
        SELECT
            p.parameter_id, p.name, p.is_output, t.name AS type_name,
            SCHEMA_NAME(t.schema_id) AS type_schema,
            p.max_length, p.precision, p.scale,
            ip.PARAMETER_MODE, t.is_table_type
        FROM sys.parameters p
        JOIN sys.types t ON t.user_type_id = p.user_type_id
        LEFT JOIN INFORMATION_SCHEMA.PARAMETERS ip
          ON ip.SPECIFIC_SCHEMA = OBJECT_SCHEMA_NAME(p.object_id)
         AND ip.SPECIFIC_NAME = OBJECT_NAME(p.object_id)
         AND ip.ORDINAL_POSITION = p.parameter_id
        WHERE p.object_id = OBJECT_ID(@procedure)
          AND p.parameter_id > 0
        ORDER BY p.parameter_id;
        """;

    private const string TvpColumnsQuery = """
        SELECT c.name, st.name, c.is_nullable, c.column_id
        FROM sys.table_types tt
        JOIN sys.columns c ON c.object_id = tt.type_table_object_id
        JOIN sys.types st ON st.user_type_id = c.user_type_id
        WHERE tt.is_user_defined = 1
          AND SCHEMA_NAME(tt.schema_id) = @schema
          AND tt.name = @type
        ORDER BY c.column_id;
        """;

    public static async Task<int> RunAsync(string[] args, string? savedConnectionString, string? activeModule)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? procedure = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-h" or "--help")
            {
                Help();
                return 0;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (procedure is null) procedure = args[i];
                else { Console.Error.WriteLine($"Unexpected argument: {args[i]}"); return 2; }
                continue;
            }
            var key = args[i][2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Missing value for --{key}.");
                return 2;
            }
            options[key] = args[++i];
        }

        string? Option(string name) => options.GetValueOrDefault(name);
        if (string.IsNullOrWhiteSpace(procedure))
        {
            Console.Error.WriteLine("Usage: cmis generate dal <ProcedureName> [--module <MODULE>] [--schema <SCHEMA>] [--output <FILE>]");
            return 2;
        }

        var module = (Option("module") ?? activeModule)?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(module))
        {
            Console.Error.WriteLine("Set an active module first ('cmis module set PIPE') or pass --module.");
            return 2;
        }
        if (!Regex.IsMatch(module, "^[A-Z][A-Z0-9_]*$"))
        {
            Console.Error.WriteLine("Invalid module name.");
            return 2;
        }

        var (schema, procName) = SplitProcedure(procedure, Option("schema") ?? module);
        if (!Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$") ||
            !Regex.IsMatch(procName, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            Console.Error.WriteLine("Procedure name must be [schema.]ProcedureName.");
            return 2;
        }

        var cs = Option("connection-string")
            ?? Environment.GetEnvironmentVariable("CMIS_CODEGEN_CONNECTION_STRING")
            ?? savedConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.Error.WriteLine("Run 'cmis connection set', set CMIS_CODEGEN_CONNECTION_STRING, or pass --connection-string.");
            return 2;
        }

        try
        {
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            var fullName = $"{schema}.{procName}";
            var parameters = await ReadParameters(connection, fullName);
            if (parameters.Count == 0)
            {
                Console.Error.WriteLine($"Stored procedure '{fullName}' was not found, has no parameters, or its metadata is not visible.");
                return 1;
            }

            foreach (var parameter in parameters.Where(p => p.IsTvp))
                parameter.Columns = await ReadTvpColumns(connection, parameter.TypeSchema!, parameter.TypeName);

            var output = Path.GetFullPath(Option("output") ?? DefaultOutput(module, procName));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, Generate(module, schema, procName, parameters), new UTF8Encoding(false));
            Console.WriteLine($"Generated DAL contract for {fullName}: {output}");
            Console.WriteLine($"  {parameters.Count} parameter(s), {parameters.Count(p => p.IsTvp)} TVP(s)");
            Console.WriteLine("  Result sets are not inferred: SQL Server procedure metadata does not expose a reliable result contract.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DAL generation failed ({ex.GetType().Name}). Check procedure permissions and SQL metadata.");
            return 1;
        }
    }

    private static async Task<List<Parameter>> ReadParameters(SqlConnection connection, string procedure)
    {
        await using var command = new SqlCommand(ParameterQuery, connection);
        command.Parameters.Add(new SqlParameter("@procedure", SqlDbType.NVarChar, 517) { Value = procedure });
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<Parameter>();
        while (await reader.ReadAsync())
        {
            var mode = reader.IsDBNull(8) ? (reader.GetBoolean(2) ? "OUT" : "IN") : reader.GetString(8);
            var type = reader.GetString(3);
            var isTvp = reader.GetBoolean(9);
            result.Add(new Parameter(
                reader.GetString(1).TrimStart('@'), type,
                reader.IsDBNull(4) ? "sys" : reader.GetString(4),
                reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7),
                mode.Equals("INOUT", StringComparison.OrdinalIgnoreCase) || mode.Equals("IN/OUT", StringComparison.OrdinalIgnoreCase),
                reader.GetBoolean(2), isTvp));
        }
        return result;
    }

    private static async Task<List<Column>> ReadTvpColumns(SqlConnection connection, string schema, string type)
    {
        await using var command = new SqlCommand(TvpColumnsQuery, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@type", type);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<Column>();
        while (await reader.ReadAsync())
            columns.Add(new Column(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        if (columns.Count == 0)
            throw new InvalidOperationException($"TVP metadata is not visible for {schema}.{type}.");
        return columns;
    }

    private static string Generate(string module, string schema, string procedure, List<Parameter> parameters)
    {
        var className = Identifier(procedure);
        var ns = $"CMIS.{Identifier(module)}.Data";
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Generated by CMIS CodeGen. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Data;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Data.SqlClient;");
        sb.AppendLine("using CMIS.Core.Db;");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();

        foreach (var tvp in parameters.Where(p => p.IsTvp))
        {
            var rowName = Identifier(tvp.TypeSchema + "_" + tvp.TypeName);
            sb.AppendLine($"public sealed record {rowName}(");
            sb.AppendLine(string.Join("," + Environment.NewLine, tvp.Columns.Select((c, i) =>
                $"    {Clr(c.SqlType, c.Nullable)} {Identifier(c.Name)}{(i == tvp.Columns.Count - 1 ? "" : "")}")));
            sb.AppendLine(");");
            sb.AppendLine();
        }

        var inputs = parameters.Where(p => !p.IsOutput || p.IsInputOutput).ToList();
        sb.AppendLine($"public sealed class {className}Request");
        sb.AppendLine("{");
        foreach (var p in inputs)
            sb.AppendLine($"    public {ParameterClr(p)} {Identifier(p.Name)} {{ get; init; }}{(p.IsTvp ? " = Array.Empty<" + Identifier(p.TypeSchema + "_" + p.TypeName) + ">();" : "")}");
        sb.AppendLine("}");
        sb.AppendLine();

        var outputs = parameters.Where(p => p.IsOutput).ToList();
        sb.AppendLine($"public sealed class {className}Response");
        sb.AppendLine("{");
        foreach (var p in outputs)
            sb.AppendLine($"    public {ParameterClr(p)} {Identifier(p.Name)} {{ get; init; }}");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine($"public static class {className}DalExtensions");
        sb.AppendLine("{");
        sb.AppendLine($"    public static async Task<{className}Response> {className}Async(this DAL dal, {className}Request request, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(dal);");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(request);");
        sb.AppendLine("        var parameters = new List<SqlParameter>();");
        foreach (var p in parameters)
        {
            var property = "request." + Identifier(p.Name);
            if (p.IsTvp)
            {
                var row = Identifier(p.TypeSchema + "_" + p.TypeName);
                sb.AppendLine($"        var table_{Identifier(p.Name)} = new DataTable({Literal(p.TypeSchema + "." + p.TypeName)});");
                foreach (var c in p.Columns)
                    sb.AppendLine($"        table_{Identifier(p.Name)}.Columns.Add(new DataColumn({Literal(c.Name)}, typeof({ClrBase(c.SqlType)})) {{ AllowDBNull = {(c.Nullable ? "true" : "false")} }});");
                sb.AppendLine($"        foreach (var item in {property})");
                sb.AppendLine("        {");
                sb.AppendLine("            var row = table_" + Identifier(p.Name) + ".NewRow();");
                foreach (var c in p.Columns)
                    sb.AppendLine($"            row[{Literal(c.Name)}] = (object?)item.{Identifier(c.Name)} ?? DBNull.Value;");
                sb.AppendLine("            table_" + Identifier(p.Name) + ".Rows.Add(row);");
                sb.AppendLine("        }");
                sb.AppendLine($"        parameters.Add(new SqlParameter({Literal("@" + p.Name)}, SqlDbType.Structured) {{ TypeName = {Literal(p.TypeSchema + "." + p.TypeName)}, Value = table_{Identifier(p.Name)} }});");
            }
            else
            {
                var direction = p.IsInputOutput ? "InputOutput" : p.IsOutput ? "Output" : "Input";
                sb.AppendLine($"        parameters.Add(new SqlParameter({Literal("@" + p.Name)}, SqlDbType.{SqlDbTypeName(p.SqlType)}) {{ Direction = ParameterDirection.{direction}, Value = {(p.IsOutput && !p.IsInputOutput ? "DBNull.Value" : "(object?)" + property + " ?? DBNull.Value")}{SizePrecisionScale(p)} }});");
            }
        }
        sb.AppendLine($"        await dal.ExecuteAsync({Literal(schema + "." + procedure)}, CommandType.StoredProcedure, cancellationToken, parameters.ToArray());");
        sb.AppendLine($"        return new {className}Response");
        sb.AppendLine("        {");
        foreach (var p in outputs)
            sb.AppendLine($"            {Identifier(p.Name)} = parameters.First(p => p.ParameterName == {Literal("@" + p.Name)}).Value is DBNull ? default : ({ParameterClr(p)})parameters.First(p => p.ParameterName == {Literal("@" + p.Name)}).Value,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string SizePrecisionScale(Parameter p) =>
        (p.MaxLength > 0 && IsStringOrBinary(p.SqlType) ? $", Size = {(p.SqlType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) || p.SqlType.Equals("nchar", StringComparison.OrdinalIgnoreCase) ? p.MaxLength / 2 : p.MaxLength)}" : "") +
        (p.Precision > 0 && IsNumeric(p.SqlType) ? $", Precision = {p.Precision}, Scale = {p.Scale}" : "");

    private static bool IsStringOrBinary(string t) => new[] { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" }.Contains(t, StringComparer.OrdinalIgnoreCase);
    private static bool IsNumeric(string t) => new[] { "decimal", "numeric" }.Contains(t, StringComparer.OrdinalIgnoreCase);
    private static string ParameterClr(Parameter p) => p.IsTvp
        ? $"IReadOnlyList<{Identifier(p.TypeSchema + "_" + p.TypeName)}>"
        : Clr(p.SqlType, true);
    private static string Clr(string sql, bool nullable) => ClrBase(sql) + (nullable && ClrBase(sql) is not "string" and not "byte[]" ? "?" : nullable ? "?" : "");
    private static string ClrBase(string sql) => sql.ToLowerInvariant() switch
    {
        "bit" => "bool", "tinyint" => "byte", "smallint" => "short", "int" => "int", "bigint" => "long",
        "real" => "float", "float" => "double", "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
        "uniqueidentifier" => "Guid", "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
        "datetimeoffset" => "DateTimeOffset", "time" => "TimeSpan", "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "byte[]",
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" => "string",
        _ => throw new NotSupportedException($"Unsupported SQL type '{sql}'.")
    };
    private static string SqlDbTypeName(string sql) => sql.ToLowerInvariant() switch
    {
        "bit" => "Bit", "tinyint" => "TinyInt", "smallint" => "SmallInt", "int" => "Int", "bigint" => "BigInt",
        "real" => "Real", "float" => "Float", "decimal" or "numeric" => "Decimal", "money" => "Money", "smallmoney" => "SmallMoney",
        "uniqueidentifier" => "UniqueIdentifier", "date" => "Date", "datetime" => "DateTime", "datetime2" => "DateTime2",
        "smalldatetime" => "SmallDateTime", "datetimeoffset" => "DateTimeOffset", "time" => "Time",
        "binary" => "Binary", "varbinary" or "image" => "VarBinary", "timestamp" or "rowversion" => "Timestamp",
        "char" => "Char", "varchar" or "text" => "VarChar", "nchar" => "NChar", "nvarchar" or "ntext" => "NVarChar", "xml" => "Xml",
        _ => throw new NotSupportedException($"Unsupported SQL type '{sql}'.")
    };
    private static (string Schema, string Name) SplitProcedure(string name, string defaultSchema)
    {
        var parts = name.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (defaultSchema, parts[0]);
    }
    private static string DefaultOutput(string module, string procedure) =>
        Path.GetFileName(Environment.CurrentDirectory).Equals("CMIS." + module, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine("Data", Identifier(procedure) + ".g.cs")
            : Path.Combine("CMIS." + module, "Data", Identifier(procedure) + ".g.cs");
    private static string Identifier(string value)
    {
        var id = Regex.Replace(value, "[^A-Za-z0-9_]", "_");
        if (id.Length == 0) id = "Generated";
        if (char.IsDigit(id[0])) id = "_" + id;
        if (CSharpKeywords.Contains(id)) id = "@" + id;
        return id;
    }
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
        "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
        "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
        "namespace","new","null","object","operator","out","override","params","private","protected","public","readonly",
        "ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw",
        "true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while"
    };
    private static string Literal(string value) => """ + value.Replace("\\", "\\\\").Replace(""", "\\"") + """;
    private static void Help() => Console.WriteLine("""
        Usage: cmis generate dal <ProcedureName> [--module PIPE] [--schema PIPE]
               [--output CMIS.PIPE/Data/SaveLineList.g.cs]
        ProcedureName may be Procedure or schema.Procedure.
        Module defaults to the saved active module. Schema defaults to the module name.
        """);

    private sealed class Parameter(string name, string sqlType, string typeSchema, short maxLength, byte precision, byte scale, bool inputOutput, bool isOutput, bool isTvp)
    {
        public string Name { get; } = name;
        public string SqlType { get; } = sqlType;
        public string TypeSchema { get; } = typeSchema;
        public short MaxLength { get; } = maxLength;
        public byte Precision { get; } = precision;
        public byte Scale { get; } = scale;
        public bool IsInputOutput { get; } = inputOutput;
        public bool IsOutput { get; } = isOutput;
        public bool IsTvp { get; } = isTvp;
        public List<Column> Columns { get; set; } = [];
    }
    private sealed record Column(string Name, string SqlType, bool Nullable);
}
