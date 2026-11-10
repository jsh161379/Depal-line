# 서버 UI (DEPAL 서버 관리)

서버 관리자용 창. C++17 + Qt 6.11 (Widgets, Network), CMake. 어두운 Windows 관리 도구 모양, 색은 상태에만 사용.
작업자용 화면(라인 현황·결과·통계)은 `admin/`의 관리자 프로그램이 맡는다.

| 부분 | 내용 |
|---|---|
| 창 제목 | 서버 PC 이름과 IP |
| 도구 모음 | 전부 켜기(F9)·전부 끄기 / 고른 프로그램 켜기·끄기 / 로그 열기(메모장) / 가짜 장치 포함 |
| 목록 | 브로커·중앙 서버·컨트롤러·DB 저장·가짜 장치의 상태, PID, 가동 시간, 종료 코드 |
| 아래 상세 | 고른 프로그램의 정보와 로그 마지막 부분 (1초마다 이어 읽기, 오류 줄 빨강) |
| 상태 줄 | 서버 가동 시간, MQTT 1883·MySQL 3306 연결, 알림 |

예정: 접속 중인 장치·하트비트 그래프, 토픽별 메시지 감시, 남은 기록(retained) 지우기 → 만들 때 탭으로 추가

## 구조
- 서버 UI는 프로그램을 직접 켜지 않고 `server/run_all.py`(실행기)에 부탁만 한다. 서버 UI가 꺼져도 서버와 라인은 계속 돈다.
  - 읽기: `server/logs/run_all.status.json` (실행기가 1초마다 씀)
  - 쓰기: `server/logs/run_all.request.json` (`{"id", "action": start|stop|restart, "name"}`)
  - 전부 켜기: `python run_all.py --hidden` (창 없이, 서버 UI와 별도 프로세스), 전부 끄기: `python run_all.py --stop`
- 수정할 값: `src/AppConfig.h` (서버 폴더, python, 브로커 주소)

## 빌드
1. Qt Creator → File → Open File or Project → `server-ui/CMakeLists.txt`
2. Kit: Desktop Qt 6.11.3 MSVC2022 64bit → Configure Project
3. 왼쪽 아래 망치(빌드) → 초록 삼각형(실행)
