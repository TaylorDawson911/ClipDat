@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -property installationPath`) do set "VS_ROOT=%%I"
if not defined VS_ROOT exit /b 1
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" "%~dp0tests\encoder_modes_smoke.cpp" "%~dp0src\hardware_h264_encoder.cpp" /Fe:"%~dp0build\x64\encoder_modes_smoke.exe" d3d11.lib mfplat.lib mfreadwrite.lib mfuuid.lib mf.lib ole32.lib
if errorlevel 1 exit /b %errorlevel%
"%~dp0build\x64\encoder_modes_smoke.exe" %*
exit /b %errorlevel%
