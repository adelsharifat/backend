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
$toolPath = Join-Path $env:USERPROFILE ".dotnet\\tools"
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

## Module context

The active module is stored per Windows user in:

```text
%LOCALAPPDATA%\\CMIS\\CodeGen\\settings.json
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
%LOCALAPPDATA%\\CMIS\\CodeGen\\connection.bin
```

Encryption uses Windows DPAPI with `DataProtectionScope.CurrentUser`. Only the same Windows user profile on the same Windows installation can decrypt it. The connection string is not written to `settings.json`.

For unattended use, the TVP generator still supports `CMIS_CODEGEN_CONNECTION_STRING` or `--connection-string`; be aware that command-line arguments and environment variables may be exposed to other processes or shell history. The saved DPAPI value is preferred for interactive use.

## Generators

### TVP generator

The TVP generator remains independently usable:

```powershell
cmis generate tvp --schema PIPE --output CMIS.Generated/PipeTvpContracts.g.cs
cmis generate tvp --schema QCPIPE --type FitupItems
```

Options: `--schema`, `--type`, `--namespace`, `--output`, `--connection-string`. Omit schema/type to generate all user-defined table types.

Connection resolution order is: explicit `--connection-string`, `CMIS_CODEGEN_CONNECTION_STRING`, then the value saved by `cmis connection set`.

Generated DTO properties preserve SQL column names using `[JsonPropertyName]`. Each class includes `ToDataTable(IEnumerable<T>)`. Unsupported SQL types fail explicitly rather than silently generating incorrect contracts.

### DAL generator

The command shape is reserved, but DAL generation is not implemented yet:

```powershell
cmis generate dal SaveFitup
cmis generate dal SaveFitup --module QCPIPE
```

When implemented, the DAL generator is intended to reuse the TVP generator internally whenever the stored procedure requires TVP contracts. The TVP generator will remain independently callable for cases where only TVP models are needed.

## Command overview

```text
cmis module set <MODULE>
cmis module current

cmis connection set
cmis connection current
cmis connection test
cmis connection clear

cmis generate tvp [options]
cmis generate dal <ProcedureName> [--module <MODULE>]
```

There is no `cmis g` alias; use the explicit `cmis generate` command.
