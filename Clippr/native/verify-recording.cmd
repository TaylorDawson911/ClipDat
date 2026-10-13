@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -property installationPath`) do set "VS_ROOT=%%I"
if not defined VS_ROOT exit /b 1
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD "%~dp0tests\verify_recording.cpp" /Fe:"%~dp0build\x64\verify_recording.exe" mfplat.lib mfreadwrite.lib mfuuid.lib mf.lib ole32.lib
if errorlevel 1 exit /b %errorlevel%
"%~dp0build\x64\verify_recording.exe" "%~1"
exit /b %errorlevel%
