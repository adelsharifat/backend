using System.Data;
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

        var module = (Option("module") ?? activeModule ?? DetectModuleFromCurrentDirectory())?.Trim().ToUpperInvariant();
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
            var dataDirectory = Path.GetDirectoryName(output)!;
            var moduleDirectory = Directory.GetParent(dataDirectory)?.FullName
                ?? throw new InvalidOperationException("Could not resolve the module project directory.");
            var modelsDirectory = Path.Combine(moduleDirectory, "Models");
            Directory.CreateDirectory(dataDirectory);
            Directory.CreateDirectory(modelsDirectory);

            var generatedFiles = new List<string>();
            await File.WriteAllTextAsync(output, Generate(module, schema, procName, parameters), new UTF8Encoding(false));
            generatedFiles.Add(output);

            var requestPath = Path.Combine(modelsDirectory, procedureNameForFile(procName) + "Request.g.cs");
            await File.WriteAllTextAsync(requestPath, GenerateRequestModel(module, procName, parameters), new UTF8Encoding(false));
            generatedFiles.Add(requestPath);

            var responsePath = Path.Combine(modelsDirectory, procedureNameForFile(procName) + "Response.g.cs");
            await File.WriteAllTextAsync(responsePath, GenerateResponseModel(module, procName, parameters), new UTF8Encoding(false));
            generatedFiles.Add(responsePath);

            foreach (var tvp in parameters.Where(p => p.IsTvp))
            {
                var modelPath = Path.Combine(modelsDirectory, TvpRowName(tvp) + ".g.cs");
                await File.WriteAllTextAsync(modelPath, GenerateTvpModel(module, tvp), new UTF8Encoding(false));
                generatedFiles.Add(modelPath);
            }

            var dalClassPath = Path.Combine(dataDirectory, classNameForModule(module) + ".g.cs");
            await File.WriteAllTextAsync(dalClassPath, GenerateDalClass(module), new UTF8Encoding(false));
            generatedFiles.Add(dalClassPath);

            Console.WriteLine($"Generated DAL contract for {fullName}:");
            foreach (var file in generatedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {file}");
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
        var procedureName = Identifier(procedure);
        var className = classNameForModule(module);
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
        sb.AppendLine($"using CMIS.{Identifier(module)}.Models;");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public partial class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    public async Task<{procedureName}Response> {procedureName}Async({procedureName}Request request, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(request);");
        sb.AppendLine("        var parameters = new List<SqlParameter>();");
        foreach (var p in parameters)
        {
            var property = "request." + Identifier(p.Name);
            if (p.IsTvp)
            {
                var tableVariable = "table_" + Regex.Replace(p.Name, "[^A-Za-z0-9_]", "_");
                sb.AppendLine($"        var {tableVariable} = Utils.ModelToDataTable({property}, {Literal(p.TypeSchema + "." + p.TypeName)});");
                sb.AppendLine($"        parameters.Add(new SqlParameter({Literal("@" + p.Name)}, SqlDbType.Structured) {{ TypeName = {Literal(p.TypeSchema + "." + p.TypeName)}, Value = {tableVariable} }});");
            }
            else
            {
                var direction = p.IsInputOutput ? "InputOutput" : p.IsOutput ? "Output" : "Input";
                sb.AppendLine($"        parameters.Add(new SqlParameter({Literal("@" + p.Name)}, SqlDbType.{SqlDbTypeName(p.SqlType)}) {{ Direction = ParameterDirection.{direction}, Value = {(p.IsOutput && !p.IsInputOutput ? "DBNull.Value" : "(object?)" + property + " ?? DBNull.Value")}{SizePrecisionScale(p)} }});");
            }
        }
        sb.AppendLine($"        await _dal.ExecuteAsync({Literal(schema + "." + procedure)}, CommandType.StoredProcedure, cancellationToken, parameters.ToArray());");
        sb.AppendLine($"        return new {procedureName}Response");
        sb.AppendLine("        {");
        foreach (var p in parameters.Where(p => p.IsOutput))
            sb.AppendLine($"            {Identifier(p.Name)} = parameters.First(p => p.ParameterName == {Literal("@" + p.Name)}).Value is DBNull ? default : ({ParameterClr(p)})parameters.First(p => p.ParameterName == {Literal("@" + p.Name)}).Value,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateRequestModel(string module, string procedure, List<Parameter> parameters)
    {
        var sb = ModelFileHeader(module);
        sb.AppendLine($"public sealed class {Identifier(procedure)}Request");
        sb.AppendLine("{");
        foreach (var p in parameters.Where(p => !p.IsOutput || p.IsInputOutput))
            sb.AppendLine($"    public {ParameterClr(p)} {Identifier(p.Name)} {{ get; init; }}{(p.IsTvp ? " = Array.Empty<" + TvpRowName(p) + ">();" : "")}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateResponseModel(string module, string procedure, List<Parameter> parameters)
    {
        var sb = ModelFileHeader(module);
        sb.AppendLine($"public sealed class {Identifier(procedure)}Response");
        sb.AppendLine("{");
        foreach (var p in parameters.Where(p => p.IsOutput))
            sb.AppendLine($"    public {ParameterClr(p)} {Identifier(p.Name)} {{ get; init; }}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTvpModel(string module, Parameter tvp)
    {
        var sb = ModelFileHeader(module);
        var rowName = TvpRowName(tvp);
        sb.AppendLine($"/// <summary>Generated from SQL table type {tvp.TypeSchema}.{tvp.TypeName}; shared by procedures using this TVP.</summary>");
        sb.AppendLine($"public sealed record {rowName}(");
        sb.AppendLine(string.Join("," + Environment.NewLine, tvp.Columns.Select(c =>
            $"    {Clr(c.SqlType, c.Nullable)} {Identifier(c.Name)}")));
        sb.AppendLine(");");
        return sb.ToString();
    }

    private static StringBuilder ModelFileHeader(string module)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Generated by CMIS CodeGen. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine($"namespace CMIS.{Identifier(module)}.Models;");
        sb.AppendLine();
        return sb;
    }

    private static string GenerateDalClass(string module)
    {
        var className = classNameForModule(module);
        return $$"""
            // <auto-generated />
            // Generated by CMIS CodeGen. Do not edit.
            #nullable enable
            using System;
            using CMIS.Core.Db;

            namespace CMIS.{{Identifier(module)}}.Data;

            /// <summary>Data access methods for the {{module}} module.</summary>
            public partial class {{className}}
            {
                private readonly DAL _dal;

                public {{className}}(DAL dal)
                {
                    _dal = dal ?? throw new ArgumentNullException(nameof(dal));
                }
            }
            """;
    }

    private static string classNameForModule(string module) =>
        Identifier(char.ToUpperInvariant(module[0]) + module[1..].ToLowerInvariant() + "Dal");

    private static string procedureNameForFile(string procedure) => Identifier(procedure);
    private static string SizePrecisionScale(Parameter p) =>
        (p.MaxLength > 0 && IsStringOrBinary(p.SqlType) ? $", Size = {(p.SqlType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) || p.SqlType.Equals("nchar", StringComparison.OrdinalIgnoreCase) ? p.MaxLength / 2 : p.MaxLength)}" : "") +
        (p.Precision > 0 && IsNumeric(p.SqlType) ? $", Precision = {p.Precision}, Scale = {p.Scale}" : "");

    private static bool IsStringOrBinary(string t) => new[] { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" }.Contains(t, StringComparer.OrdinalIgnoreCase);
    private static bool IsNumeric(string t) => new[] { "decimal", "numeric" }.Contains(t, StringComparer.OrdinalIgnoreCase);
    private static string TvpRowName(Parameter parameter) =>
        Identifier(parameter.TypeSchema + "_" + parameter.TypeName);
    private static string ParameterClr(Parameter p) => p.IsTvp
        ? $"IReadOnlyList<{TvpRowName(p)}>"
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
    private static string? DetectModuleFromCurrentDirectory()
    {
        var directoryName = Path.GetFileName(Environment.CurrentDirectory);
        return directoryName.StartsWith("CMIS.", StringComparison.OrdinalIgnoreCase)
            ? directoryName[5..]
            : null;
    }

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
    private static string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static void Help() => Console.WriteLine("""
        Usage: cmis generate dal <ProcedureName> [--module PIPE] [--schema PIPE]
               [--output CMIS.PIPE/Data/SaveLineList.g.cs]
        ProcedureName may be Procedure or schema.Procedure.
        Module defaults to the saved active module, then to a current directory named CMIS.<MODULE>. Schema defaults to the module name.
        """);

    private sealed class Parameter(string name, string sqlType, string typeSchema, short maxLength, byte precision, byte scale, bool inputOutput, bool isOutput, bool isTvp)
    {
        public string Name { get; } = name;
        public string SqlType { get; } = sqlType;
        // For scalar parameters this is the SQL scalar type name; for TVPs it is the table type name.
        public string TypeName { get; } = sqlType;
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
