# PB35: the third-party notices the installer carries, taken from the packages the app was
# actually published with rather than written by hand, so a package upgrade cannot leave a stale
# license behind. Reads the app's obj\project.assets.json, and for every runtime package copies
# its own license and notice files into <out>\<package>\, then writes <out>\README.txt listing
# each package, its version and its declared license. <out> sits inside the publish folder, whose
# .dll files say which build-target packages shipped.
#
#   powershell -NoProfile -File build\collect-notices.ps1 -Out artifacts\publish\licenses

param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Assets = ''
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 has no $PSScriptRoot while it binds parameters, so the default is here.
if (-not $Assets) { $Assets = Join-Path $PSScriptRoot '..\src\Revelei.App\obj\project.assets.json' }
$resolved = Get-Content -Raw -Encoding UTF8 $Assets | ConvertFrom-Json
$root = $resolved.packageFolders.PSObject.Properties.Name | Select-Object -First 1
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null

$lines = @(
    'Revelei third-party notices',
    '',
    'Revelei ships the packages below. Each folder here holds the license and notice files the',
    'package itself carries. The models the installer carries, in the models folder, list their',
    'licenses in the app''s About panel (GFPGAN v1.4 and RMBG-1.4 carry non-commercial terms).',
    ''
)

# Only what ships: a package with a runtime or native file for win-x64, so build-time packages
# (ILLink) and another platform's natives (SkiaSharp's macOS assets) are not credited as shipped.
$target = $resolved.targets.PSObject.Properties | Where-Object { $_.Name -like '*/win-x64' } | Select-Object -First 1
$shipped = @{}
foreach ($entry in $target.Value.PSObject.Properties) {
    $assets = @($entry.Value.runtime, $entry.Value.native) | Where-Object { $_ } |
        ForEach-Object { $_.PSObject.Properties.Name } | Where-Object { $_ -notlike '*_._' }
    if ($assets) { $shipped[$entry.Name] = $true }
}

# And a package that copies its binaries through build targets instead (Microsoft.AI.DirectML puts
# DirectML.dll beside the exe that way): one of its .dll files is in the published folder.
$published = @{}
Get-ChildItem (Split-Path $Out -Parent) -File -Filter *.dll | ForEach-Object { $published[$_.Name] = $true }

foreach ($library in $resolved.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $folder = Join-Path $root $library.Value.path
    $copied = Get-ChildItem $folder -Recurse -File -Filter *.dll | Where-Object { $published[$_.Name] } | Select-Object -First 1
    if (-not $shipped[$library.Name] -and -not $copied) { continue }
    $name, $version = $library.Name -split '/'
    $nuspec = Get-ChildItem $folder -Filter *.nuspec | Select-Object -First 1
    $metadata = ([xml](Get-Content -Raw -Encoding UTF8 $nuspec.FullName)).package.metadata
    $license = if ($metadata.license.type -eq 'expression') { $metadata.license.'#text' }
               elseif ($metadata.license) { "its own terms, see $name\$($metadata.license.'#text')" }
               else { $metadata.licenseUrl }
    $files = Get-ChildItem $folder -File | Where-Object { $_.Name -match '(?i)licen[cs]e|notice' }
    if ($files) {
        $target = Join-Path $Out $name
        New-Item -ItemType Directory -Force $target | Out-Null
        $files | Copy-Item -Destination $target
    }
    $lines += '{0} {1}: {2}' -f $name, $version, $license
}

$lines | Set-Content -Encoding utf8 (Join-Path $Out 'README.txt')
Write-Host "notices for $(@($resolved.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }).Count) packages -> $Out"
