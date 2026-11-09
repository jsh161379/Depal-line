# 디팔레타이저 라인 — AA 건전지 검사·분류

학원 최종 프로젝트 「디팔레타이저를 위한 영상처리와 신경망 알고리즘 개발」 (2026.11.12 ~ 12.17, 6명)

팔레트의 AA 건전지를 로봇팔이 하나씩 꺼내, 카메라(YOLO) 외관·극 방향 검사와 전압·내부저항 측정을 거쳐
A / B / 불량 / 재분류로 나눈다. 모든 장치와 프로그램은 **MQTT 브로커(Mosquitto)** 를 통해서만 메시지를 주고받는다.

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
3. 관리자 프로그램을 실제 연결로 보려면 실행 폴더(`bin\Debug\net8.0-windows`)에 `settings.local.json`을 만들고 `"UseFakeMqtt": false` (나머지 값은 `settings.json`과 같게)

**DB까지 저장해 볼 때** (MySQL 8 필요)
1. `server/db/teamproject.sql`로 DB 만들기
2. `server/start_db_test.bat` → `5 DB WRITER` 창에 MySQL 비밀번호 입력
3. 확인: `server/check_db.bat`

## 규칙

- **자기 담당 폴더만 고친다.** 공통 파일(`admin/src/DepalAdmin.Core`, `DepalAdmin.Comm`, `DepalAdmin.Shell`, `server/`)과 통신 규약은 통신 담당이 관리한다. 바꿔야 하면 먼저 말하기
- **메시지 형식은 `server/docs/통신_규약_v1.0.md`가 기준.** 토픽·필드 이름을 임의로 바꾸지 않는다
- **비밀번호·개인 설정은 올리지 않는다.** `settings.local.json`, `data/` 폴더는 `.gitignore`에 들어 있음. MySQL 비밀번호는 코드에 쓰지 말고 실행할 때 입력
- 올리기 전에 빌드가 되는지 확인 (Visual Studio: 빌드 → 솔루션 빌드)
