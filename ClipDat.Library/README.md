# ClipDat Library

ClipDat Library is an older optional standalone WinUI 3 application with its own executable, installation entry, Start Menu shortcut, metadata store, and uninstaller. ClipDat now includes the library in its main window; the main app's Clip Library button, Quick Share settings, and tray menu open that page instead of this separate application. This standalone app can still be launched from its Start Menu shortcut if installed.

## Implemented

- Recursively finds MP4/MKV/MOV clips in game subfolders and PNG/JPEG screenshots in the configured clips folder. The standalone app reads Core's packaged settings on first launch or lets the user choose a folder.
- Scans files off the UI thread and uses a debounced filesystem watcher to update the list. The grid virtualizes its cards; thumbnails load when cards appear and the in-memory cache has a 160-item limit. Windows Shell thumbnails are used for images and supported videos. MKV uses a video icon because Shell thumbnail generation is inconsistent across machines.
- Searches file names, descriptions, and tags. Filters clips, screenshots, favourites, and game folders. Sorts by date, name, or file size.
- Previews selected files using the native media player or image viewer. Double-click opens a file in its default Windows application. File actions support rename, copy file, copy path, and show in folder.
- Supports multiple selection and moves files to the Windows Recycle Bin only after confirmation. The test used temporary files; user recordings were not deleted.
- Saves descriptions, tags, and favourites in `%LOCALAPPDATA%\ClipDat Library\library.json`, outside the clips folder. Metadata follows a file moved between game folders when its name, size, and creation time match one missing entry. Uninstall preserves this file and all media unless `-RemoveLibrarySettings` is explicitly passed.
- Does not start a server, change the router, upload media, or offer a fake public URL. Quick Share in Core reports the verified status.

## Build and install

On a Windows x64 developer machine with the .NET 8 SDK and WinUI build tools:

```powershell
dotnet build Clippr.sln -c Debug -p:Platform=x64
.\ClipDat.Library\packaging\Install-Library.ps1
```

The installer script publishes a self-contained x64 build, copies matching WinUI XBF/PRI resources, and installs per-user under `%LOCALAPPDATA%\Programs\ClipDat Library`. It registers a Start Menu shortcut and an Installed Apps entry. Pass `-DesktopShortcut` to add a desktop shortcut. To uninstall:

```powershell
.\ClipDat.Library\packaging\Uninstall-Library.ps1
```

The script-based packaging needs the .NET SDK on the machine that builds the release. A signed, user-facing installer executable has not been produced or tested. The Library's installed executable is self-contained; its installation does not require ClipDat Core to run.

## Current limits

- Direct browser links remain unavailable. A safe public link requires both a browser-trusted HTTPS endpoint and proof that another network can reach it; the current application has neither. Copy Link is never enabled for a local-only clip.
- Editing, trimming, collections, additional watched folders, date/duration/application filters, and per-video duration/resolution metadata are not implemented. The UI does not present fake actions for them.
- MKV thumbnail generation is omitted pending a reliable local decoder path. Video playback depends on codecs installed in Windows.
- The installer and Library were exercised on this Windows x64 machine; fresh Windows installs and other hardware have not been tested.
