@echo off
rem 11/20 이전 판정 기준(temp-2026-11-10)의 비어 있는 기준값 채우기 (한 번만 실행). 결과는 logs\fill_old_rule.txt 에도 남는다.
rem 기준값이 이미 있으면 아무것도 바꾸지 않으므로 두 번 실행해도 안전하다.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
if defined AA_DB_PASSWORD (
  set MYSQL_PWD=%AA_DB_PASSWORD%
  %MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\fill_old_rule.sql > logs\fill_old_rule.txt 2>&1
) else (
  echo MySQL root 비밀번호를 물어보면 입력하세요.
  %MYSQL% -u root -p --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\fill_old_rule.sql > logs\fill_old_rule.txt
)
type logs\fill_old_rule.txt
pause
