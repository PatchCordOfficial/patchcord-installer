@echo off
REM ── Patchcord Installer – build script ───────────────────────────────────────
REM  Requires: .NET 8 SDK  https://dotnet.microsoft.com/download/dotnet/8.0
REM  Run from the folder containing all source files.
REM  Output: bin\publish\PatchcordInstaller.exe

echo [BUILD] Restoring packages...
dotnet restore PatchcordInstaller.csproj --nologo
if errorlevel 1 goto :fail

echo [BUILD] Publishing single-file exe...
dotnet publish PatchcordInstaller.csproj -c Release -o bin\publish --nologo
if errorlevel 1 goto :fail

echo.
echo [OK] Done!  bin\publish\PatchcordInstaller.exe
start "" "bin\publish"
goto :eof

:fail
echo.
echo [FAIL] Build failed. See errors above.
pause
