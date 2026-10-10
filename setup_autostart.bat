@echo off
:: ============================================================
:: Setup Auto-Start for Monitoring Part Usage R24
:: Run this script as Administrator
:: ============================================================

echo ===========================================
echo  Monitoring Part Usage R24 - Auto-Start Setup
echo ===========================================
echo.

:: Get the directory where this script is located
set "SCRIPT_DIR=%~dp0"

:: Find the exe in bin\Release or bin\Debug
if exist "%SCRIPT_DIR%bin\Release\Monitoring Tutorial R24 - Autorun.exe" (
    set "EXE_PATH=%SCRIPT_DIR%bin\Release\Monitoring Tutorial R24 - Autorun.exe"
) else if exist "%SCRIPT_DIR%bin\Debug\Monitoring Tutorial R24 - Autorun.exe" (
    set "EXE_PATH=%SCRIPT_DIR%bin\Debug\Monitoring Tutorial R24 - Autorun.exe"
) else (
    echo ERROR: Could not find "Monitoring Tutorial R24 - Autorun.exe" in bin\Release or bin\Debug
    echo Please build the project first.
    pause
    exit /b 1
)

echo Found: %EXE_PATH%
echo.

:: Create the Task Scheduler task
echo Creating Windows Task Scheduler task...
schtasks /Create /TN "Monitoring Part Usage R24" /TR "\"%EXE_PATH%\"" /SC ONLOGON /DELAY 0000:30 /RL HIGHEST /F

if %ERRORLEVEL% EQU 0 (
    echo.
    echo =============================================
    echo  SUCCESS! Task created successfully.
    echo  Task Name: "Monitoring Part Usage R24"
    echo  Trigger: At Logon (30 second delay)
    echo  EXE: %EXE_PATH%
    echo =============================================
    echo.
    echo To manage this task, open Task Scheduler:
    echo   taskschd.msc
    echo.
    echo To remove this task later:
    echo   schtasks /Delete /TN "Monitoring Part Usage R24" /F
) else (
    echo.
    echo ERROR: Failed to create task.
    echo Make sure you are running this script as Administrator.
)

echo.
pause
