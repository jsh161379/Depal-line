@echo off
rem 주문 관리 DB 적용 (2026-11-17): 사전 정리 -> 주문 테이블 7개 -> 읽기 계정 권한 -> 확인
rem 결과는 logs\apply_order_db.txt 에도 남는다. 다시 실행해도 안전하다 (이미 있으면 건너뜀).
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
set OUT=logs\apply_order_db.txt
if defined AA_DB_PASSWORD set MYSQL_PWD=%AA_DB_PASSWORD%
if not defined AA_DB_PASSWORD echo MySQL root password is asked 4 times.
echo === apply_order_db %date% %time% === > %OUT%
echo [1/4] prep
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\1_prep.sql >> %OUT% 2>&1
if errorlevel 1 goto fail
echo [2/4] order tables
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\2_order_tables.sql >> %OUT% 2>&1
if errorlevel 1 goto fail
echo [3/4] grants for ui_reader (errors for a missing host are expected)
%MYSQL% -u root --default-character-set=utf8mb4 --force aa_battery_inspection < db\sql\3_grants.sql >> %OUT% 2>&1
echo [4/4] check
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\4_check.sql >> %OUT% 2>&1
type %OUT%
echo.
echo DONE. Restart the server (stop_all.bat, then run_all) so the new DB writer starts.
pause
exit /b 0
:fail
type %OUT%
echo.
echo FAILED - see logs\apply_order_db.txt
pause
exit /b 1
