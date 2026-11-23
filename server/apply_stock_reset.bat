@echo off
rem ⑪-③ 배송 완료 재고 차감 끈 뒤 stock 음수를 0으로 (장성찬 DB_변경_2026-11-23_재고음수정리.sql과 같은 내용, 영문 이름 사본). 결과는 logs\stock_reset.txt
rem 반드시 새 mqtt_db_writer.py(DEDUCT_STOCK_ON_DELIVERY = False)로 DB 저장 프로그램을 다시 켠 뒤 실행할 것.
chcp 65001 >nul
cd /d "%~dp0"
set MYSQL="C:\Program Files\MySQL\MySQL Server 8.2\bin\mysql.exe"
if defined AA_DB_PASSWORD (
  set MYSQL_PWD=%AA_DB_PASSWORD%
  %MYSQL% -u root --default-character-set=utf8mb4 -t < db\sql\stock_reset_2026-11-23.sql > logs\stock_reset.txt 2>&1
) else (
  echo MySQL root 비밀번호를 물어보면 입력하세요.
  %MYSQL% -u root -p --default-character-set=utf8mb4 -t < db\sql\stock_reset_2026-11-23.sql > logs\stock_reset.txt
)
type logs\stock_reset.txt
pause
