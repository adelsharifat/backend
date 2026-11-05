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
    param()

    if (-not (Get-Command cmis -ErrorAction SilentlyContinue)) {
        throw "The cmis CLI is not installed or is not available on PATH."
    }

    & cmis sync
    if ($LASTEXITCODE -ne 0) {
        throw "cmis sync failed (exit code $LASTEXITCODE). The existing completion cache was not changed."
    }

    try {
        $cache = Get-Content -LiteralPath $script:CmisCompletionCachePath -Raw -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
        if ($cache.SchemaVersion -lt 2) {
            throw "Unsupported completion cache version."
        }

        $script:CmisStoredProcedures = @(
            $cache.Procedures |
                ForEach-Object { [string]$_ } |
                Where-Object { $_ -match '^[^\.\s]+\.[^\.\s]+$' } |
                Sort-Object -Unique
        )
        $script:CmisTableTypes = @(
            $cache.TableTypes |
                ForEach-Object { [string]$_ } |
                Where-Object { $_ -match '^[^\.\s]+\.[^\.\s]+$' } |
                Sort-Object -Unique
        )
    }
    catch {
        throw "The cache was synced, but could not be loaded into this PowerShell session: $($_.Exception.Message)"
    }

    Write-Host "CMIS completion cache loaded: $($script:CmisStoredProcedures.Count) stored procedures and $($script:CmisTableTypes.Count) TVP types."
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
        Where-Object { $_ -like "*$current*" } |
        Sort-Object -Unique |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
}
