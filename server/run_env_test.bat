@echo off
rem 환경 센서 시험 프로그램 실행 (예전에 켜 둔 env_bridge_test.py가 있으면 먼저 끔)
rem run_all 등 다른 프로그램은 건드리지 않음
chcp 65001 >nul
cd /d C:\depal\server
echo [1/2] Stopping old env_bridge_test.py ...
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'python*' -and $_.CommandLine -like '*env_bridge_test*' } | ForEach-Object { Write-Host ('  stopped PID ' + $_.ProcessId); Stop-Process -Id $_.ProcessId -Force }"
timeout /t 2 >nul
echo [2/2] Starting env_bridge_test.py on COM4 ...
python env_bridge_test.py --port COM4
pause
