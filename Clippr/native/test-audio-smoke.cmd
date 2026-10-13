@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -property installationPath`) do set "VS_ROOT=%%I"
if not defined VS_ROOT exit /b 1
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
set "FFMPEG_ROOT=%~dp0vcpkg_installed\x64-windows"
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" /I"%FFMPEG_ROOT%\include" "%~dp0tests\audio_capture_smoke.cpp" "%~dp0src\audio_capture.cpp" /link /LIBPATH:"%FFMPEG_ROOT%\lib" /LIBPATH:"%FFMPEG_ROOT%\bin" ole32.lib uuid.lib windowsapp.lib avcodec.lib avutil.lib /OUT:"%~dp0build\x64\audio_capture_smoke.exe"
if errorlevel 1 exit /b %errorlevel%
"%~dp0build\x64\audio_capture_smoke.exe" %*
exit /b %errorlevel%
