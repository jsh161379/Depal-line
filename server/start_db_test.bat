@echo off
REM ============================================================
REM  start_db_test.bat - line + DB integration test (no E-STOP)
REM  For the E-STOP test use start_estop_test.bat
REM  broker -> server -> controller -> monitor -> DB writer -> fake pi/yolo
REM  1) Close every old cmd window first.
REM  2) Double-click this file.
REM  3) In "5 DB WRITER" type the MySQL password, wait for "Subscribed".
REM  4) In "8 COMMAND":  python line_cmd.py start
REM  5) In "8 COMMAND":  check_db.bat   (row counts per table)
REM ============================================================
cd /d %~dp0

start "1 BROKER" cmd /k ""C:\Program Files\mosquitto\mosquitto.exe" -c mosquitto.conf"
timeout /t 2 /nobreak >nul
start "2 SERVER" cmd /k "python central_server.py"
timeout /t 2 /nobreak >nul
start "3 CONTROLLER" cmd /k "python line_controller.py"
start "4 MONITOR" cmd /k "python monitor.py"
start "5 DB WRITER" cmd /k "cd db && python mqtt_db_writer.py"
timeout /t 1 /nobreak >nul
start "6 FAKE PI" cmd /k "python fake_pi.py"
start "7 FAKE YOLO" cmd /k "python fake_yolo.py"
timeout /t 2 /nobreak >nul
start "8 COMMAND" cmd /k "echo 1) type the password in 5 DB WRITER   2) python line_cmd.py start   3) check_db.bat"
