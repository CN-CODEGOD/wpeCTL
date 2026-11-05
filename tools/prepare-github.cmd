@echo off
rem Prepare a local commit of wpectl for github.com/CN-CODEGOD/wpeCTL.
rem Stages under %TEMP% because git ref locking fails inside workspace paths here.
rem This script does NOT push - run the printed commands yourself so your
rem credentials only ever pass through git.
setlocal

set SRC=%~dp0..
set REPO=https://github.com/CN-CODEGOD/wpeCTL
set WORK=%TEMP%\wpeCTL-stage

set GIT=git
where git >nul 2>nul
if errorlevel 1 (
  if exist "C:\Program Files\Git\bin\git.exe" (
    set GIT="C:\Program Files\Git\bin\git.exe"
  ) else (
    echo git.exe not found - install Git for Windows
    exit /b 1
  )
)

if exist "%WORK%" rmdir /s /q "%WORK%"
%GIT% clone %REPO% "%WORK%"
if errorlevel 1 exit /b 1

rem A brand-new GitHub repo is empty and its default branch is main, while a
rem local clone of it may start on master - pin the branch before committing.
%GIT% -C "%WORK%" symbolic-ref HEAD refs/heads/main

xcopy /E /I /Y /Q "%SRC%\*" "%WORK%\" >nul
if errorlevel 1 exit /b 1
copy /y "%SRC%\.gitignore" "%WORK%\.gitignore" >nul
if exist "%WORK%\bin" rmdir /s /q "%WORK%\bin"

if "%GIT_AUTHOR_NAME%"=="" set GIT_AUTHOR_NAME=CN-CODEGOD
if "%GIT_AUTHOR_EMAIL%"=="" set GIT_AUTHOR_EMAIL=CN-CODEGOD@users.noreply.github.com

%GIT% -C "%WORK%" add -A
%GIT% -C "%WORK%" status --short
%GIT% -C "%WORK%" -c user.name="%GIT_AUTHOR_NAME%" -c user.email="%GIT_AUTHOR_EMAIL%" commit -m "Add wpectl: control Wallpaper Engine from C# or Lua"
if errorlevel 1 exit /b 1

echo.
%GIT% -C "%WORK%" log --oneline -3
echo.
echo Review, then push with your own credentials:
echo   cd /d "%WORK%"
echo   git push -u origin main
echo.
echo To discard:  rmdir /s /q "%WORK%"
