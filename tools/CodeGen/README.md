# CodeGen

CodeGen is the extensible .NET 10 command-line tool for automated C# code generation in CMIS. Its first generator reads SQL Server user-defined table types and produces strongly typed C# DTOs plus DataTable converters for ADO.NET TVPs. SQL Server remains the source of truth; no hand-written DTOs are required.

## Run from source

Requires the .NET 10 SDK and access to the target database.

```powershell
$env:CMIS_CODEGEN_CONNECTION_STRING = "Server=...;Database=CMIS;Integrated Security=True;TrustServerCertificate=True"
dotnet run --project tools/CodeGen -- --schema PIPE --output CMIS.Generated/PipeTvpContracts.g.cs
```

Options: `--schema`, `--type`, `--namespace`, `--output`, `--connection-string`. Omit schema/type to generate all user-defined table types.

Generated DTO properties preserve the exact SQL column name using `[JsonPropertyName]`. Each class includes `ToDataTable(IEnumerable<T>)`, and the generated class name is based on `<schema>_<table-type>`.

Example usage:

```csharp
var table = PIPE_LinesType.ToDataTable(request.Lines);
var parameter = command.Parameters.AddWithValue("@Lines", table);
parameter.SqlDbType = SqlDbType.Structured;
parameter.TypeName = "PIPE.LinesType";
```

Initial version supports common scalar SQL types: bit, integer types, decimal/numeric/money, uniqueidentifier, date/time types, binary types, strings and XML. Unsupported types fail explicitly instead of silently creating an incorrect contract. Nullability comes from the TVP metadata and is not the same as nullability of columns in target tables. The current TVP generator produces contracts and mappers only; it does not rewrite controllers or stored procedures automatically. Future generators can be added under the CodeGen tool without renaming the overall project.
