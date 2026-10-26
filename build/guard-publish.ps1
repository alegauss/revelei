<#
.SYNOPSIS
    Refuse the publish when something is running from the folder it is about to overwrite.

.DESCRIPTION
    After claude-tray's guard (T266 there). `dotnet publish` writes Revelei.exe and its natives
    into artifacts\publish. If a process is running from that folder - the app started from the
    last publish to try it - the SDK fails inside its bundler with an UnauthorizedAccessException
    and a stack trace about MSBuild, for a state that is not a bug. This names it instead.

    It matches on the PATH, not on the process name: a Revelei running from bin\Debug, or
    installed under %LocalAppData%\Programs, is not in the way.

    It does NOT stop the process: that is the user's running app, and a build script that kills
    applications is worse than one that asks.

.PARAMETER PublishDir
    The folder the publish will write. Defaults to the one build-installer.cmd uses.
#>
[CmdletBinding()]
param([string]$PublishDir)

$ErrorActionPreference = 'Stop'

# Resolved here and not as a param default: Windows PowerShell 5.1 has no $PSScriptRoot while it
# binds parameters.
if (-not $PublishDir) {
    $PublishDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish'
}

# Nothing published yet is nothing to be in the way of.
if (-not (Test-Path -LiteralPath $PublishDir)) { exit 0 }

$full = (Resolve-Path -LiteralPath $PublishDir).Path
$blocking = @()

foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
    # A process whose path cannot be read is one this session has no rights over: it cannot be
    # holding a file this checkout is about to write.
    $path = try { $p.Path } catch { $null }
    if (-not $path) { continue }
    if ($path.StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase)) {
        $blocking += [pscustomobject]@{ Id = $p.Id; Path = $path }
    }
}

if ($blocking.Count -eq 0) { exit 0 }

Write-Host ''
Write-Host '*** The publish would overwrite a file that is running. ***' -ForegroundColor Red
foreach ($b in $blocking) { Write-Host ("    pid {0}  {1}" -f $b.Id, $b.Path) }
Write-Host ''
Write-Host 'Quit it and run this again. Nothing was built, and nothing was stopped for you.'
Write-Host ''
exit 1
