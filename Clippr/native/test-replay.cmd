@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -property installationPath`) do set "VS_ROOT=%%I"
if not defined VS_ROOT exit /b 1
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++latest /EHsc /W4 /permissive- /MD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%~dp0include" "%~dp0tests\replay_buffer_tests.cpp" "%~dp0src\replay_buffer.cpp" /Fe:"%~dp0build\x64\replay_buffer_tests.exe"
if errorlevel 1 exit /b %errorlevel%
"%~dp0build\x64\replay_buffer_tests.exe"
exit /b %errorlevel%
