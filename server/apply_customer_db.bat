@echo off
rem 고객 정보 칸 + 팔레트 2x4 가짜 데이터 적용 (DB 담당 2026-11-20 변경)
rem   [1/3] 가짜 데이터 다시 넣기 (회차당 8개, 칸 1~8)  db\sql\test_fake_data.sql
rem   [2/3] db\sql\customer_info.sql (= db\DB_변경_2026-11-20_고객정보.sql 사본): orders에 customer_type / age_group / region 칸 추가 + 지역 채우기 + 가짜 고객 정보
rem         (가짜 데이터를 먼저 넣어야 가짜 주문에 고객 정보가 들어감)
rem   [3/3] 확인
rem 결과는 logs\apply_customer_db.txt. 다시 실행해도 안전하다 (칸이 있으면 건너뜀, 가짜 데이터는 지우고 다시 넣음).
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
set OUT=logs\apply_customer_db.txt
if defined AA_DB_PASSWORD set MYSQL_PWD=%AA_DB_PASSWORD%
if not defined AA_DB_PASSWORD echo MySQL root password is asked 3 times.
echo === apply_customer_db %date% %time% === > %OUT%
echo [1/3] fake data (2x4)
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\test_fake_data.sql >> %OUT% 2>&1
if errorlevel 1 goto fail
echo [2/3] customer columns
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\customer_info.sql >> %OUT% 2>&1
if errorlevel 1 goto fail
echo [3/3] check
%MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection -e "SELECT COUNT(*) AS fake_batteries, MAX(b.pallet_slot) AS max_slot FROM battery_cycles b JOIN runs r ON r.run_id = b.run_id WHERE r.source_run_id LIKE '%%-fa0%%'; SELECT customer_type, age_group, COUNT(*) AS n FROM orders GROUP BY customer_type, age_group ORDER BY customer_type, age_group; SELECT IFNULL(region,'(NULL)') AS region, COUNT(*) AS n FROM orders GROUP BY region ORDER BY n DESC;" >> %OUT% 2>&1
type %OUT%
echo.
echo DONE. Restart the server (stop_all.bat, then start_all.bat) so the new DB writer starts.
pause
exit /b 0
:fail
type %OUT%
echo.
echo FAILED - see logs\apply_customer_db.txt
pause
exit /b 1
