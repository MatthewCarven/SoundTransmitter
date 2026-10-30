@echo off
rem Laptop: play the desktop's sound on this machine's default output.
rem Extra options pass straight through, e.g.  listen.cmd --latency 80
"%~dp0publish\st-recv\st-recv.exe" --from id10terror-desktop %*
