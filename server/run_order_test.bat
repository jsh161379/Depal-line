@echo off
rem 홈페이지(주문) 연결 시험: 홈페이지용 웹 서버(8080) + 가짜 주문 관리 프로그램을 켠다.
rem 먼저 실행기(run_all)로 브로커를 켜 둘 것. 실제 주문 관리 프로그램(C#)과 동시에 쓰지 말 것.
rem 홈페이지 주소: http://localhost:8080/bareun_site_v7.html
chcp 65001 >nul
cd /d "%~dp0"
start "homepage web server 8080" python -m http.server 8080 --bind 127.0.0.1 --directory web
start "fake order admin" python fake_order_admin.py
