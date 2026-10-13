param(
    [switch] $RemoveSettings
)

$ErrorActionPreference = 'Stop'
$identityName = 'ce842948-d72b-4d57-ae7b-474f481f442e'
$applicationId = 'App'
$displayName = 'ClipDat'
$backupSettings = Join-Path $env:LOCALAPPDATA 'ClipDat\InstallerBackup\settings.json'
$installed = Get-AppxPackage -Name $identityName | Sort-Object Version -Descending | Select-Object -First 1
if (-not $installed) {
    Write-Host 'ClipDat is not installed for the current Windows user.'
    return
}

if ($RemoveSettings) {
    $confirmation = Read-Host 'This removes ClipDat settings only. Clips and screenshots are never removed. Type REMOVE to continue'
    if ($confirmation -cne 'REMOVE') { throw 'Settings removal was not confirmed; nothing was uninstalled.' }
}

# Ask ClipDat to close normally and refuse to force-kill it if it does not cooperate.
$processes = Get-Process -Name Clippr -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($installed.InstallLocation, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
}
foreach ($process in $processes) {
    if ($process.MainWindowHandle -ne 0) { [void]$process.CloseMainWindow() }
}
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $remaining = @($processes | Where-Object { -not $_.HasExited })
    if ($remaining.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
}
$remaining = @($processes | Where-Object { -not $_.HasExited })
if ($remaining.Count -gt 0) { throw 'ClipDat is still running. Close it normally, then run the uninstaller again.' }

$settingsPath = Join-Path $env:LOCALAPPDATA "Packages\$($installed.PackageFamilyName)\LocalCache\Local\Clippr\settings.json"
if (-not $RemoveSettings -and (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupSettings) | Out-Null
    Copy-Item -LiteralPath $settingsPath -Destination $backupSettings -Force
}

$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktop "$displayName.lnk"
if (Test-Path -LiteralPath $shortcutPath) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $expectedArguments = "shell:AppsFolder\$($installed.PackageFamilyName)!$applicationId"
    if ($shortcut.TargetPath -ieq (Join-Path $env:WINDIR 'explorer.exe') -and $shortcut.Arguments -ieq $expectedArguments) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}

Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction Stop

if ($RemoveSettings) {
    Remove-Item -LiteralPath $backupSettings -Force -ErrorAction SilentlyContinue
    $ownedSettingsDirectory = Join-Path $env:LOCALAPPDATA "Packages\$($installed.PackageFamilyName)\LocalCache\Local\Clippr"
    if (Test-Path -LiteralPath $ownedSettingsDirectory -PathType Container) {
        Remove-Item -LiteralPath $ownedSettingsDirectory -Recurse -Force
    }
    # This user-owned app data directory is reserved for installer settings backup.
    $backupRoot = Split-Path -Parent $backupSettings
    if ((Test-Path -LiteralPath $backupRoot -PathType Container) -and -not (Get-ChildItem -LiteralPath $backupRoot -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $backupRoot -Force
    }
}

Write-Host 'ClipDat has been removed. User clips and screenshots were left untouched.'
if (-not $RemoveSettings) { Write-Host 'Settings were preserved for a later reinstall.' }
