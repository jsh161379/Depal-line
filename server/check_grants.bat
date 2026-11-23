@echo off
rem ui_reader 계정의 grading_rules 읽기 권한 확인 (읽기만 함). 결과는 logs\check_grants.txt 에도 남는다.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
if defined AA_DB_PASSWORD (
  set MYSQL_PWD=%AA_DB_PASSWORD%
  %MYSQL% -u root --default-character-set=utf8mb4 -t < db\sql\check_grants.sql > logs\check_grants.txt 2>&1
) else (
  echo MySQL root 비밀번호를 물어보면 입력하세요.
  %MYSQL% -u root -p --default-character-set=utf8mb4 -t < db\sql\check_grants.sql > logs\check_grants.txt
)
type logs\check_grants.txt
pause
