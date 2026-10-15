## ClipDat release

Download the ClipDat Setup ZIP, extract it, and run `Start-Setup.ps1` from PowerShell. Keep the `Dependencies` folder beside the script. The optional standalone Clip Library is included as a selectable add-on and is also available as a separate ZIP.

### Changes

- Replay clips can be organised into game folders, with custom executable mappings.
- The main and optional standalone Clip Libraries discover game subfolders.
- The installer supports Express and Minimal first-run settings without replacing existing settings during upgrades.

### Upgrade and known limits

- Install the newer MSIX with the same package identity and signing publisher. Close ClipDat normally before upgrading.
- Existing clips, screenshots, settings, hotkeys, and mappings are preserved.
- Public Quick Share links are unavailable until a supported, verified HTTPS endpoint is configured. Voice recognition requires local Windows speech support and microphone permission.
- Direct recording file organisation and the exact rounded visual treatment in the concept art are not included.
- Clean-machine installation, upgrade, repair, and runtime behaviour must be verified before marking a release production-ready.

Verify downloads against `SHA256SUMS.txt`. Windows may warn about unsigned development artifacts; public releases require a trusted signature.
