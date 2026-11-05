# CMIS CLI

The CMIS developer tool is a .NET 10 global tool. Its executable command is **`cmis`**, so it can be invoked from any working directory after installation.

## Build and install locally (Windows)

From the backend repository root:

```powershell
dotnet pack tools/CodeGen/CodeGen.csproj --configuration Release --output .artifacts/tools
dotnet tool install --global --add-source .artifacts/tools Cmis.CodeGen
```

If PowerShell cannot find `cmis`, add the .NET global-tools directory to your user PATH and open a new terminal:

```powershell
$toolPath = Join-Path $env:USERPROFILE ".dotnet\tools"
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (($userPath -split ";") -notcontains $toolPath) {
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$toolPath", "User")
}
```

Verify the installation:

```powershell
cmis --help
cmis module current
cmis connection current
```

## Update and uninstall

After building a newer package:

```powershell
dotnet pack tools/CodeGen/CodeGen.csproj --configuration Release --output .artifacts/tools
dotnet tool update --global --add-source .artifacts/tools Cmis.CodeGen
```

To uninstall:

```powershell
dotnet tool uninstall --global Cmis.CodeGen
```

## PowerShell tab completion

The repository includes a native PowerShell argument completer for `cmis`. It provides command, subcommand, option, module, stored-procedure and TVP suggestions. PowerShell handles cycling through suggestions with repeated Tab presses.

Load it in the current session from the repository root:

```powershell
. ./tools/CodeGen/PowerShell/CmisCompletion.ps1
```

To load it automatically in every PowerShell session, add the following to your profile. Run this from the repository root so the path resolves correctly:

```powershell
$completionScript = (Resolve-Path ./tools/CodeGen/PowerShell/CmisCompletion.ps1).Path
Add-Content -Path $PROFILE -Value ". '$completionScript'"
```

### Stored-procedure suggestions

Tab completion is local-only. Pressing Tab never opens a SQL connection. Stored-procedure and TVP names are read from a local cache when the completion script is loaded, and partial text matches anywhere in the schema-qualified name.

Refresh the cache using the saved connection configured with `cmis connection set`:

```powershell
cmis sync
```

Or refresh and reload suggestions in the current PowerShell session in one step:

```powershell
Update-CmisCompletionCache
```

The cache contains both stored procedures and user-defined table types and is saved at:

```text
%LOCALAPPDATA%\CMIS\CodeGen\completions.json
```

The refresh command queries objects visible to the configured SQL login. Before each DAL or TVP generation, CodeGen checks SQL Server's `modify_date` for stored procedures and table types; if any object is newer than the cache's database-check timestamp, it automatically refreshes the cache in the background. The cache is replaced only after both metadata queries succeed; if SQL Server is unavailable or permissions are insufficient, the previous cache remains intact. No additional PowerShell SQL module is required.

Examples:

```powershell
cmis <Tab>
cmis generate <Tab>
cmis generate dal Save<Tab>
cmis generate dal Fitup --module <Tab>
```

## Module context

The active module is stored per Windows user in:

```text
%LOCALAPPDATA%\CMIS\CodeGen\settings.json
```

It contains non-secret CLI settings only. It does not create or modify tracked project files.

```powershell
cmis module set PIPE
cmis module current
```

An explicit `--module` on a generation command is an invocation-only override; it does not change the saved default.

## SQL Server connection

Configure the connection interactively. Input is hidden and the value is not echoed to the terminal:

```powershell
cmis connection set
cmis connection current
cmis connection test
cmis connection clear
```

- `set`: prompts for a SQL Server connection string without displaying typed characters.
- `current`: shows server, database and authentication mode; it never prints the connection string, user name or password.
- `test`: opens a real SQL Server connection and reports success or a non-secret SQL error number.
- `clear`: deletes the saved connection.

The encrypted connection is stored separately at:

```text
%LOCALAPPDATA%\CMIS\CodeGen\connection.bin
```

Encryption uses Windows DPAPI with `DataProtectionScope.CurrentUser`. Only the same Windows user profile on the same Windows installation can decrypt it. The connection string is not written to `settings.json`.

For unattended use, the TVP generator still supports `CMIS_CODEGEN_CONNECTION_STRING` or `--connection-string`; be aware that command-line arguments and environment variables may be exposed to other processes or shell history. The saved DPAPI value is preferred for interactive use.

## Generators

### TVP generator

The TVP generator remains independently usable:

```powershell
cmis generate tvp
cmis generate tvp --schema PIPE
cmis generate tvp QCPIPE.FitupItems
```

Pass a TVP as a positional `Schema.Type` argument (for example, `cmis generate tvp PIPE.Linestype`). A supplied name may be a full name or any partial text; matching names are read from the local cache and displayed for selection. Omitting the type displays the cached list. `--schema` filters unqualified searches. PowerShell Tab completion is also available before running the command (`cmis generate tvp <Tab>`). Passing `--type` uses the same cache-based partial-name matching and selection. Each TVP is generated into its own file named `<Schema>_<Type>.g.cs` under the module's `Models/` directory. For example, `PIPE_LineItems.g.cs`. `--output` can specify a file for one selected TVP or a directory when generating multiple TVPs. The default namespace is `CMIS.<MODULE>.Models` when a module is known. Override the output or namespace with `--output` or `--namespace`. When input or output is redirected and the type is omitted, the generator retains its batch behavior and generates all matching user-defined table types.

Connection resolution order is: explicit `--connection-string`, `CMIS_CODEGEN_CONNECTION_STRING`, then the value saved by `cmis connection set`.

Generated DTO properties preserve SQL column names using `[JsonPropertyName]`. Each class includes `ToDataTable(IEnumerable<T>)`. Unsupported SQL types fail explicitly rather than silently generating incorrect contracts.

### DAL generator

The DAL generator reads procedure parameters and TVP column metadata from SQL Server and emits a procedure-specific Request/Response contract plus a method in the module's partial DAL class (`PipeDal`, `QcpipeDal`, …). A supplied procedure name may be a full name or partial text (for example, `cmis generate dal pipe.savelin`); matching names are read from the local cache and displayed for selection. Omitting the name displays the cached list. PowerShell Tab completion is also available before running the command (`cmis generate dal <Tab>`).

```powershell
cmis generate dal
cmis generate dal SaveFitup
cmis generate dal SaveFitup --module QCPIPE
cmis generate dal PIPE.SaveLineList --output CMIS.PIPE/Data/SaveLineList.g.cs
```

DAL generation writes the procedure implementation under `Data/`, request/response contracts under `Models/`, and one reusable TVP row model per SQL table type under `Models/`. TVP model names use the SQL schema and type name, so different procedures that use the same TVP share one C# model. It also emits the module DAL partial class (`Data/PipeDal.g.cs`, `Data/QcpipeDal.g.cs`, etc.) with its `DAL` dependency and constructor; the procedure files extend that class. Generated methods convert each TVP row collection with `Utils.ModelToDataTable(request.Items, "SCHEMA.TypeName")`. The shared static utility in `CMIS.CORE/Db/Utils.cs` creates DataTable columns from the generated row contract, using property names, CLR types and nullability, then copies the rows. The SQL TVP type name is still assigned to `SqlParameter.TypeName` from SQL metadata. The standalone TVP generator remains independently callable. SQL Server does not expose reliable result-set schemas through `sys.parameters`; result-set DTOs therefore require an explicit contract and are not guessed.

## Command overview

```text
cmis module set <MODULE>
cmis module current

cmis connection set
cmis connection current
cmis connection test
cmis connection clear
cmis sync

cmis generate tvp [<Schema.Type>] [options]
cmis generate dal [<ProcedureName>] [--module <MODULE>]
```

There is no `cmis g` alias; use the explicit `cmis generate` command.


### Name filtering

The procedure and TVP name arguments support wildcard-based filtering:

- `*pipe.saveline*`: contains `pipe.saveline`
- `pipe.*`: starts with `pipe.`
- `*List`: ends with `List`

Without wildcards, partial names use contains matching, while an exact full name is selected directly.
