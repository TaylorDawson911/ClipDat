@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" exit /b 1
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -property installationPath`) do set "VS_ROOT=%%I"
if not defined VS_ROOT exit /b 1
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
if not defined CLIPPR_FFMPEG_ROOT set "CLIPPR_FFMPEG_ROOT=%~dp0vcpkg_installed\x64-windows"
set "FFMPEG_ROOT=%CLIPPR_FFMPEG_ROOT%"
if not exist "%FFMPEG_ROOT%\include\libavformat\avformat.h" (
  echo FFmpeg libraries are missing. Run vcpkg install --triplet x64-windows from the project root.
  exit /b 2
)
if not exist "%~dp0build\x64" mkdir "%~dp0build\x64"
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /I"%USERPROFILE%\.nuget\packages\microsoft.windowsappsdk.winui\2.3.9\include" /c "%~dp0src\capture.cpp" /Fo:"%~dp0build\x64\capture.obj"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /I"%USERPROFILE%\.nuget\packages\microsoft.windowsappsdk.winui\2.3.9\include" /c "%~dp0src\settings_store.cpp" /Fo:"%~dp0build\x64\settings_store.obj"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /c "%~dp0src\hardware_h264_encoder.cpp" /Fo:"%~dp0build\x64\hardware_h264_encoder.obj"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /c "%~dp0src\replay_buffer.cpp" /Fo:"%~dp0build\x64\replay_buffer.obj"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /I"%FFMPEG_ROOT%\include" /c "%~dp0src\replay_export.cpp" /Fo:"%~dp0build\x64\replay_export.obj"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /I"%FFMPEG_ROOT%\include" /c "%~dp0src\audio_capture.cpp" /Fo:"%~dp0build\x64\audio_capture.obj"
if errorlevel 1 exit /b %errorlevel%
link /nologo /DLL /LIBPATH:"%FFMPEG_ROOT%\lib" /LIBPATH:"%FFMPEG_ROOT%\bin" /OUT:"%~dp0build\x64\ClipprCapture.dll" "%~dp0build\x64\capture.obj" "%~dp0build\x64\settings_store.obj" "%~dp0build\x64\hardware_h264_encoder.obj" "%~dp0build\x64\audio_capture.obj" "%~dp0build\x64\replay_buffer.obj" "%~dp0build\x64\replay_export.obj" d3d11.lib dxgi.lib d2d1.lib dwmapi.lib version.lib shell32.lib user32.lib gdi32.lib windowsapp.lib mfplat.lib mfreadwrite.lib mfuuid.lib mf.lib ole32.lib uuid.lib windowscodecs.lib avformat.lib avcodec.lib avutil.lib
if errorlevel 1 exit /b %errorlevel%
for %%F in ("%FFMPEG_ROOT%\bin\avformat-*.dll" "%FFMPEG_ROOT%\bin\avcodec-*.dll" "%FFMPEG_ROOT%\bin\avutil-*.dll") do if exist "%%~F" copy /y "%%~F" "%~dp0build\x64\" >nul
exit /b %errorlevel%
