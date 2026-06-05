@echo off
setlocal

if "%KSPDIR%"=="" goto :no_kspdir

if not exist "Release\ReRootMod.zip" (
    echo ERROR: Release\ReRootMod.zip not found - run build.bat first
    exit /b 1
)

echo =====================================
echo Removing existing Mod folder
echo =====================================

if exist "%KSPDIR%\GameData\ReRootMod" rmdir /s /q "%KSPDIR%\GameData\ReRootMod"
if errorlevel 1 (
    echo ERROR: Failed to remove the Mod folder
    exit /b 1
)

echo.
echo =====================================
echo Unzipping Mod
echo =====================================

powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -Path 'Release\ReRootMod.zip' -DestinationPath '%KSPDIR%\GameData\ReRootMod' -Force"
if errorlevel 1 (
    echo ERROR: Failed to unzip the Mod
    exit /b 1
)

echo.
echo Mod installed
echo.
echo Run at: %date% %time%
exit /b 0

:no_kspdir
echo ERROR: KSPDIR is not set - set it to your Kerbal Space Program install path
exit /b 1
