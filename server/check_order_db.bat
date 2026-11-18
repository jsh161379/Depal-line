@echo off
rem 주문 테이블 확인 (건수, 최근 주문·상태 기록·문의, 권한). 결과는 logs\check_order_db.txt 에도 남는다.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
if defined AA_DB_PASSWORD set MYSQL_PWD=%AA_DB_PASSWORD%
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\4_check.sql > logs\check_order_db.txt 2>&1
type logs\check_order_db.txt
pause
