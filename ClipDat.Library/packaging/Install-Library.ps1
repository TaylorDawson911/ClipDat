param(
    [switch]$DesktopShortcut,
    [switch]$SkipBuild,
    [string]$PayloadPath
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\ClipDat.Library.csproj'))
$buildOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'))
$publish = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\x64\Release\selfcontained-publish'))
if (-not $PayloadPath -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'payload\ClipDat.Library.exe') -PathType Leaf)) {
    $PayloadPath = Join-Path $PSScriptRoot 'payload'
}
if ($PayloadPath) {
    $publish = (Resolve-Path -LiteralPath $PayloadPath).Path
    $SkipBuild = $true
}
$programs = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$install = [IO.Path]::GetFullPath((Join-Path $programs 'ClipDat Library'))
if (-not $install.StartsWith($programs + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Invalid install directory: $install"
}

if (-not $SkipBuild) {
    & dotnet publish $project -c Release -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --self-contained true -o $publish -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'Clip Library publish failed.' }
}
$executable = Join-Path $publish 'ClipDat.Library.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw "Publish output is missing: $executable" }
foreach ($resource in @('App.xbf', 'LibraryWindow.xbf', 'ClipDat.Library.pri')) {
    if ($PayloadPath) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $resource) -PathType Leaf)) {
            throw "Library package is missing: $resource"
        }
        continue
    }
    $source = Join-Path $buildOutput $resource
    if (-not (Test-Path -LiteralPath $source)) { throw "Build output is missing: $source" }
    Copy-Item -LiteralPath $source -Destination $publish -Force
}

Get-Process -Name 'ClipDat.Library' -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(10000)) { throw 'Close Clip Library before installing an update.' }
}

if (Test-Path -LiteralPath $install) {
    if ((Get-Item -LiteralPath $install -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Library install directory is a linked folder; refusing replacement.' }
    Remove-Item -LiteralPath $install -Recurse -Force
}
New-Item -ItemType Directory -Path $install -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $install -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Library.ps1') -Destination $install -Force
$installedExe = Join-Path $install 'ClipDat.Library.exe'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startMenu 'ClipDat Library.lnk'))
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $install
$shortcut.IconLocation = "$installedExe,0"
$shortcut.Save()
if ($DesktopShortcut) {
    $desktopLink = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'ClipDat Library.lnk'))
    $desktopLink.TargetPath = $installedExe
    $desktopLink.WorkingDirectory = $install
    $desktopLink.IconLocation = "$installedExe,0"
    $desktopLink.Save()
}
New-Item -Path 'HKCU:\Software\ClipDat\Library' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\ClipDat\Library' -Name Executable -Value $installedExe
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDat.Library'
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'ClipDat Library'
Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '0.1.0'
Set-ItemProperty -Path $uninstallKey -Name Publisher -Value 'ClipDat'
Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $install
Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value $installedExe
Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $install 'Uninstall-Library.ps1')`""
Write-Host "Installed ClipDat Library at $install"
