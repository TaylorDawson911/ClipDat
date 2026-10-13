[CmdletBinding()]
param(
    [string] $PackagePath,
    [string] $RuntimePackagePath
)

$ErrorActionPreference = 'Stop'
$identityName = 'ce842948-d72b-4d57-ae7b-474f481f442e'
$installer = Join-Path $PSScriptRoot 'Install-ClipDat.ps1'
$installed = Get-AppxPackage -Name $identityName | Sort-Object Version -Descending | Select-Object -First 1
if (-not $installed) { throw 'ClipDat is not installed for this Windows user. Run Install-ClipDat.ps1 first.' }

if (-not $PackagePath) {
    $versionedPackage = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "ClipDat_$($installed.Version)_x64.msix" -File | Select-Object -First 1
    if (-not $versionedPackage) { throw "The matching ClipDat $($installed.Version) package was not found beside this repair script." }
    $PackagePath = $versionedPackage.FullName
}

$arguments = @{ PackagePath = $PackagePath }
if ($RuntimePackagePath) { $arguments.RuntimePackagePath = $RuntimePackagePath }
& $installer @arguments
Write-Host 'Repair completed by reapplying the MSIX package. User data remains in the package data folder.'
