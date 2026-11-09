@echo off
REM ============================================================
REM  start_all.bat - start every server program in ONE window
REM  This window shows the monitor. Other programs run without
REM  a window and write to logs\<name>.log
REM  Stop: Ctrl+C here (answer Y if asked), or stop_all.bat
REM  Options: start_all.bat --no-fake   (real devices)
REM           start_all.bat --no-db     (no MySQL)
REM           start_all.bat --estop-test
REM ============================================================
cd /d %~dp0
title DEPAL SERVER
python run_all.py %*
pause
