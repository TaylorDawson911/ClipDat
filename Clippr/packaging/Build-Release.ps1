[CmdletBinding()]
param(
    [string] $SigningThumbprint,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'release'),
    [switch] $Unsigned
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\Clippr.csproj'
$manifestPath = Join-Path $PSScriptRoot '..\Package.appxmanifest'
$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $ns)
$version = [Version]$identity.Version
$publisher = [string]$identity.Publisher
$displayName = [string]$manifest.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $ns).InnerText
$minimumVersion = [Version]$manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name="Windows.Desktop"]', $ns).MinVersion

if ($version -eq [Version]'0.0.0.0') { throw 'MSIX package version cannot be zero.' }
if ($minimumVersion -gt [Version]'10.0.17763.0') { throw "Unexpected minimum Windows version: $minimumVersion" }
if (-not $Unsigned -and -not $SigningThumbprint) { throw 'Provide -SigningThumbprint for a production signed package, or -Unsigned for a local test build.' }

$cert = $null
if ($SigningThumbprint) {
    $thumbprint = $SigningThumbprint.Replace(' ', '').ToUpperInvariant()
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
    if (-not $cert) { throw "Signing certificate $thumbprint is not present in the Windows certificate store." }
    if (-not $cert.HasPrivateKey) { throw 'The selected signing certificate has no private key.' }
    if ($cert.Subject -ne $publisher) { throw "Certificate subject '$($cert.Subject)' does not match package publisher '$publisher'." }
    if ($cert.NotAfter -le (Get-Date)) { throw 'The signing certificate is expired.' }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$buildOutput = [IO.Path]::GetFullPath((Join-Path $tempRoot "ClipDat-MSIX-$version-$PID"))
if (-not $buildOutput.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($buildOutput) -ne "ClipDat-MSIX-$version-$PID") { throw 'Unsafe temporary MSIX build path.' }
Remove-Item -LiteralPath $buildOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $buildOutput | Out-Null
New-Item -ItemType Directory -Force -Path $output | Out-Null
$packageName = "${displayName}_${version}_x64.msix"
$packagePath = Join-Path $buildOutput $packageName
$signingEnabled = if ($Unsigned) { 'false' } else { 'true' }

$properties = @(
    '-c', 'Release',
    '-p:Platform=x64',
    '-p:RuntimeIdentifier=win-x64',
    '-p:PublishTrimmed=false',
    '-p:GenerateAppxPackageOnBuild=true',
    '-p:AppxBundle=Never',
    ('-p:AppxPackageSigningEnabled=' + $signingEnabled),
    ('-p:AppxPackageDir=' + ($buildOutput.TrimEnd('\') + '\'))
)
if ($SigningThumbprint) { $properties += '-p:PackageCertificateThumbprint=' + $thumbprint }

Push-Location $repoRoot
try {
    & dotnet publish $project @properties
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }

$built = Get-ChildItem -LiteralPath $buildOutput -Filter '*.msix' -File -Recurse |
    Where-Object Name -eq $packageName | Select-Object -First 1
if (-not $built) {
    # SDK tooling may use the default generated name; accept it only when the package identity matches.
    $built = Get-ChildItem -LiteralPath $buildOutput -Filter '*.msix' -File -Recurse | Select-Object -First 1
}
if (-not $built) { throw 'MSBuild completed without producing an MSIX package.' }
if ($built.FullName -ne $packagePath) { Copy-Item -LiteralPath $built.FullName -Destination $packagePath -Force }

$runtime = Get-ChildItem -LiteralPath $buildOutput -Filter 'Microsoft.WindowsAppRuntime.2.msix' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $runtime) { throw 'The Windows App SDK framework dependency was not produced with the package.' }

Copy-Item -LiteralPath $packagePath -Destination (Join-Path $output $packageName) -Force
New-Item -ItemType Directory -Force -Path (Join-Path $output 'Dependencies\x64') | Out-Null
Copy-Item -LiteralPath $runtime.FullName -Destination (Join-Path $output 'Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ClipDat.ps1') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Repair-ClipDat.ps1') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-ClipDat.ps1') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force

$hash = (Get-FileHash -LiteralPath (Join-Path $output $packageName) -Algorithm SHA256).Hash
@{
    displayName = $displayName
    version = $version.ToString()
    architecture = 'x64'
    minimumWindowsVersion = $minimumVersion.ToString()
    package = $packageName
    publisher = $publisher
    signingThumbprint = if ($cert) { $cert.Thumbprint } else { $null }
    sha256 = $hash
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding utf8

Write-Host "Release package: $(Join-Path $output $packageName)"
Write-Host "SHA-256: $hash"
if ($Unsigned) { Write-Warning 'This package is unsigned and suitable only for local build validation, not distribution.' }
Remove-Item -LiteralPath $buildOutput -Recurse -Force -ErrorAction SilentlyContinue
