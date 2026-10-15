[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseDirectory, [switch]$RequireTrustedSignature)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $release -PathType Container)) { throw "Release directory is missing: $release" }
$main = @(Get-ChildItem -LiteralPath $release -Filter 'ClipDat_*_x64.msix' -File)
$library = @(Get-ChildItem -LiteralPath $release -Filter 'ClipDat-Library-v*.zip' -File)
if ($main.Count -ne 1 -or $library.Count -ne 1) { throw 'Expected one main MSIX and one optional Library archive.' }

Copy-Item -LiteralPath (Join-Path $root 'installer\Start-Setup.ps1') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $root 'Clippr\Assets\ClipprTray.ico') -Destination $release -Force
$version = [regex]::Match($main[0].Name, '^ClipDat_(\d+\.\d+\.\d+)\.\d+_x64\.msix$').Groups[1].Value
if (-not $version) { throw "Unexpected main package name: $($main[0].Name)" }
$bundle = Join-Path $release "ClipDat-Setup-v$version.zip"
$names = @('Dependencies','Start-Setup.ps1','ClipprTray.ico','Install-ClipDat.ps1',
    'Repair-ClipDat.ps1','Uninstall-ClipDat.ps1','README.md',$main[0].Name,$library[0].Name)
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $release $name))) { throw "Setup payload is missing: $name" }
}
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Force }
Compress-Archive -Path ($names | ForEach-Object { Join-Path $release $_ }) -DestinationPath $bundle
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -ReleaseDirectory $release
& (Join-Path $PSScriptRoot 'Test-Installer.ps1') -ReleaseDirectory $release -RequireTrustedSignature:$RequireTrustedSignature
Write-Host "Setup bundle: $bundle"
