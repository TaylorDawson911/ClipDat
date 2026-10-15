# ClipDat release packaging

ClipDat's main application is a WinUI 3 **MSIX** package. Windows installs it per user and manages its install location, Start Menu entry, Installed Apps registration, and updates. The existing `Clippr/packaging/Build-Release.ps1` packages the app and the Windows App SDK runtime dependency. A traditional Inno installer would create a second deployment identity for the same app, so this release path stays MSIX. The standalone Clip Library remains a separate optional archive with its own installer and uninstaller script.

`Start-Setup.ps1` provides a dark guided setup screen for Express, Minimal, and Custom installation. It applies supported first-run settings and invokes the real MSIX installer. WinForms cannot reproduce every rounded control and animation in the concept artwork; package reliability takes priority.

## Build locally

From the repository root on Windows with Visual Studio C++ build tools, .NET 8, the pinned vcpkg dependencies, and Windows SDK:

```powershell
& .\Clippr\packaging\Build-Release.ps1 -Unsigned -OutputDirectory .\installer\release
& .\installer\scripts\Build-Library.ps1 -OutputDirectory .\installer\release
& .\installer\scripts\Assemble-Release.ps1 -ReleaseDirectory .\installer\release
& .\installer\scripts\Test-Installer.ps1 -ReleaseDirectory .\installer\release
```

Unsigned MSIX builds are for package validation only. For a signed build, replace `-Unsigned` with `-SigningThumbprint THUMBPRINT`. The certificate subject must match `Clippr/Package.appxmanifest` exactly. The current `CN=ryand` publisher is a development identity; establish a trusted production publisher and planned identity migration before public distribution. See [Microsoft's MSIX signing guidance](https://learn.microsoft.com/windows/msix/package/sign-msix-package-guide).

## First-run modes

After extracting the main release archive, run `& .\Start-Setup.ps1` for the guided setup. The command-line equivalents are:

```powershell
& .\Install-ClipDat.ps1 -Mode Express
& .\Install-ClipDat.ps1 -Mode Minimal
& .\Install-ClipDat.ps1 -Mode Custom -ConfigurationPath .\my-settings.json
```

Express enables screenshots, game folders, desktop and microphone audio, and Windows notifications. Minimal disables screenshots, game folders, microphone audio, Quick Share controls, notification sounds, and notifications. Both keep the recording engine in the main package. Voice recognition remains off unless a Custom configuration explicitly enables it. Quick Share never starts public sharing automatically.

Windows owns the main app's install directory and Start Menu registration, so the wizard does not offer controls that MSIX cannot honour. Screenshot files use ClipDat's existing `Screenshots` subfolder. Process audio track routing, separate screenshot storage, and custom sound packs are not installer choices; use the supported in-app settings where available. No automatic background updater is installed.

The Custom JSON can contain only the keys accepted by `Install-ClipDat.ps1`, for example:

```json
{
  "clipLengthSeconds": 30,
  "qualityPreset": "High",
  "videoFormat": "mp4",
  "clipFolder": "C:\\Users\\YourName\\Videos\\ClipDat",
  "organizeClipsByGame": true,
  "desktopAudioEnabled": true,
  "microphoneAudioEnabled": false,
  "screenshotsEnabled": true,
  "notificationStyle": "Windows"
}
```

Validate without installing: `& .\Install-ClipDat.ps1 -Mode Custom -ConfigurationPath .\my-settings.json -ValidateConfigurationOnly`. On a fresh install, the installer writes the selected values to ClipDat's existing JSON settings path. If settings already exist or have been preserved from an earlier uninstall, the installer leaves them intact. User clips and screenshots are never moved or deleted.

The optional Library archive can be extracted anywhere and installed by running its `Install-Library.ps1` script. It installs to the current user's Programs directory. Its `Uninstall-Library.ps1` preserves media and Library metadata by default. The in-app Clip Library remains available even without this optional companion.

## Validation and release

`Test-Installer.ps1` defaults to non-installing package checks, signature checks when requested, and SHA-256 verification. `-InstallForCurrentUser -IUnderstandCurrentUserTest` performs install/uninstall only when no ClipDat package is already registered for that Windows user. Use a disposable Windows test account or VM. This test does not prove capture on every GPU or a clean upgrade.

The current verification boundary is documented in [RELEASE_TEST_RECORD.md](RELEASE_TEST_RECORD.md).

The GitHub workflow builds on a Windows runner for `vMAJOR.MINOR.PATCH` tags and manual runs. Manual runs produce **unsigned validation artifacts**. Publishing a tag creates a GitHub Release only when repository variable `CLIPDAT_RELEASE_APPROVED` is `true` and the signing secrets `CLIPDAT_SIGNING_PFX_BASE64` and `CLIPDAT_SIGNING_PFX_PASSWORD` are configured. The workflow checks tag/manifest version agreement and aborts if build, signing, package validation, or checksum generation fails. It does not change repository visibility.

Before the first public release, confirm the repository release policy and licence, choose the production signing identity, test fresh install, upgrade, repair, and uninstall on clean Windows 10 and 11 x64 systems, and verify the Windows App SDK dependency on a machine without it preinstalled. Then set the approval variable and secrets, change the manifest version, push a matching tag, and review the workflow result. Downloads live at [GitHub Releases](https://github.com/TaylorDawson911/ClipDat/releases/latest).
