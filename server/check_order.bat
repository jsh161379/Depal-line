@echo off
rem 주문 하나 확인: 주문 상태, 연결된 회차(실제 DB에 있는 회차인지), 오늘 주문 수·매출(F4와 비교), 창고 stock 표.
rem 쓰는 법: check_order.bat BR-20261123-XXXX   (번호를 안 쓰면 가장 최근 주문)
rem 결과는 logs\check_order.txt 에도 남는다.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
set ORDER_NO=%~1
if defined AA_DB_PASSWORD (
  set MYSQL_PWD=%AA_DB_PASSWORD%
  %MYSQL% -u root --default-character-set=utf8mb4 -t aa_battery_inspection -e "SET @arg='%ORDER_NO%'; source db/sql/check_order.sql" > logs\check_order.txt 2>&1
) else (
  echo MySQL root 비밀번호를 물어보면 입력하세요.
  %MYSQL% -u root -p --default-character-set=utf8mb4 -t aa_battery_inspection -e "SET @arg='%ORDER_NO%'; source db/sql/check_order.sql" > logs\check_order.txt
)
type logs\check_order.txt
pause
