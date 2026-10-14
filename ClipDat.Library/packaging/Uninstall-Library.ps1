param([switch]$RemoveLibrarySettings)

$ErrorActionPreference = 'Stop'
$programs = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$install = [IO.Path]::GetFullPath((Join-Path $programs 'ClipDat Library'))
if (-not $install.StartsWith($programs + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Invalid install directory: $install"
}
Get-Process -Name 'ClipDat.Library' -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(10000)) { throw 'Close Clip Library before uninstalling.' }
}
$startMenuLink = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ClipDat Library.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'ClipDat Library.lnk'
Remove-Item -LiteralPath $startMenuLink -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $desktopLink -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\ClipDat\Library' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDat.Library' -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
if ($RemoveLibrarySettings) {
    $settings = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ClipDat Library'))
    $localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
    if (-not $settings.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid library settings directory: $settings"
    }
    if (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings -Recurse -Force }
}
Write-Host 'Uninstalled ClipDat Library. Recordings and screenshots were preserved.'
