@echo off
setlocal

REM ==== 1) 여기만 너 게임 경로로 맞춰줘 (한 번만) ====
set GAME_MANAGED=D:\SteamLibrary\steamapps\common\HuniePop\HuniePop_Data\Managed

REM ==== 2) 필수 DLL 체크 ====
if not exist "%GAME_MANAGED%\UnityEngine.dll" (
  echo [ERROR] UnityEngine.dll not found: %GAME_MANAGED%\UnityEngine.dll
  pause
  exit /b 1
)

REM (선택) Assembly-CSharp.dll 체크 (참조는 안 걸어도 되지만, 환경 확인용)
if not exist "%GAME_MANAGED%\Assembly-CSharp.dll" (
  echo [WARN] Assembly-CSharp.dll not found: %GAME_MANAGED%\Assembly-CSharp.dll
)

REM ==== 3) csc 찾기 ====
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
  echo - Install Visual Studio Build Tools and select ".NET desktop build tools"
  pause
  exit /b 1
)

echo [OK] Using CSC: %CSC%

REM ==== 4) 출력 폴더 ====
if not exist "..\output" mkdir "..\output"

REM ==== 5) 빌드 (HuniePop = 32bit 이므로 x86) ====
"%CSC%" ^
  /target:library ^
  /platform:x86 ^
  /optimize+ ^
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
