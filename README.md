# 디팔레타이저 라인 — AA 건전지 검사·분류

학원 최종 프로젝트 「디팔레타이저를 위한 영상처리와 신경망 알고리즘 개발」 (2026.11.12 ~ 12.17, 6명)

팔레트의 AA 건전지를 로봇팔이 하나씩 꺼내, 카메라(YOLO) 외관·극 방향 검사와 전압·내부저항 측정을 거쳐
A / B / 불량 / 재분류로 나눈다.

서버와 각 장치의 대표 프로그램(라즈베리파이, YOLO, AGV 중계, 관리자 프로그램)은 MQTT 브로커(Mosquitto) 로 메시지를 주고받는다. 하드웨어끼리(라즈베리파이↔아두이노 시리얼, AGV 블루투스 등)의 통신은 하드웨어 담당이 정한다.

## 폴더

| 폴더 | 내용 | 언어 |
|---|---|---|
| `server/` | 중앙 서버(장치 감시·하트비트), 라인 컨트롤러(지휘·판정), 가짜 장치, 콘솔 모니터, DB 저장 프로그램 | Python |
| `server/docs/` | **통신 규약 v1.0**, 메시지 형식 (모든 담당이 기준으로 삼는 문서) | |
| `admin/` | 관리자 프로그램 (팀 6명이 화면별로 나눠 개발) — 분담·규칙은 `admin/README.md` | C# WPF |

## 처음 받았을 때

**공통 준비**
- Mosquitto 설치: https://mosquitto.org/download/ (기본 경로 `C:\Program Files\mosquitto`)
- Python 3.11 이상 → `server` 폴더에서 `python -m pip install -r requirements.txt`
- Visual Studio 2022 (.NET 8, WPF)

**서버 없이 화면만 개발할 때**
- `admin/DepalAdmin.sln` 열기 → F5. 기본 설정(`settings.json`)이 가짜 데이터 모드라 브로커가 없어도 동작한다
- 처음 계정: `admin` / `admin1234` (첫 로그인 때 새 비밀번호를 정함)

**가짜 장치로 라인 전체를 돌려 볼 때**
1. `server/start_demo.bat` 더블클릭 → 브로커·서버·컨트롤러·모니터·가짜 라파·가짜 YOLO 창이 열림
2. `7 COMMAND` 창에서 `python line_cmd.py start`
3. 관리자 프로그램을 실제 연결로 보려면 실행 폴더 `admin\src\DepalAdmin.Shell\bin\Debug\net8.0-windows`에
   `settings.local.json`을 만든다. 이 파일이 있으면 `settings.json` 대신 읽으므로 **모든 값을 다 적어야 한다**

```json
{
  "StationName": "DEPAL-01",
  "UseFakeMqtt": false,
  "UseFakeDb": true,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "ClientId": "ui",
  "MySqlConnection": "Server=localhost;Port=3306;Database=aa_battery_inspection;User ID=ui_reader;Password=여기에_비밀번호;",
  "MaxRowsInMemory": 500,
  "AutoLogoutMinutes": 10,
  "AlarmSound": true,
  "DataFolder": "data"
}
```
   - `UseFakeMqtt`: `false` = 실제 브로커 연결 (라인 현황·장치 상태·검사 결과·조작 버튼)
   - `UseFakeDb`: `false` = 실제 MySQL 연결 (이력 조회·통계·보고서). 아래 "DB까지" 준비 후

**DB까지 저장해 볼 때** (MySQL 8 필요)
1. `server/db/teamproject.sql`로 DB 만들기
2. `server/start_db_test.bat` → `5 DB WRITER` 창에 MySQL 비밀번호 입력
3. 확인: `server/check_db.bat`
4. 관리자 프로그램이 DB를 읽게 하려면 **읽기 전용 계정**을 만든다 (root로 접속 후, 비밀번호는 각자 정함)
   ```sql
   CREATE USER 'ui_reader'@'localhost' IDENTIFIED BY '비밀번호';
   GRANT SELECT ON aa_battery_inspection.* TO 'ui_reader'@'localhost';
   ```
   그다음 `settings.local.json`에서 `"UseFakeDb": false`, `Password=`에 그 비밀번호 → 관리자 프로그램 다시 실행

**다른 PC에서 서버 PC의 라인·DB에 붙을 때** (같은 학원 네트워크)
1. 서버 PC에서 브로커(`start_demo.bat` 또는 `start_db_test.bat`)를 켜 둔다. MySQL은 서비스라 항상 켜져 있음
2. 다른 PC의 PowerShell에서 연결 확인 (둘 다 `TcpTestSucceeded : True`여야 함)
   ```
   Test-NetConnection <서버PC IP> -Port 3306
   Test-NetConnection <서버PC IP> -Port 1883
   ```
3. 관리자 프로그램 → **설정** 탭에서
   - 가짜 MQTT 사용·가짜 DB 사용 **체크 해제**
   - 브로커 주소: `<서버PC IP>`
   - MySQL 연결: `Server=<서버PC IP>;Port=3306;Database=aa_battery_inspection;User ID=ui_reader;Password=<비밀번호>;`
   - **저장** → 프로그램 다시 시작
4. 서버 PC IP와 비밀번호는 통신 담당에게 따로 받는다 (README에 적지 않음)
- MQTT 접속 이름은 `ui-PC이름`으로 자동으로 정해진다. 같은 이름으로 두 대가 붙으면 서로 끊기므로 `ClientId`는 비워 둘 것
- 서버 PC 쪽 준비(이미 되어 있음): 네트워크용 읽기 전용 계정 `ui_reader@'192.168.0.%'`, 방화벽 3306·1883 허용
- 학원 PC는 IP가 바뀔 수 있다. 접속이 안 되면 서버 PC에서 `ipconfig`로 IP부터 확인

## 규칙

- **자기 담당 폴더만 고친다.** 공통 파일(`admin/src/DepalAdmin.Core`, `DepalAdmin.Comm`, `DepalAdmin.Shell`, `server/`)과 통신 규약은 통신 담당이 관리한다. 바꿔야 하면 먼저 말하기
- **메시지 형식은 `server/docs/통신_규약_v1.0.md`가 기준.** 토픽·필드 이름을 임의로 바꾸지 않는다
- **비밀번호·개인 설정은 올리지 않는다.** `settings.local.json`, `data/` 폴더는 `.gitignore`에 들어 있음. MySQL 비밀번호는 코드에 쓰지 말고 실행할 때 입력
- 올리기 전에 빌드가 되는지 확인 (Visual Studio: 빌드 → 솔루션 빌드)
- **작업 시작 전 풀, 작업 끝나면 커밋 → 푸시** (Visual Studio: Git → 풀 / Git → 커밋 또는 스태시 → 푸시). 풀을 안 하고 푸시하면 충돌 오류가 난다
