@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -property installationPath`) do set "VS_ROOT=%%I"
if not defined VS_ROOT exit /b 1
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
set "FFMPEG_ROOT=%~dp0vcpkg_installed\x64-windows"
if not exist "%~dp0build\x64" mkdir "%~dp0build\x64"
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /I"%FFMPEG_ROOT%\include" /c "%~dp0tests\audio_capture_smoke.cpp" /Fo:"%~dp0build\x64\audio_capture_smoke.obj"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /I"%FFMPEG_ROOT%\include" /c "%~dp0src\audio_capture.cpp" /Fo:"%~dp0build\x64\audio_capture_smoke_support.obj"
if errorlevel 1 exit /b %errorlevel%
link /nologo /OUT:"%~dp0build\x64\audio_capture_smoke.exe" /LIBPATH:"%FFMPEG_ROOT%\lib" /LIBPATH:"%FFMPEG_ROOT%\bin" "%~dp0build\x64\audio_capture_smoke.obj" "%~dp0build\x64\audio_capture_smoke_support.obj" ole32.lib uuid.lib windowsapp.lib avcodec.lib avutil.lib
if errorlevel 1 exit /b %errorlevel%
"%~dp0build\x64\audio_capture_smoke.exe" %*
exit /b %errorlevel%
