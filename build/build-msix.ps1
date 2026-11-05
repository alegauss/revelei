# build-msix.ps1 - Packs artifacts\publish as the Microsoft Store's MSIX (PB90), after claude-tray's.
#
# Called by build-msix.cmd after publish.cmd. Steps:
#   1) version: <Version> from Directory.Build.props as X.Y.Z.0 (the Store requires the fourth part 0)
#   2) layout in artifacts\msix\layout: the whole publish folder (exe, natives, licenses, staged
#      models), the manifest with the version, and the logos from build\msix\Assets (npm run
#      msix-assets in site\)
#   3) makepri, so the shell finds the logo's targetsize and altform-unplated variants, then
#      makeappx pack -> artifacts\Revelei.msix, unsigned (the Store signs it on upload)
#   4) with -Register: installs the layout on this machine (Add-AppxPackage -Register), the only way
#      to try the package without a certificate; needs Developer Mode.
param(
    [switch]$Register
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

# --- 1) Version ----------------------------------------------------------------------------------
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "<Version> in Directory.Build.props is '$version', not X.Y.Z" }
$version = "$version.0"

# --- 2) Layout -----------------------------------------------------------------------------------
$publish = Join-Path $artifacts 'publish'
if (-not (Test-Path (Join-Path $publish 'Revelei.exe'))) { throw "no published app in $publish (run build\build-msix.cmd without -NoPublish)" }

$layout = Join-Path $artifacts 'msix\layout'
if (Test-Path $layout) {
    # A registered layout is the installed app: re-registering over a running one fails half-way.
    if (Get-Process Revelei -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$layout*" }) {
        throw "Revelei is running from $layout; close it first"
    }
    Remove-Item $layout -Recurse -Force
}
New-Item -ItemType Directory -Force $layout | Out-Null

# What the installer leaves out (installer.iss [Files] Excludes) stays out of the package too.
robocopy $publish $layout /E /NFL /NDL /NJH /NJS /NP /XF *.pdb DirectML.Debug.dll *.lib *.xml | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copying the publish into the layout failed (robocopy $LASTEXITCODE)" }
Copy-Item (Join-Path $PSScriptRoot 'msix\Assets') (Join-Path $layout 'Assets') -Recurse

$manifest = Get-Content (Join-Path $PSScriptRoot 'msix\AppxManifest.xml') -Raw -Encoding UTF8
$manifest = $manifest.Replace('{VERSION}', $version)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))
Write-Host "layout: $layout  (version $version)"

# --- 3) makepri and makeappx ---------------------------------------------------------------------
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeappx = Get-ChildItem $kits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\x64\*' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found: install the Windows SDK' }

$makepri = Join-Path $makeappx.DirectoryName 'makepri.exe'
$priconfig = Join-Path $artifacts 'msix\priconfig.xml'
& $makepri createconfig /cf $priconfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig exited with $LASTEXITCODE" }
& $makepri new /pr $layout /cf $priconfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new exited with $LASTEXITCODE" }

$msix = Join-Path $artifacts 'Revelei.msix'
& $makeappx.FullName pack /o /d $layout /p $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx exited with $LASTEXITCODE" }
Write-Host ("package: {0}  ({1:N0} MB)" -f $msix, ((Get-Item $msix).Length / 1MB))

# --- 4) Local install ----------------------------------------------------------------------------
if ($Register) {
    Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml')
    Write-Host 'installed from the layout: open Revelei from Start'
}
