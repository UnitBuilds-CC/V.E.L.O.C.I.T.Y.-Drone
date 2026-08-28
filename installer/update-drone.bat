@echo off
echo Velocity Drone Self-Updater
echo ===========================
echo.

set DRONE_DIR=C:\Drone
set NEW_EXE=%DRONE_DIR%\share\velocity-drone-new.exe
set OLD_EXE=%DRONE_DIR%\velocity-drone.exe
set BACKUP_EXE=%DRONE_DIR%\velocity-drone.backup.exe

echo Waiting for drone process to stop...
:waitloop
tasklist /fi "imagename eq velocity-drone.exe" 2>NUL | find /I /N "velocity-drone.exe" >NUL
if %errorlevel%==0 (
    timeout /t 1 /nobreak >NUL
    goto waitloop
)
echo Drone stopped.
timeout /t 2 /nobreak >NUL

echo Backing up current version...
if exist "%OLD_EXE%" (
    copy /Y "%OLD_EXE%" "%BACKUP_EXE%" >NUL
    echo Backup created: %BACKUP_EXE%
)

echo Installing new version...
if exist "%NEW_EXE%" (
    copy /Y "%NEW_EXE%" "%OLD_EXE%" >NUL
    del "%NEW_EXE%" >NUL
    echo New version installed.
) else (
    echo ERROR: New exe not found at %NEW_EXE%
    echo Restoring backup...
    if exist "%BACKUP_EXE%" copy /Y "%BACKUP_EXE%" "%OLD_EXE%" >NUL
    pause
    exit /b 1
)

echo Starting drone...
cd /d "%DRONE_DIR%"
start "" "%DRONE_DIR%\run-drone.bat"

echo Update complete!
timeout /t 3 /nobreak >NUL