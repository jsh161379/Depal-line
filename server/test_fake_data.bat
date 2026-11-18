@echo off
rem 테스트용 가짜 데이터 넣기 / 빼기 (DB 담당 2026-11-18). 실제 데이터와 재고는 그대로 남는다.
rem   test_fake_data.bat         넣기 (여러 번 실행해도 겹치지 않음)
rem   test_fake_data.bat remove  빼기
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
set OUT=logs\test_fake_data.txt
if defined AA_DB_PASSWORD set MYSQL_PWD=%AA_DB_PASSWORD%
set SQL=db\sql\test_fake_data.sql
if /i "%~1"=="remove" set SQL=db\sql\test_fake_data_remove.sql
echo === test_fake_data %~1 %date% %time% === > %OUT%
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < %SQL% >> %OUT% 2>&1
if errorlevel 1 goto fail
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection -e "SELECT 'runs(fake)' AS t, COUNT(*) AS n FROM runs WHERE source_run_id LIKE '%%-fa0%%' UNION ALL SELECT 'orders(all)', COUNT(*) FROM orders UNION ALL SELECT 'inquiries(all)', COUNT(*) FROM inquiries; SELECT product_id, stock_packs FROM stock ORDER BY product_id;" >> %OUT% 2>&1
type %OUT%
echo.
echo DONE. 결과는 logs\test_fake_data.txt
pause
exit /b 0
:fail
type %OUT%
echo.
echo FAILED - logs\test_fake_data.txt
pause
exit /b 1
