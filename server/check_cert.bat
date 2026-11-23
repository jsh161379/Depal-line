@echo off
rem 성적서 발급 시험 확인 (읽기만 함): 최근 회차 연결 주문의 성적서 번호, 연결 회차별 A 수·판정 기준 버전. 결과는 logs\check_cert.txt 에도 남는다.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
if defined AA_DB_PASSWORD (
  set MYSQL_PWD=%AA_DB_PASSWORD%
  %MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\check_cert.sql > logs\check_cert.txt 2>&1
) else (
  echo MySQL root 비밀번호를 물어보면 입력하세요.
  %MYSQL% -u root -p --default-character-set=utf8mb4 -t aa_battery_inspection < db\sql\check_cert.sql > logs\check_cert.txt
)
type logs\check_cert.txt
pause
