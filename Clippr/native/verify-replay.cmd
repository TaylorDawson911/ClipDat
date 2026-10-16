@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -property installationPath`) do set "VS_ROOT=%%I"
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if not defined CLIPPR_FFMPEG_ROOT set "CLIPPR_FFMPEG_ROOT=%~dp0vcpkg_installed\x64-windows"
set "FFMPEG_ROOT=%CLIPPR_FFMPEG_ROOT%"
if not exist "%~dp0build\x64" mkdir "%~dp0build\x64"
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%FFMPEG_ROOT%\include" /Fo:"%~dp0build\x64\verify_replay.obj" /Fe:"%~dp0build\x64\verify_replay.exe" "%~dp0tests\verify_replay.cpp" /link /LIBPATH:"%FFMPEG_ROOT%\lib" /LIBPATH:"%FFMPEG_ROOT%\bin" avformat.lib avcodec.lib avutil.lib
if errorlevel 1 exit /b %errorlevel%
set "PATH=%~dp0build\x64;%FFMPEG_ROOT%\bin;%PATH%"
"%~dp0build\x64\verify_replay.exe" %*
exit /b %errorlevel%
