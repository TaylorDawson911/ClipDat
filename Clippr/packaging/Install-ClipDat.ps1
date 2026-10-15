[CmdletBinding()]
param(
    [string] $PackagePath,
    [string] $RuntimePackagePath,
    [switch] $DesktopShortcut,
    [ValidateSet('Express','Minimal','Custom')][string] $Mode = 'Express',
    [string] $ConfigurationPath,
    [switch] $ValidateConfigurationOnly
)

$ErrorActionPreference = 'Stop'
$identityName = 'ce842948-d72b-4d57-ae7b-474f481f442e'
$applicationId = 'App'
$displayName = 'ClipDat'
$minimumBuild = 17763
$backupSettings = Join-Path $env:LOCALAPPDATA 'ClipDat\InstallerBackup\settings.json'

function New-FirstRunSettings {
    $settings = [ordered]@{
        schemaVersion = 2
        clipLengthSeconds = 30
        qualityPreset = 'Standard'
        videoFormat = 'mp4'
        desktopAudioEnabled = $true
        microphoneAudioEnabled = ($Mode -ne 'Minimal')
        organizeClipsByGame = ($Mode -ne 'Minimal')
        screenshotsEnabled = ($Mode -ne 'Minimal')
        quickShareEnabled = ($Mode -ne 'Minimal')
        voiceCommandsEnabled = $false
        notificationStyle = if ($Mode -eq 'Minimal') { 'None' } else { 'Windows' }
        saveNotificationSound = ($Mode -ne 'Minimal')
        screenshotNotificationSound = $false
        startWithWindows = $false
        minimizeToTrayOnClose = $true
        hotkey = 'Ctrl+Shift+F10'
        screenshotFormat = 'png'
        screenshotJpegQuality = 90
    }
    if ($Mode -eq 'Custom' -and -not $ConfigurationPath) {
        throw 'Custom installation requires -ConfigurationPath with the selected first-run settings.'
    }
    if ($ConfigurationPath) {
        $configuration = Get-Content -LiteralPath (Resolve-Path -LiteralPath $ConfigurationPath).Path -Raw | ConvertFrom-Json
        if (-not $configuration -or $configuration -isnot [pscustomobject]) { throw 'Configuration must be a JSON object.' }
        $allowed = @('clipLengthSeconds','qualityPreset','videoFormat','desktopAudioEnabled',
            'microphoneAudioEnabled','organizeClipsByGame','screenshotsEnabled','quickShareEnabled',
            'voiceCommandsEnabled','notificationStyle','saveNotificationSound','screenshotNotificationSound',
            'startWithWindows','minimizeToTrayOnClose','hotkey','screenshotFormat','screenshotJpegQuality','clipFolder')
        foreach ($property in $configuration.PSObject.Properties) {
            if ($property.Name -cnotin $allowed) { throw "Unsupported installer setting: $($property.Name)" }
            $settings[$property.Name] = $property.Value
        }
    }
    $clipLength = 0
    if (-not [int]::TryParse([string]$settings.clipLengthSeconds, [ref]$clipLength) -or
        $clipLength -lt 1 -or $clipLength -gt 3600) { throw 'Clip length must be 1 to 3600 seconds.' }
    $settings.clipLengthSeconds = $clipLength
    if ($settings.qualityPreset -cnotin @('Standard','High')) { throw 'Choose Standard or High quality.' }
    if ($settings.videoFormat -cnotin @('mp4','mkv')) { throw 'Choose MP4 or MKV.' }
    if ($settings.notificationStyle -cnotin @('Windows','ClipDat','None')) { throw 'Choose Windows, ClipDat, or None notifications.' }
    if ($settings.screenshotFormat -cnotin @('png','jpg')) { throw 'Choose PNG or JPEG screenshots.' }
    $jpegQuality = 0
    if (-not [int]::TryParse([string]$settings.screenshotJpegQuality, [ref]$jpegQuality) -or
        $jpegQuality -lt 1 -or $jpegQuality -gt 100) { throw 'JPEG quality must be 1 to 100.' }
    $settings.screenshotJpegQuality = $jpegQuality
    if ($settings.hotkey -notmatch '^Ctrl\+(Alt|Shift)\+F([1-9]|1[0-2])$') { throw 'Choose a supported Ctrl+Alt/Shift+F1-F12 hotkey.' }
    foreach ($key in @('desktopAudioEnabled','microphoneAudioEnabled','organizeClipsByGame','screenshotsEnabled',
        'quickShareEnabled','voiceCommandsEnabled','saveNotificationSound','screenshotNotificationSound',
        'startWithWindows','minimizeToTrayOnClose')) {
        if ($settings[$key] -isnot [bool]) { throw "$key must be true or false." }
    }
    if ($settings.Contains('clipFolder')) {
        if ($settings.clipFolder -isnot [string] -or -not [IO.Path]::IsPathRooted($settings.clipFolder)) {
            throw 'The clips folder must be an absolute path.'
        }
        $settings.clipFolder = [IO.Path]::GetFullPath($settings.clipFolder)
    }
    return ($settings | ConvertTo-Json -Depth 4)
}

function Resolve-PackagePath([string] $Path, [string] $Pattern) {
    if ($Path) {
        $resolved = (Resolve-Path -LiteralPath $Path).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Package file not found: $resolved" }
        return $resolved
    }
    $candidate = Get-ChildItem -LiteralPath $PSScriptRoot -Filter $Pattern -File |
        Where-Object { $_.Name -notmatch '\.msixbundle$' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { throw "No package matching '$Pattern' was found beside this installer." }
    return $candidate.FullName
}

function Assert-SupportedSystem {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [Environment]::OSVersion.Version.Build -lt $minimumBuild) {
        throw "ClipDat requires Windows 10 version 1809 (build $minimumBuild) or newer."
    }
    $processorArchitecture = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Architecture)
    if (-not [Environment]::Is64BitOperatingSystem -or $processorArchitecture -ne 9) {
        throw 'This ClipDat release contains the x64 capture engine and requires 64-bit Intel or AMD Windows.'
    }
}

function Get-PackageSettingsPath {
    $installed = Get-AppxPackage -Name $identityName | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $installed) { return $null }
    return Join-Path $env:LOCALAPPDATA "Packages\$($installed.PackageFamilyName)\LocalCache\Local\Clippr\settings.json"
}

function Stop-InstalledClipDat($InstalledPackage) {
    if (-not $InstalledPackage) { return }
    $processes = Get-Process -Name Clippr -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($InstalledPackage.InstallLocation, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
    foreach ($process in $processes) {
        if ($process.MainWindowHandle -ne 0) { [void]$process.CloseMainWindow() }
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $remaining = @($processes | Where-Object { -not $_.HasExited })
        if ($remaining.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    }
    if (@($processes | Where-Object { -not $_.HasExited }).Count -gt 0) {
        throw 'ClipDat is still running. Close it normally, then run the installer again.'
    }
}

$firstRunJson = New-FirstRunSettings
if ($ValidateConfigurationOnly) { Write-Output $firstRunJson; return }
Assert-SupportedSystem
$package = Resolve-PackagePath $PackagePath 'ClipDat_*_x64.msix'
$runtime = $null
if ($RuntimePackagePath) {
    $runtime = (Resolve-Path -LiteralPath $RuntimePackagePath).Path
} else {
    $runtimeCandidate = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Dependencies\x64') -Filter 'Microsoft.WindowsAppRuntime.2.msix' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($runtimeCandidate) { $runtime = $runtimeCandidate.FullName }
}
if (-not $runtime) { throw 'Windows App SDK 2.5.1 runtime package was not found. Keep Dependencies\x64 beside the installer or pass -RuntimePackagePath.' }

# Shut down cleanly before an in-place update and preserve JSON settings. This is a no-op on first install.
$previousInstall = Get-AppxPackage -Name $identityName | Sort-Object Version -Descending | Select-Object -First 1
Stop-InstalledClipDat $previousInstall
$settingsPath = Get-PackageSettingsPath
if ($settingsPath -and (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupSettings) | Out-Null
    Copy-Item -LiteralPath $settingsPath -Destination $backupSettings -Force
}

$addPackage = @{ Path = $package; DependencyPath = @($runtime); ErrorAction = 'Stop' }
Add-AppxPackage @addPackage

$installed = Get-AppxPackage -Name $identityName | Sort-Object Version -Descending | Select-Object -First 1
if (-not $installed -or $installed.Status -ne 'Ok') { throw 'Windows did not report ClipDat as installed and ready.' }

# Restore settings after an uninstall/reinstall. Never overwrite settings already present in the new package.
$settingsPath = Join-Path $env:LOCALAPPDATA "Packages\$($installed.PackageFamilyName)\LocalCache\Local\Clippr\settings.json"
if ((Test-Path -LiteralPath $backupSettings -PathType Leaf) -and -not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsPath) | Out-Null
    Copy-Item -LiteralPath $backupSettings -Destination $settingsPath
}
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsPath) | Out-Null
    [IO.File]::WriteAllText($settingsPath, $firstRunJson, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Applied $Mode first-run settings. They can be changed in ClipDat Settings."
} else {
    Write-Host 'Existing ClipDat settings were preserved; installer presets were not applied.'
}

if ($DesktopShortcut) {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $shortcutPath = Join-Path $desktop "$displayName.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $env:WINDIR 'explorer.exe'
    $shortcut.Arguments = "shell:AppsFolder\$($installed.PackageFamilyName)!$applicationId"
    $shortcut.Description = $displayName
    $shortcut.Save()
}

Write-Host "$displayName $($installed.Version) is installed for the current Windows user."
Write-Host 'Windows manages the package install location, Start Menu entry, and Installed Apps registration.'
if ($DesktopShortcut) { Write-Host 'A Desktop shortcut was added.' }
