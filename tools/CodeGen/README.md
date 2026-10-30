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

Verify the installation from any directory:

```powershell
cmis --help
cmis module current
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

The active module is stored in the current Windows user's Local AppData, not in the repository. It therefore does not create or modify tracked project files.

```powershell
cmis module set PIPE
cmis module current
```

An explicit `--module` value on a generation command is intended to override the active module for that invocation only; it must not change the saved default.

## Current generator

The existing TVP generator is available through the hierarchical command:

```powershell
$env:CMIS_CODEGEN_CONNECTION_STRING = "Server=...;Database=CMIS;Integrated Security=True;TrustServerCertificate=True"
cmis generate tvp --schema PIPE --output CMIS.Generated/PipeTvpContracts.g.cs
```

Options: `--schema`, `--type`, `--namespace`, `--output`, `--connection-string`. Omit schema/type to generate all user-defined table types.

Generated DTO properties preserve SQL column names using `[JsonPropertyName]`. Each class includes `ToDataTable(IEnumerable<T>)`. Unsupported SQL types fail explicitly rather than silently generating incorrect contracts.

## Planned command shape

The DAL generator will use the same command tree and module context:

```powershell
cmis generate dal SaveFitup
cmis generate dal SaveFitup --module QCPIPE
```

The explicit module is an invocation-only override. The DAL generator is not included in this CLI foundation change yet.
