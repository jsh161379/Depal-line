@echo off
rem 라인 A 등급 확인: 최근 회차 A 개수, 주문 관리 "라인 A 남음"과 같아야 하는 값, 판정 기준. 결과는 logs\check_line_a.txt 에도 남는다.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
if defined AA_DB_PASSWORD (
  set MYSQL_PWD=%AA_DB_PASSWORD%
  %MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\check_line_a.sql > logs\check_line_a.txt 2>&1
) else (
  echo MySQL root 비밀번호를 물어보면 입력하세요.
  %MYSQL% -u root -p --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\check_line_a.sql > logs\check_line_a.txt
)
type logs\check_line_a.txt
pause
