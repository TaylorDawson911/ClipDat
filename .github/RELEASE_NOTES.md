## ClipDat v1.0.24

Download `ClipDat-Setup-v1.0.24.0-x64.exe` and run it to install ClipDat for your Windows user account. The installer creates a Start Menu shortcut, offers an optional desktop shortcut, and can be removed from Windows Installed Apps.

### Changes

- Search across Settings from any tab. Results jump to the relevant setting, including advanced options.
- The installer includes the app's runtime and preserves clips and settings when uninstalled.

### Upgrade and known limits

- Close ClipDat before installing or updating. The EXE edition does not remove an older MSIX installation; both editions share a single-instance lock.
- Existing clips, screenshots, settings, hotkeys, and mappings are preserved. The EXE edition copies eligible MSIX data only when its own data does not already exist.
- Public Quick Share links are unavailable until a supported, verified HTTPS endpoint is configured. Voice recognition requires local Windows speech support and microphone permission.
- This installer is unsigned, so Windows SmartScreen may warn when opening it. Runtime checks were performed on a development PC; clean-machine installation remains unverified.

Verify the download against its SHA-256 checksum in the release assets.
