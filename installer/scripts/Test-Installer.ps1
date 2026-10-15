[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [switch]$RequireTrustedSignature,
    [switch]$InstallForCurrentUser,
    [switch]$IUnderstandCurrentUserTest
)

$ErrorActionPreference = 'Stop'
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $release -Filter 'ClipDat_*_x64.msix' -File)
if ($packages.Count -ne 1) { throw "Expected exactly one ClipDat x64 MSIX in $release; found $($packages.Count)." }
$package = $packages[0]
$dependency = Join-Path $release 'Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix'
if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw 'Windows App SDK runtime dependency is missing.' }
foreach ($script in @('Install-ClipDat.ps1','Repair-ClipDat.ps1','Uninstall-ClipDat.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $release $script) -PathType Leaf)) { throw "Release helper is missing: $script" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    foreach ($expected in @('AppxManifest.xml','Clippr.exe','ClipprCapture.dll')) {
        if ($expected -cnotin $names) { throw "MSIX is missing: $expected" }
    }
    $manifestEntry = $zip.GetEntry('AppxManifest.xml')
    $reader = New-Object IO.StreamReader($manifestEntry.Open())
    try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
    $ns.AddNamespace('f','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity',$ns)
    if (-not $identity -or $identity.Name -ne 'ce842948-d72b-4d57-ae7b-474f481f442e' -or
        $package.Name -notmatch [regex]::Escape($identity.Version.ToString())) { throw 'MSIX identity or version does not match the expected ClipDat package.' }
} finally { $zip.Dispose() }

$sumsPath = Join-Path $release 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) { throw 'SHA256SUMS.txt is missing.' }
$verified = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $sumsPath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $sumsPath) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw "Invalid checksum line: $line" }
        $expectedHash = $matches[1]
        $relative = $matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Unsafe checksum path.' }
        $candidate = Join-Path $release $relative
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Checksum target is missing: $relative" }
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ine $expectedHash) { throw "Checksum mismatch: $relative" }
        [void]$verified.Add($relative.Replace('\','/'))
    }
}
foreach ($required in @($package.Name,'Dependencies/x64/Microsoft.WindowsAppRuntime.2.msix')) {
    if (-not $verified.Contains($required)) { throw "Checksum manifest does not cover: $required" }
}
$bundles = @(Get-ChildItem -LiteralPath $release -Filter 'ClipDat-Setup-v*.zip' -File)
if ($bundles.Count -gt 1) { throw 'Multiple setup bundles were found.' }
if ($bundles.Count -eq 1) {
    if (-not $verified.Contains($bundles[0].Name)) { throw "Checksum manifest does not cover: $($bundles[0].Name)" }
    $bundle = [IO.Compression.ZipFile]::OpenRead($bundles[0].FullName)
    try {
        $entries = @($bundle.Entries | ForEach-Object FullName)
        foreach ($required in @('Start-Setup.ps1','Install-ClipDat.ps1','ClipprTray.ico',
            'Dependencies/x64/Microsoft.WindowsAppRuntime.2.msix',$package.Name)) {
            if ($required -cnotin $entries) { throw "Setup bundle is missing: $required" }
        }
    } finally { $bundle.Dispose() }
}
$libraryArchives = @(Get-ChildItem -LiteralPath $release -Filter 'ClipDat-Library-v*.zip' -File)
if ($libraryArchives.Count -gt 1) { throw 'Multiple Library archives were found.' }
if ($libraryArchives.Count -eq 1 -and -not $verified.Contains($libraryArchives[0].Name)) {
    throw "Checksum manifest does not cover: $($libraryArchives[0].Name)"
}
if ($RequireTrustedSignature) {
    $signature = Get-AuthenticodeSignature -LiteralPath $package.FullName
    if ($signature.Status -ne 'Valid') { throw "MSIX signature is not trusted: $($signature.Status)" }
}
$installScript = Join-Path $release 'Install-ClipDat.ps1'
$express = & $installScript -Mode Express -ValidateConfigurationOnly | ConvertFrom-Json
$minimal = & $installScript -Mode Minimal -ValidateConfigurationOnly | ConvertFrom-Json
if (-not $express.organizeClipsByGame -or -not $express.screenshotsEnabled -or
    $minimal.organizeClipsByGame -or $minimal.screenshotsEnabled -or
    $minimal.notificationStyle -ne 'None') { throw 'First-run preset handoff did not match the installer modes.' }
Write-Host "Package validation passed: $($package.Name)"

if (-not $InstallForCurrentUser) { return }
if (-not $IUnderstandCurrentUserTest) { throw 'Current-user install test requires -IUnderstandCurrentUserTest.' }
$existing = Get-AppxPackage -Name 'ce842948-d72b-4d57-ae7b-474f481f442e'
if ($existing) { throw 'ClipDat is already installed for this user. Use a clean test account or VM; no installation was changed.' }
try {
    Add-AppxPackage -Path $package.FullName -DependencyPath $dependency
    $installed = Get-AppxPackage -Name 'ce842948-d72b-4d57-ae7b-474f481f442e'
    if (-not $installed -or $installed.Status -ne 'Ok') { throw 'Windows did not register ClipDat correctly.' }
    if (-not (Test-Path -LiteralPath (Join-Path $installed.InstallLocation 'Clippr.exe') -PathType Leaf)) { throw 'Installed executable is missing.' }
    Write-Host 'Current-user installation and registration passed.'
} finally {
    $installed = Get-AppxPackage -Name 'ce842948-d72b-4d57-ae7b-474f481f442e'
    if ($installed) { Remove-AppxPackage -Package $installed.PackageFullName }
}
if (Get-AppxPackage -Name 'ce842948-d72b-4d57-ae7b-474f481f442e') { throw 'Uninstall did not remove the package.' }
Write-Host 'Current-user uninstallation passed. User recordings were not touched.'
