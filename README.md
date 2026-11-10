# 디팔레타이저 라인 — AA 건전지 검사·분류

학원 최종 프로젝트 「디팔레타이저를 위한 영상처리와 신경망 알고리즘 개발」 (2026.11.12 ~ 12.17, 6명)

팔레트의 AA 건전지를 로봇팔이 하나씩 꺼내, 카메라(YOLO) 외관·극 방향 검사와 전압·내부저항 측정을 거쳐
A / B / 불량 / 재분류로 나눈다.

서버와 각 장치의 대표 프로그램(라즈베리파이, YOLO, AGV 중계, 관리자 프로그램)은 MQTT 브로커(Mosquitto) 로 메시지를 주고받는다. 하드웨어끼리(라즈베리파이↔아두이노 시리얼, AGV 블루투스 등)의 통신은 하드웨어 담당이 정한다.

## 폴더

| 폴더 | 내용 | 언어 |
|---|---|---|
| `server/` | 중앙 서버(장치 감시·하트비트), 라인 컨트롤러(지휘·판정), 가짜 장치, 콘솔 모니터, DB 저장 프로그램 | Python |
| `server/docs/` | **통신 규약 v1.2**, 메시지 형식 v1.4 (모든 담당이 기준으로 삼는 문서) | |
| `admin/` | 관리자 프로그램 (팀 6명이 화면별로 나눠 개발) — 분담·규칙은 `admin/README.md` | C# WPF |

## 처음 받았을 때

**공통 준비**
- Mosquitto 설치: https://mosquitto.org/download/ (기본 경로 `C:\Program Files\mosquitto`)
- Python 3.11 이상 → `server` 폴더에서 `python -m pip install -r requirements.txt`
- Visual Studio 2022 (.NET 8, WPF)

**서버 없이 화면만 개발할 때**
- `admin/DepalAdmin.sln` 열기 → F5. 기본 설정(`settings.json`)이 가짜 데이터 모드라 브로커가 없어도 동작한다
- 처음 계정: `admin` / `admin1234` (첫 로그인 때 새 비밀번호를 정함)

**서버 켜고 끄기** (`server` 폴더)

| 파일 | 하는 일 |
|---|---|
| `start_all.bat` | 창 **1개**로 켜기. 그 창에 모니터가 뜨고, 나머지는 창 없이 실행. 시작할 때 MySQL 비밀번호를 한 번 물어봄. 끄기는 그 창에서 Ctrl+C |
| `start_all.vbs` | 창 **없이** 켜기. 켜지면 알림 창이 한 번 뜸. 상태는 관리자 프로그램에서 확인 |
| `stop_all.bat` | 어느 쪽으로 켰든 전부 끄기 |
| `check_db.bat` | DB 표별 건수 확인 |

- 켜지는 것: 브로커 → 중앙 서버 → 라인 컨트롤러 → DB 저장 → 가짜 라파(로봇팔 1)·가짜 Mega(벨트·측정·로봇팔 2)·가짜 YOLO
- 각 프로그램의 출력: `server/logs/<이름>.log` (지난번 것은 `<이름>.prev.log`). 하나가 꺼지면 "○○ 종료됨 → logs\○○.log 확인"이 뜬다
- 라인 시작: 관리자 프로그램의 시작 버튼 (또는 다른 cmd에서 `python line_cmd.py start`)
- 옵션은 cmd에서 붙인다: `start_all.bat --no-fake`(실제 장치를 붙일 때), `--no-db`(MySQL 없이), `--estop-test`(40초 뒤 가짜 Mega가 비상정지 버튼을 누름)
- `start_all.vbs`는 비밀번호를 물을 창이 없으므로 MySQL 비밀번호를 한 번 저장해 둬야 한다: cmd에서 `setx AA_DB_PASSWORD <비밀번호>` (이 PC 사용자 계정에만 저장됨).
- 이미 켜져 있으면 다시 켜지지 않는다. 다시 켜려면 `stop_all.bat` 먼저

**가짜 장치로 라인 전체를 돌려 볼 때**
1. `server/start_all.bat` 더블클릭 → MySQL 비밀번호 입력 (DB 없이 보려면 cmd에서 `start_all.bat --no-db`)
2. 관리자 프로그램의 시작 버튼, 또는 다른 cmd에서 `python line_cmd.py start`
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
2. `server/start_all.bat` → MySQL 비밀번호 입력 → 라인 시작
3. 확인: `server/check_db.bat`
4. 관리자 프로그램이 DB를 읽게 하려면 **읽기 전용 계정**을 만든다 (root로 접속 후, 비밀번호는 각자 정함)
   ```sql
   CREATE USER 'ui_reader'@'localhost' IDENTIFIED BY '비밀번호';
   GRANT SELECT ON aa_battery_inspection.* TO 'ui_reader'@'localhost';
   ```
   그다음 `settings.local.json`에서 `"UseFakeDb": false`, `Password=`에 그 비밀번호 → 관리자 프로그램 다시 실행

**다른 PC에서 서버 PC의 라인·DB에 붙을 때** (같은 학원 네트워크)
1. 서버 PC에서 `start_all.bat` 또는 `start_all.vbs`로 서버를 켜 둔다. MySQL은 서비스라 항상 켜져 있음
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
- **메시지 형식은 `server/docs/통신_규약_v1.2.md`가 기준.** 토픽·필드 이름을 임의로 바꾸지 않는다
- **비밀번호·개인 설정은 올리지 않는다.** `settings.local.json`, `data/` 폴더는 `.gitignore`에 들어 있음. MySQL 비밀번호는 코드에 쓰지 말고 실행할 때 입력
- 올리기 전에 빌드가 되는지 확인 (Visual Studio: 빌드 → 솔루션 빌드)
- **master에 바로 올리지 않는다. 자기 브랜치 → PR → 합치기**
  1. 작업 시작 전: master로 바꾸고 **풀** (Visual Studio: Git → 풀)
  2. 자기 브랜치에서 작업: 브랜치 이름은 영문 이름 (예: `kim-hagyun`). 처음 한 번 Git → 새 분기, 다음부터는 아래쪽 브랜치 표시에서 고르기
  3. 끝나면 커밋 → 푸시 (자기 브랜치로 올라감)
  4. GitHub 저장소에 뜨는 **Compare & pull request** → 무엇을 바꿨는지 적고 **Create pull request**
  5. PR의 **Files changed** 탭에서 바뀐 파일 목록을 확인
     - **자기 담당 폴더 안만 바뀌었으면**: 빌드 확인 후 본인이 **Merge pull request** → **Confirm merge**
     - **공통 부분이 하나라도 섞였으면** (`admin/src/DepalAdmin.Core`, `Comm`, `Shell`, `admin/Directory.*.props`, `DepalAdmin.sln`, `server/`, `README.md`): 합치지 말고 PR 제목 앞에 `[공통]`을 붙인 뒤 조시현에게 알림 → 조시현이 확인 후 합침
  6. 합쳐진 뒤 다시 master로 바꾸고 풀
- PR 하나에는 한 가지 일만 (예: "검사 결과 탭 등급 5종 반영")
- 남의 담당 폴더를 고쳐야 하면 직접 고치지 말고 담당자에게 요청
- Claude Code로 작업할 때는 위 방식 그대로 (Claude Code가 브랜치를 만들고 PR까지 올려 줌). 공통 부분을 고쳤으면 PR 제목에 `[공통]`을 붙이라고 Claude에게 말할 것
- GitHub가 이 규칙을 막아 주지는 않는다 (무료 계정의 비공개 저장소는 보호 규칙·CODEOWNERS를 쓸 수 없음). 약속으로 지킨다
