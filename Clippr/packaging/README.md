# ClipDat Windows release packaging

Extract the release Setup ZIP and run `Start-Setup.ps1` for the Express, Minimal, or Custom first-run wizard. It applies supported ClipDat settings and invokes the MSIX install helper. To validate a downloaded package without installing it, run `installer/scripts/Test-Installer.ps1` from the source repository.

ClipDat is already a Windows App SDK packaged WinUI 3 application. Releases stay MSIX packages. Windows owns the package install location, per-user registration, Start Menu entry, Installed Apps entry, package file cleanup, and update identity. A WiX MSI wrapper would duplicate those responsibilities and conflict with the existing package identity.

## Supported release target

- Windows 10 version 1809 (build 17763) or later, including Windows 11.
- x64 Intel and AMD systems. The current native capture DLL and FFmpeg runtime are x64 only; ARM64 and x86 are intentionally not release targets.
- Per-user installation through App Installer or `Install-ClipDat.ps1`; no administrator rights are required for the current-user package deployment.
- Windows App SDK 2.5.1 framework dependency is shipped next to the MSIX in `Dependencies\x64` and installed as part of the package deployment. Windows resolves an already installed framework or installs the sidecar dependency. No browser, advertising, telemetry, service, or unrelated software is installed.

MSIX does not let an application choose its package directory. Windows chooses the location and manages Start Menu and Installed Apps registration. A Desktop shortcut is optional.

## Build a release

1. Increment the four-part version in `Package.appxmanifest` for each release.
2. Obtain an organization-controlled Authenticode code-signing certificate. For updates, its subject must remain exactly equal to the package `Publisher` value in `Package.appxmanifest`; changing package identity/publisher breaks the normal update path. Replace the development publisher `CN=ryand` only as a planned identity migration before first public release.
3. Make the certificate and private key available in the current-user or local-machine `My` certificate store, then run:

   ```powershell
   .\packaging\Build-Release.ps1 -SigningThumbprint YOUR_CERTIFICATE_THUMBPRINT
   ```

The build script verifies certificate subject, private key, and expiry, builds Release x64, packages the Windows App SDK runtime dependency, and writes a versioned `.msix`, SHA-256 release manifest, and install/repair/uninstall helpers under `packaging\release`. Use the same controlled build tools and NuGet lock/feeds for repeatable builds; MSIX timestamps/signatures can make byte-for-byte output differ between builds.

For local unsigned build validation only:

```powershell
.\packaging\Build-Release.ps1 -Unsigned
```

Unsigned packages are not distributable and Windows will reject them in ordinary end-user installation flows.

## Install, update, repair, uninstall

Run `Install-ClipDat.ps1` beside the versioned MSIX for a per-user install. Pass `-DesktopShortcut` to add a desktop shortcut. The package supplies the Start Menu and Installed Apps entries. Installing a higher version with the same identity upgrades the existing package and preserves package settings.

`Repair-ClipDat.ps1` reapplies the matching package. It does not reset app data. `Uninstall-ClipDat.ps1` requests a normal app shutdown and then removes the current-user package. By default it saves the JSON settings outside the package data folder so a reinstall can restore them. User clips and screenshots are outside the package and are never touched by these scripts. To also remove app settings, use `-RemoveSettings`; the script requires typing `REMOVE`. Recordings remain untouched even with that option.

PowerShell execution policy may require launching the helper from an approved PowerShell session. The MSIX itself can also be installed using Windows App Installer.

## Update the local development package

After editing XAML, exit ClipDat from its notification-area menu and run `packaging\Update-DevPackage.ps1 -Launch` from a PowerShell session. The script builds Debug x64, copies the matching executable, XBF files, and `resources.pri` into the registered development package, then registers and opens it. Use `-SkipBuild` only when the current Debug x64 build has already succeeded. An old `resources.pri` paired with new XBF files can crash WinUI while opening the window.

## Signing and publisher metadata

Public distribution needs a trusted code-signing certificate and a publisher value matching the certificate subject. The current manifest's `CN=ryand` is a development identity, not verified publisher metadata. Do not publish the current identity or a self-signed test certificate as a production trust claim. Timestamp the package signature with the release organization’s approved timestamp service when signing through its normal release process.

## Test record

Validation on Windows 11 x64 build 26200, 2026-10-13:

- Release build produced `ClipDat_1.0.13.0_x64.msix` and the x64 Windows App SDK 2.5.1 dependency. The embedded package identity, publisher, version, architecture, and minimum Windows version were inspected.
- The uninstaller removed the existing current-user 1.0.12.0 package. The settings JSON was preserved byte-for-byte, and the configured clip folder inventory was unchanged (2 files).
- A signed local test package was rejected by Windows with `0x800B0109` because the temporary self-signed root was not trusted. Fresh install, in-place upgrade, and repair of the release MSIX could not be verified on this machine. The original development package was restored and launched afterward.
- The generated distributable package is unsigned. The manifest publisher `CN=ryand` is a development identity, not verified production publisher metadata.

Do not call a release production-ready until a publisher-matched trusted signing certificate is available and fresh installation, in-place upgrade, repair, and uninstallation pass on a clean Windows 10 1809+ x64 machine. Package creation alone is not evidence that these flows work on a clean machine. Also test with no Windows App SDK runtime already installed.
