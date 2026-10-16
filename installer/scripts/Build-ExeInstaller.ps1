[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\release'))

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $root 'Clippr\Clippr.csproj'
$wizardProject = Join-Path $root 'ClipDat.Setup\ClipDat.Setup.csproj'
$bootstrapProject = Join-Path $root 'ClipDat.Setup.Bootstrap\ClipDat.Setup.Bootstrap.csproj'
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
    $payload = Join-Path $work 'payload'
    $wizard = Join-Path $work 'wizard'
    $innerOutput = Join-Path $work 'inner'
    $bootstrapOutput = Join-Path $work 'bootstrap'
    New-Item -ItemType Directory -Force -Path $payload, $wizard, $innerOutput, $bootstrapOutput | Out-Null
    & dotnet publish $project -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        -p:ClipDatUnpackaged=true -p:PublishTrimmed=false -p:PublishSingleFile=false `
        -p:GenerateAppxPackageOnBuild=false "-p:Version=$version" --self-contained true -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Unpackaged ClipDat publish failed.' }
    $build = Join-Path $root 'Clippr\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'
    foreach ($resource in @('App.xbf', 'MainWindow.xbf', 'SettingsView.xbf', 'LibraryView.xbf')) {
        $source = Join-Path $build $resource
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Compiled XAML is missing: $resource" }
        Copy-Item -LiteralPath $source -Destination $payload -Force
    }
    foreach ($notice in @('THIRD_PARTY_NOTICES.md', 'FFmpegBuildConfiguration.txt')) {
        Copy-Item -LiteralPath (Join-Path $root "Clippr\$notice") -Destination $payload -Force
    }
    $ffmpegCopyright = Join-Path $root 'Clippr\native\vcpkg_installed\x64-windows\share\ffmpeg\copyright'
    if (Test-Path -LiteralPath $ffmpegCopyright -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path (Join-Path $payload 'FFmpeg') | Out-Null
        Copy-Item -LiteralPath $ffmpegCopyright -Destination (Join-Path $payload 'FFmpeg\LICENSE.txt') -Force
    }
    foreach ($file in @('Clippr.exe', 'Clippr.dll', 'ClipprCapture.dll', 'Microsoft.UI.Xaml.dll', 'Clippr.pri')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) {
            throw "Published app is missing $file"
        }
    }
    & $iscc "/DPayloadDir=$payload" "/DOutputDir=$innerOutput" "/DAppVersion=$version" `
        (Join-Path $root 'installer\ClipDat.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE" }
    $innerSetup = Join-Path $innerOutput "ClipDat-Setup-v$version-x64.exe"
    if (-not (Test-Path -LiteralPath $innerSetup -PathType Leaf)) { throw 'File installer was not produced.' }

    & dotnet publish $wizardProject -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        "-p:Version=$version" --self-contained true -o $wizard
    if ($LASTEXITCODE -ne 0) { throw 'WinUI setup wizard publish failed.' }
    $wizardBuild = Join-Path $root 'ClipDat.Setup\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'
    foreach ($resource in @('App.xbf', 'MainWindow.xbf', 'ClipDat.Setup.pri')) {
        $source = Join-Path $wizardBuild $resource
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Setup XAML resource is missing: $resource" }
        Copy-Item -LiteralPath $source -Destination $wizard -Force
    }
    Copy-Item -LiteralPath $innerSetup -Destination (Join-Path $wizard 'ClipDat-Payload.exe') -Force
    $bundle = Join-Path $work 'ClipDat.Setup.Bundle.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($wizard, $bundle, [IO.Compression.CompressionLevel]::Optimal, $false)
    & dotnet publish $bootstrapProject -c Release -p:RuntimeIdentifier=win-x64 `
        -p:PublishSingleFile=true "-p:SetupArchivePath=$bundle" "-p:Version=$version" `
        --self-contained true -o $bootstrapOutput
    if ($LASTEXITCODE -ne 0) { throw 'Single-file setup bootstrap publish failed.' }
    $setup = Join-Path $output "ClipDat-Setup-v$version-x64.exe"
    Copy-Item -LiteralPath (Join-Path $bootstrapOutput 'ClipDat-Setup.exe') -Destination $setup -Force
    Write-Host "Installer: $setup"
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
