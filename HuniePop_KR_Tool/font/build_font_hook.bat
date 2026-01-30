@echo off
setlocal

REM ==== 1) 게임 Managed 경로만 맞춰줘 ====
set GAME_MANAGED=D:\SteamLibrary\steamapps\common\HuniePop\HuniePop_Data\Managed

if not exist "%GAME_MANAGED%\UnityEngine.dll" (
  echo [ERROR] UnityEngine.dll not found: %GAME_MANAGED%\UnityEngine.dll
  pause
  exit /b 1
)

REM ==== csc 찾기 ====
set CSC=
for /f "delims=" %%P in ('where csc 2^>nul') do (
  set CSC=%%P
  goto :FOUND_CSC
)
for %%P in (
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
) do (
  if exist "%%~P" set CSC=%%~P
)
:FOUND_CSC

if "%CSC%"=="" (
  echo [ERROR] csc.exe not found.
  pause
  exit /b 1
)

echo [OK] Using CSC: %CSC%

if not exist "..\output" mkdir "..\output"

REM ==== HuniePop = 32bit (x86) ====
"%CSC%" ^
  /target:library ^
  /platform:x86 ^
  /optimize+ ^
  /out:"..\output\FontHook.dll" ^
  /reference:"%GAME_MANAGED%\UnityEngine.dll" ^
  FontHook.cs

if errorlevel 1 (
  echo [ERROR] Build failed.
  pause
  exit /b 1
)

echo [OK] Built: ..\output\FontHook.dll
pause
