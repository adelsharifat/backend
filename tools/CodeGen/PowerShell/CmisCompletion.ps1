# Fast, local-only native argument completion for the CMIS CLI.
# Dot-source this file from the current user's PowerShell profile.

if (-not (Get-Command Register-ArgumentCompleter -ErrorAction SilentlyContinue)) {
    Write-Warning "This PowerShell version does not support native argument completers."
    return
}

$script:CmisCompletionCommands = @('module', 'connection', 'generate', '--help', '-h')
$script:CmisModules = @('DCC', 'CIVIL', 'QCCV', 'PIPE', 'QCPIPE')
$script:CmisCompletionCachePath = Join-Path $env:LOCALAPPDATA 'CMIS\CodeGen\completions.json'
$script:CmisStoredProcedures = @()
$script:CmisTableTypes = @()

# Load once at dot-source time. The completer itself never accesses SQL Server,
# imports modules, or performs file I/O while Tab is pressed.
if (Test-Path -LiteralPath $script:CmisCompletionCachePath -PathType Leaf) {
    try {
        $cache = Get-Content -LiteralPath $script:CmisCompletionCachePath -Raw -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop

        if ($cache.SchemaVersion -in @(1, 2) -and $null -ne $cache.Procedures) {
            $script:CmisStoredProcedures = @(
                $cache.Procedures |
                    ForEach-Object { [string]$_ } |
                    Where-Object { $_ -match '^[^.\s]+\.[^.\s]+$' } |
                    Sort-Object -Unique
            )
        }

        if ($cache.SchemaVersion -ge 2 -and $null -ne $cache.TableTypes) {
            $script:CmisTableTypes = @(
                $cache.TableTypes |
                    ForEach-Object { [string]$_ } |
                    Where-Object { $_ -match '^[^.\s]+\.[^.\s]+$' } |
                    Sort-Object -Unique
            )
        }
    }
    catch {
        $script:CmisStoredProcedures = @()
        $script:CmisTableTypes = @()
    }
}

function Update-CmisCompletionCache {
    [CmdletBinding()]
    param(
        [Parameter()]
        [string]$ConnectionString = $env:CMIS_CODEGEN_CONNECTION_STRING
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "Provide -ConnectionString or set CMIS_CODEGEN_CONNECTION_STRING before refreshing the cache."
    }
    if (-not (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
        throw "The optional SqlServer PowerShell module is required. Install it with: Install-Module SqlServer -Scope CurrentUser"
    }

    $procedureQuery = @'
SELECT s.name + N'.' + p.name AS ObjectName
FROM sys.procedures AS p
JOIN sys.schemas AS s ON s.schema_id = p.schema_id
ORDER BY s.name, p.name;
'@

    $tableTypeQuery = @'
SELECT s.name + N'.' + tt.name AS ObjectName
FROM sys.table_types AS tt
JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
WHERE tt.is_user_defined = 1
ORDER BY s.name, tt.name;
'@

    # SQL runs only when the user explicitly invokes this refresh function.
    $procedures = @(
        Invoke-Sqlcmd -ConnectionString $ConnectionString -Query $procedureQuery -QueryTimeout 15 -ConnectionTimeout 5 -ErrorAction Stop |
            ForEach-Object { [string]$_.ObjectName } |
            Where-Object { $_ -match '^[^.\s]+\.[^.\s]+$' } |
            Sort-Object -Unique
    )

    $tableTypes = @(
        Invoke-Sqlcmd -ConnectionString $ConnectionString -Query $tableTypeQuery -QueryTimeout 15 -ConnectionTimeout 5 -ErrorAction Stop |
            ForEach-Object { [string]$_.ObjectName } |
            Where-Object { $_ -match '^[^.\s]+\.[^.\s]+$' } |
            Sort-Object -Unique
    )

    $directory = Split-Path -Parent $script:CmisCompletionCachePath
    New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null

    $cache = [ordered]@{
        SchemaVersion = 2
        UpdatedAtUtc  = [DateTime]::UtcNow.ToString('o')
        Procedures    = @($procedures)
        TableTypes    = @($tableTypes)
    }

    $temporaryPath = "$($script:CmisCompletionCachePath).tmp"
    try {
        $cache | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $temporaryPath -Encoding UTF8 -ErrorAction Stop
        Move-Item -LiteralPath $temporaryPath -Destination $script:CmisCompletionCachePath -Force -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }

    $script:CmisStoredProcedures = @($procedures)
    $script:CmisTableTypes = @($tableTypes)
    Write-Host "CMIS completion cache updated: $($procedures.Count) stored procedures and $($tableTypes.Count) TVP types."
}

Register-ArgumentCompleter -Native -CommandName cmis -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)

    $tokens = @($commandAst.CommandElements | ForEach-Object { $_.ToString() })
    if ($tokens.Count -gt 0) { $tokens = $tokens | Select-Object -Skip 1 }
    $current = $wordToComplete
    $previous = if ($tokens.Count -gt 0) { $tokens[-1] } else { '' }

    $candidates = @()
    if ($previous -eq '--module' -or ($tokens.Count -eq 2 -and $tokens[0] -eq 'module' -and $tokens[1] -eq 'set')) {
        $candidates = $script:CmisModules
    }
    elseif ($tokens.Count -eq 0) {
        $candidates = $script:CmisCompletionCommands
    }
    elseif ($tokens.Count -eq 1 -and $tokens[0] -eq 'module') {
        $candidates = @('set', 'current')
    }
    elseif ($tokens.Count -eq 1 -and $tokens[0] -eq 'connection') {
        $candidates = @('set', 'current', 'test', 'clear')
    }
    elseif ($tokens.Count -eq 1 -and $tokens[0] -eq 'generate') {
        $candidates = @('tvp', 'dal')
    }
    elseif ($tokens.Count -ge 2 -and $tokens[0] -eq 'generate' -and $tokens[1] -eq 'tvp') {
        if ($current.StartsWith('-')) {
            $candidates = @('--schema', '--type', '--namespace', '--output', '--connection-string')
        }
        else {
            $candidates = $script:CmisTableTypes
        }
    }
    elseif ($tokens.Count -ge 2 -and $tokens[0] -eq 'generate' -and $tokens[1] -eq 'dal') {
        if ($current.StartsWith('-')) {
            $candidates = @('--module', '--schema', '--output', '--connection-string')
        }
        else {
            $candidates = $script:CmisStoredProcedures
        }
    }

    $candidates |
        Where-Object { $_ -like "$current*" } |
        Sort-Object -Unique |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
}
