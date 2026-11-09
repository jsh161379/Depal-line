@echo off
REM ============================================================
REM  start_demo.bat - full line demo with fake devices
REM  broker -> server -> controller -> monitor -> fake pi -> fake yolo
REM  Put this file in C:\mqtt_server and double-click it.
REM  Then type in the "7 COMMAND" window:  python line_cmd.py start
REM ============================================================
cd /d %~dp0

start "1 BROKER" cmd /k ""C:\Program Files\mosquitto\mosquitto.exe" -c mosquitto.conf -v"
timeout /t 2 /nobreak >nul
start "2 SERVER" cmd /k "python central_server.py"
timeout /t 2 /nobreak >nul
start "3 CONTROLLER" cmd /k "python line_controller.py"
start "4 MONITOR" cmd /k "python monitor.py"
timeout /t 1 /nobreak >nul
start "5 FAKE PI" cmd /k "python fake_pi.py"
start "6 FAKE YOLO" cmd /k "python fake_yolo.py"
timeout /t 3 /nobreak >nul
start "7 COMMAND" cmd /k "echo Type: python line_cmd.py start   (pause / resume / skip / stop / estop / reset)"
