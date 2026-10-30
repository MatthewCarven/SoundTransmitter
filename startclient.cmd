@echo off
rem Sound Transmitter client: run this on the LAPTOP.
rem Plays the desktop's sound on this machine's default output.
rem
rem Extra options pass straight through, e.g.  startclient.cmd --latency 60
rem If it stops with an error (the desktop is off, say) it tries again every
rem 5 seconds. Ctrl+C quits. A log goes to client.log beside this script.
setlocal
set SERVER=id10terror-desktop
set EXE=%~dp0st-recv.exe
if not exist "%EXE%" set EXE=%~dp0publish\st-recv\st-recv.exe
if not exist "%EXE%" (
    echo Can't find st-recv.exe beside this script or in publish\st-recv.
    pause
    exit /b 1
)
title Sound Transmitter client - playing from %SERVER%

:run
"%EXE%" --from %SERVER% --log "%~dp0client.log" %*
set CODE=%errorlevel%
if "%CODE%"=="0" exit /b 0
rem 2 is a mistake in the options: retrying won't help.
if "%CODE%"=="2" (
    pause
    exit /b 2
)
echo st-recv stopped with code %CODE%; trying again in 5 seconds. Ctrl+C quits.
rem ping, not timeout: timeout gives up at once when there's no console input.
ping -n 6 127.0.0.1 >nul
goto run
