@echo off
rem 팔레트 2×3 칸 번호 확인 (통신 규약 v1.5.3): 최근 회차 2개의 pallet_slot. 결과는 logs\check_slots.txt 에도 남는다.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
if defined AA_DB_PASSWORD (
  set MYSQL_PWD=%AA_DB_PASSWORD%
  %MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\check_slots.sql > logs\check_slots.txt 2>&1
) else (
  echo MySQL root 비밀번호를 물어보면 입력하세요.
  %MYSQL% -u root -p --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\check_slots.sql > logs\check_slots.txt
)
type logs\check_slots.txt
pause
