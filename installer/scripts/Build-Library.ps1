[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\release'))

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repo 'ClipDat.Library\ClipDat.Library.csproj'
$version = [regex]::Match((Get-Content -LiteralPath $project -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version) { throw 'Clip Library project version was not found.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$work = [IO.Path]::GetFullPath((Join-Path $tempRoot "ClipDat-Library-$PID"))
if (-not $work.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($work) -ne "ClipDat-Library-$PID") { throw 'Unsafe temporary Library build path.' }
if (Test-Path -LiteralPath $work) { throw "Temporary build directory already exists: $work" }
New-Item -ItemType Directory -Force -Path (Join-Path $work 'payload'), $output | Out-Null
try {
    & dotnet publish $project -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        -p:WindowsAppSDKSelfContained=true --self-contained true -o (Join-Path $work 'payload')
    if ($LASTEXITCODE -ne 0) { throw 'Clip Library publish failed.' }
    $build = Join-Path $repo 'ClipDat.Library\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'
    foreach ($resource in @('App.xbf','LibraryWindow.xbf','ClipDat.Library.pri')) {
        $source = Join-Path $build $resource
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Published Library resource is missing: $resource" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $work 'payload') -Force
    }
    foreach ($file in @('ClipDat.Library.exe','ClipDat.Library.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $work 'payload' $file) -PathType Leaf)) { throw "Published Library file is missing: $file" }
    }
    Copy-Item -LiteralPath (Join-Path $repo 'ClipDat.Library\packaging\Install-Library.ps1') -Destination $work
    Copy-Item -LiteralPath (Join-Path $repo 'ClipDat.Library\packaging\Uninstall-Library.ps1') -Destination $work
    $zip = Join-Path $output "ClipDat-Library-v$version.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Library package: $zip"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
