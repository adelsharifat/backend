# Cmis.TvpGen

A .NET 10 command-line tool that reads SQL Server user-defined table types and generates strongly typed C# DTOs plus DataTable converters for ADO.NET TVPs. SQL Server is the source of truth; no hand-written DTOs are required.

## Run from source

Requires the .NET 10 SDK and access to the target database.

```powershell
$env:CMIS_TVPGEN_CONNECTION_STRING = "Server=...;Database=CMIS;Integrated Security=True;TrustServerCertificate=True"
dotnet run --project tools/Cmis.TvpGen -- --schema PIPE --output CMIS.Generated/PipeTvpContracts.g.cs
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

Initial version supports common scalar SQL types: bit, integer types, decimal/numeric/money, uniqueidentifier, date/time types, binary types, strings and XML. Unsupported types fail explicitly instead of silently creating an incorrect contract. Nullability comes from the TVP metadata and is not the same as nullability of columns in target tables. This tool generates contracts/mappers only; it does not rewrite controllers or stored procedures automatically.
