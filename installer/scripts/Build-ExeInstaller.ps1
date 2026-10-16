[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\release'))

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $root 'Clippr\Clippr.csproj'
$manifest = [xml](Get-Content -LiteralPath (Join-Path $root 'Clippr\Package.appxmanifest') -Raw)
$version = [string]$manifest.Package.Identity.Version
$output = [IO.Path]::GetFullPath($OutputDirectory)
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$work = [IO.Path]::GetFullPath((Join-Path $tempRoot "ClipDat-EXE-$PID"))
if (-not $work.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($work) -ne "ClipDat-EXE-$PID") { throw 'Unsafe temporary build path.' }
if (Test-Path -LiteralPath $work) { throw "Temporary build directory already exists: $work" }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required. Install JRSoftware.InnoSetup with winget.' }

New-Item -ItemType Directory -Force -Path $work, $output | Out-Null
try {
    & dotnet publish $project -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        -p:ClipDatUnpackaged=true -p:PublishTrimmed=false -p:PublishSingleFile=false `
        -p:GenerateAppxPackageOnBuild=false "-p:Version=$version" --self-contained true -o $work
    if ($LASTEXITCODE -ne 0) { throw 'Unpackaged ClipDat publish failed.' }
    $build = Join-Path $root 'Clippr\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'
    foreach ($resource in @('App.xbf', 'MainWindow.xbf', 'SettingsView.xbf', 'LibraryView.xbf')) {
        $source = Join-Path $build $resource
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Compiled XAML is missing: $resource" }
        Copy-Item -LiteralPath $source -Destination $work -Force
    }
    foreach ($notice in @('THIRD_PARTY_NOTICES.md', 'FFmpegBuildConfiguration.txt')) {
        Copy-Item -LiteralPath (Join-Path $root "Clippr\$notice") -Destination $work -Force
    }
    $ffmpegCopyright = Join-Path $root 'Clippr\native\vcpkg_installed\x64-windows\share\ffmpeg\copyright'
    if (Test-Path -LiteralPath $ffmpegCopyright -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path (Join-Path $work 'FFmpeg') | Out-Null
        Copy-Item -LiteralPath $ffmpegCopyright -Destination (Join-Path $work 'FFmpeg\LICENSE.txt') -Force
    }
    foreach ($file in @('Clippr.exe', 'Clippr.dll', 'ClipprCapture.dll', 'Microsoft.UI.Xaml.dll', 'Clippr.pri')) {
        if (-not (Test-Path -LiteralPath (Join-Path $work $file) -PathType Leaf)) {
            throw "Published app is missing $file"
        }
    }
    & $iscc "/DPayloadDir=$work" "/DOutputDir=$output" "/DAppVersion=$version" `
        (Join-Path $root 'installer\ClipDat.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE" }
    $setup = Join-Path $output "ClipDat-Setup-v$version-x64.exe"
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer executable was not produced.' }
    Write-Host "Installer: $setup"
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
