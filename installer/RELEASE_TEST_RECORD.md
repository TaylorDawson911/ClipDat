# Release packaging test record — 2026-10-15

Environment: Windows 11 Home x64, build 26200. Existing ClipDat 1.0.22.0 MSIX remained registered throughout testing.

## Passed on this machine

- Main application Debug and Release x64 builds completed. The unsigned MSIX packaging script produced `ClipDat_1.0.22.0_x64.msix` and a Windows App SDK runtime dependency.
- Standalone Clip Library published and packaged as `ClipDat-Library-v0.1.0.zip`, with its executable, WinUI resources, and install/uninstall helpers present.
- The setup bundle was assembled with the MSIX, runtime dependency, guided setup, and optional Library archive. Package identity, required files, and SHA-256 checksums passed the non-installing validator.
- The extracted setup bundle opened and completed its Express preview flow. Source wizard preview flows for Express, Minimal, and Custom completed without installation. Visual previews of Welcome, Features, Recording, Storage, Experience, and Review were inspected.
- Express, Minimal, and Custom first-run configuration generation was checked. Minimal disables screenshots, game folders, Quick Share controls, and notifications. Invalid custom numeric values are rejected.
- Game organisation and local share smoke checks passed.
- The install smoke test detected the existing ClipDat registration and refused to replace it. The registered package remained version 1.0.22.0.
- PowerShell release scripts parsed; workflow YAML parsed; build outputs and credentials were confirmed ignored by Git.

## Not verified

- The locally built MSIX is **unsigned** (`Get-AuthenticodeSignature` reported `NotSigned`). It was not installed or published.
- Fresh installation, upgrade, repair, uninstallation, Windows App SDK dependency installation on a clean PC, startup task activation, and actual first-run settings persistence were not executed. Run these on disposable Windows 10 and 11 x64 test accounts or VMs with a trusted publisher-matched signing certificate.
- GitHub Actions has not yet run. The workflow may need runner-specific adjustment after its first manual validation run.
- The exact rounded concept-art design cannot be reproduced by the WinForms setup wizard. Its controls and installer flow are functional in preview; the install action itself awaits a signed package test.
- Screen recording, microphone, screenshots, Quick Share, and voice recognition were not runtime-tested as part of installer validation.

No public release tag was created. The release workflow requires an explicit repository approval variable and signing secrets before publishing a tag build.
