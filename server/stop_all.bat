@echo off
REM stop_all.bat - stop everything started by start_all.bat or start_all.vbs
cd /d %~dp0
python run_all.py --stop
timeout /t 3 >nul
