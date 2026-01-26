@echo off
setlocal

REM ==== 1) 여기만 너 게임 경로로 맞춰줘 (한 번만) ====
set GAME_MANAGED=D:\SteamLibrary\steamapps\common\HuniePop\HuniePop_Data\Managed

REM ==== 2) UnityEngine.dll 경로 체크 ====
if not exist "%GAME_MANAGED%\UnityEngine.dll" (
  echo [ERROR] UnityEngine.dll not found: %GAME_MANAGED%\UnityEngine.dll
  pause
  exit /b 1
)

REM ==== 3) csc 찾기 (.NET Framework csc 우선) ====
set CSC=
for %%P in (
  "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
  "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) do (
  if exist "%%~P" set CSC=%%~P
)

if "%CSC%"=="" (
  echo [ERROR] csc.exe not found. (Need .NET Framework installed)
  echo - Usually Windows has it, but if missing, install .NET Framework 4.x.
  pause
  exit /b 1
)

echo [OK] Using CSC: %CSC%

REM ==== 4) 빌드 출력 ====
if not exist "..\output" mkdir "..\output"

"%CSC%" /target:library /optimize+ ^
  /out:"..\output\KRHook.dll" ^
  /reference:"%GAME_MANAGED%\UnityEngine.dll" ^
  KRHook.cs

if errorlevel 1 (
  echo [ERROR] Build failed.
  pause
  exit /b 1
)

echo [OK] Built: ..\output\KRHook.dll
pause
