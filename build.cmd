@echo off
rem Kospet Map Maker build. Same options as build.ps1:
rem   build.cmd                      tests + publish win-x64 and linux-x64
rem   build.cmd -SkipTests           publish only
rem   build.cmd -Runtimes win-x64    one platform
rem   build.cmd -Release             + tag and GitHub release (needs gh)
rem   build.cmd -Version 1.1.0 -Release
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
