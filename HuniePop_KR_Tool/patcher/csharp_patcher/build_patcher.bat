@echo off
setlocal

REM === Mono.Cecil.dll 위치: (NuGet에서 받아온 파일을 여기에 둬야 함) ===
set CECIL_DLL=%~dp0Mono.Cecil.dll

if not exist "%CECIL_DLL%" (
  echo [ERROR] Mono.Cecil.dll not found: %CECIL_DLL%
  echo Put Mono.Cecil.dll next to this bat file.
  pause
  exit /b 1
)

REM === csc 찾기 (Build Tools Roslyn) ===
set CSC=
for %%P in (
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
) do (
  if exist "%%~P" set CSC=%%~P
)

if "%CSC%"=="" (
  echo [ERROR] csc.exe not found. Install VS 2022 Build Tools with ".NET desktop build tools"
  pause
  exit /b 1
)

if not exist "..\..\output" mkdir "..\..\output"

"%CSC%" /target:exe /optimize+ ^
  /out:"..\..\output\Patcher.exe" ^
  /reference:"%CECIL_DLL%" ^
  Patcher.cs

if errorlevel 1 (
  echo [ERROR] build failed
  pause
  exit /b 1
)

echo [OK] Built: ..\..\output\Patcher.exe
pause
