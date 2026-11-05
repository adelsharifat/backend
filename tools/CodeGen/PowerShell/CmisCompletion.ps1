# PowerShell native argument completion for the CMIS CLI.
# Dot-source this file from the current user's PowerShell profile.
if (-not (Get-Command Register-ArgumentCompleter -ErrorAction SilentlyContinue)) {
    Write-Warning "This PowerShell version does not support native argument completers."
    return
}

$script:CmisCompletionCommands = @('module', 'connection', 'generate', '--help', '-h')
$script:CmisModules = @('DCC', 'CIVIL', 'QCCV', 'PIPE', 'QCPIPE')

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
        $candidates = @('--schema', '--type', '--namespace', '--output', '--connection-string')
    }
    elseif ($tokens.Count -ge 2 -and $tokens[0] -eq 'generate' -and $tokens[1] -eq 'dal') {
        if ($current.StartsWith('-')) {
            $candidates = @('--module', '--output', '--connection-string')
        }
        elseif ($env:CMIS_CODEGEN_CONNECTION_STRING -and
                (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
            # Procedure names are queried only when the optional SqlServer
            # module and an explicit connection-string environment variable exist.
            $prefix = $current.Replace("'", "''")
            $query = @"
SELECT s.name + N'.' + p.name AS ProcedureName
FROM sys.procedures AS p
JOIN sys.schemas AS s ON s.schema_id = p.schema_id
WHERE s.name + N'.' + p.name LIKE N'$prefix%'
   OR p.name LIKE N'$prefix%'
ORDER BY s.name, p.name;
"@
            try {
                $candidates = @(
                    Invoke-Sqlcmd -ConnectionString $env:CMIS_CODEGEN_CONNECTION_STRING -Query $query -ErrorAction Stop |
                        ForEach-Object { [string]$_.ProcedureName }
                )
            }
            catch {
                $candidates = @()
            }
        }
    }

    $candidates |
        Where-Object { $_ -like "$current*" } |
        Sort-Object -Unique |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
}
