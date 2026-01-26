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

REM ==== 3) csc 찾기 (where 우선, 없으면 VS BuildTools Roslyn 경로) ====
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
  echo - In Build Tools installer, select ".NET desktop build tools"
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
