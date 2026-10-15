[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseDirectory)

$ErrorActionPreference = 'Stop'
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$files = @(
    Get-ChildItem -LiteralPath $release -File |
        Where-Object { $_.Extension -in @('.msix','.zip') }
    Get-ChildItem -LiteralPath (Join-Path $release 'Dependencies\x64') -Filter '*.msix' -File -ErrorAction SilentlyContinue
) | Sort-Object FullName
if (-not ($files | Where-Object Extension -eq '.msix')) { throw 'No MSIX was found for checksums.' }
$lines = foreach ($file in $files) {
    $relative = $file.FullName.Substring($release.TrimEnd('\').Length + 1).Replace('\','/')
    "$( (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash )  $relative"
}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), [string[]]$lines,
    (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $($files.Count) SHA-256 checksums."
