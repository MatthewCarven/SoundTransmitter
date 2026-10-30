@echo off
rem Desktop: send whatever the default playback device plays to the laptop.
rem Set the default to "Speakers (3- High Definition Audio Device)" (the dead
rem jack) and the desktop itself stays silent; everything goes to the laptop.
rem Extra options pass straight through, e.g.  send.cmd --device "LG TV"
"%~dp0st-send.exe" --to ID10TError-Laptop1 %*
