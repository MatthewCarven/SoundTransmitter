@echo off
rem Sound Transmitter server: run this on the DESKTOP.
rem Sends whatever the default playback device plays to the laptop. Set the
rem desktop's default output to "Speakers (3- High Definition Audio Device)",
rem the dead jack, and the desktop itself stays silent.
rem
rem Extra options pass straight through, e.g.  startserver.cmd --device "LG TV"
rem If it stops with an error (the laptop is off, say) it tries again every
rem 5 seconds. Ctrl+C quits. A log goes to server.log beside this script.
setlocal
set CLIENT=ID10TError-Laptop1
set EXE=%~dp0st-send.exe
if not exist "%EXE%" set EXE=%~dp0publish\st-send\st-send.exe
if not exist "%EXE%" (
    echo Can't find st-send.exe beside this script or in publish\st-send.
    pause
    exit /b 1
)
title Sound Transmitter server - sending to %CLIENT%

:run
"%EXE%" --to %CLIENT% --log "%~dp0server.log" %*
set CODE=%errorlevel%
if "%CODE%"=="0" exit /b 0
rem 2 is a mistake in the options: retrying won't help.
if "%CODE%"=="2" (
    pause
    exit /b 2
)
echo st-send stopped with code %CODE%; trying again in 5 seconds. Ctrl+C quits.
rem ping, not timeout: timeout gives up at once when there's no console input.
ping -n 6 127.0.0.1 >nul
goto run
