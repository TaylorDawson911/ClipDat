param(
    [switch]$SkipBuild,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $projectRoot '..')).Path
$output = Join-Path $projectRoot 'bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64'
$identity = 'ce842948-d72b-4d57-ae7b-474f481f442e'

if (-not $SkipBuild) {
    & dotnet build (Join-Path $repoRoot 'Clippr.sln') -c Debug -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'The x64 Debug build failed.' }
}

$package = Get-AppxPackage -Name $identity | Sort-Object Version -Descending | Select-Object -First 1
if ($null -eq $package) { throw 'The ClipDat development package is not registered for this user.' }

$output = [IO.Path]::GetFullPath($output)
$stage = [IO.Path]::GetFullPath($package.InstallLocation)
$relativeStage = [IO.Path]::GetRelativePath($output, $stage)
if ($relativeStage.StartsWith('..', [StringComparison]::Ordinal) -or
    [IO.Path]::IsPathRooted($relativeStage) -or
    (Split-Path $stage -Leaf) -notlike 'AppX-ClipDat-*') {
    throw "The registered package is outside ClipDat's development output: $stage"
}

$sourceManifestPath = Join-Path $output 'AppxManifest.xml'
$stageManifestPath = Join-Path $stage 'AppxManifest.xml'
if (-not (Test-Path -LiteralPath $sourceManifestPath) -or
    -not (Test-Path -LiteralPath $stageManifestPath)) {
    throw 'A generated package manifest is missing. Build and deploy ClipDat from Visual Studio first.'
}
[xml]$sourceManifest = Get-Content -LiteralPath $sourceManifestPath
[xml]$stageManifest = Get-Content -LiteralPath $stageManifestPath
if ($sourceManifest.Package.Identity.Name -ne $stageManifest.Package.Identity.Name -or
    $sourceManifest.Package.Identity.Version -ne $stageManifest.Package.Identity.Version) {
    throw 'The build and registered package versions differ. Deploy the matching package first.'
}

if (Get-Process -Name Clippr -ErrorAction SilentlyContinue) {
    throw 'Exit ClipDat from its notification-area menu before updating the development package.'
}

$required = @(
    'Clippr.exe', 'Clippr.dll', 'Clippr.deps.json', 'Clippr.runtimeconfig.json',
    'ClipprCapture.dll', 'resources.pri', 'App.xbf', 'MainWindow.xbf', 'SettingsView.xbf', 'LibraryView.xbf'
)
foreach ($name in $required) {
    $source = Join-Path $output $name
    if (-not (Test-Path -LiteralPath $source)) { throw "Build output is missing $name" }
}

# XBF connection IDs and resources.pri must come from the same build as Clippr.dll.
foreach ($name in $required) {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination $stage -Force
}
Get-ChildItem -LiteralPath $output -Filter 'av*.dll' -File |
    Copy-Item -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $output 'Assets') -Destination $stage -Recurse -Force
Add-AppxPackage -Register -Path $stageManifestPath -DisableDevelopmentMode

Write-Host "Updated ClipDat development package $($package.PackageFullName)"
if ($Launch) {
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($package.PackageFamilyName)!App"
}
