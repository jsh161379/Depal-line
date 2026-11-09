@echo off
REM ============================================================
REM  start_line.bat - broker, server, monitor in order
REM  Put this file in C:\mqtt_server and double-click it.
REM  Each program opens in its own window. Close a window to stop it.
REM ============================================================
cd /d %~dp0

REM 1) MQTT broker (Mosquitto) - must be first
start "1 BROKER" cmd /k ""C:\Program Files\mosquitto\mosquitto.exe" -c mosquitto.conf -v"
timeout /t 2 /nobreak >nul

REM 2) Central server
start "2 SERVER" cmd /k "python central_server.py"
timeout /t 2 /nobreak >nul

REM 3) Line monitor (readable view for capture)
start "3 MONITOR" cmd /k "python monitor.py"

REM Fake devices are started by hand when needed, for example:
REM   python fake_device.py pi
REM   python fake_device.py yolo --freeze 10
REM   python fake_device.py agv --crash 10
REM   python sample_results.py
