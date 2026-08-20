@echo off
REM ---------------------------------------------------------------------------
REM Incremental update deployment for GovBudget (IIS / ASP.NET Core Module).
REM
REM   deploy-update.cmd  <site-folder>
REM   deploy-update.cmd  \\server\c$\inetpub\govbudget
REM   deploy-update.cmd  E:\sites\govbudget
REM
REM What it does:
REM   1. publishes into .\publish  (Release),
REM   2. takes the site offline with app_offline.htm so the DLLs are not locked,
REM   3. copies ONLY files whose size or timestamp differs (robocopy),
REM   4. brings the site back online.
REM
REM What it never touches on the server:
REM   App_Data\                    - data-protection key ring; overwriting it signs everyone out
REM   logs\                        - stdout logs
REM   appsettings.Production.json  - live connection string / secrets
REM   web.config                   - see the note at the bottom
REM ---------------------------------------------------------------------------

setlocal
set "SITE=%~1"
if "%SITE%"=="" (
    echo Usage: deploy-update.cmd ^<site-folder^>
    echo    e.g. deploy-update.cmd \\server\c$\inetpub\govbudget
    exit /b 1
)
if not exist "%SITE%\" (
    echo Site folder not found: %SITE%
    exit /b 1
)

set "HERE=%~dp0"
set "OUT=%HERE%publish"

echo.
echo === 1/4  Publishing to %OUT%
dotnet publish "%HERE%GovBudget.csproj" -c Release -o "%OUT%" || exit /b 1

echo.
echo === 2/4  Taking the site offline
> "%SITE%\app_offline.htm" echo ^<html^>^<body^>^<h2^>GovBudget is being updated. Please try again in a minute.^</h2^>^</body^>^</html^>
REM Give IIS a moment to shut the worker process down and release the DLLs.
timeout /t 5 /nobreak >nul

echo.
echo === 3/4  Copying changed files only
robocopy "%OUT%" "%SITE%" /MIR /FFT /R:3 /W:2 /NP /NDL ^
    /XD "%SITE%\App_Data" "%SITE%\logs" ^
    /XF app_offline.htm appsettings.Production.json appsettings.Local.json web.config
set RC=%ERRORLEVEL%
REM robocopy: 0-7 = success (0 = nothing to copy), 8+ = real failure.
if %RC% GEQ 8 (
    echo robocopy failed with code %RC% - site left OFFLINE on purpose. Fix, then re-run.
    exit /b %RC%
)

echo.
echo === 4/4  Bringing the site back online
del "%SITE%\app_offline.htm"

echo.
echo Done. Only changed files were transferred (robocopy exit code %RC%).
echo.
echo NOTE about web.config:
echo   web.config is now part of the project, so publish keeps your settings instead of
echo   regenerating the file. It is excluded above only so this script cannot overwrite the
echo   copy that is currently live. Once the repository web.config matches the server, delete
echo   "web.config" from the /XF list and it will be deployed like any other file.
endlocal
