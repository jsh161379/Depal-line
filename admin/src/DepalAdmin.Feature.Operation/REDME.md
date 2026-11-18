# DepalAdmin.Feature.Operation (라인 조작 패널)

* **담당**: 유동호
* **역할**: 라인 현황 화면(`DepalAdmin.Feature.Process`) 우측 슬롯(`PanelSlots.LineControls`)에 장착되어 작업자의 라인 제어 명령(`ui/cmd`)을 발행하고, 실시간 상태·경고 안내를 표시하는 조작 패널입니다.

---

## 1. 폴더 및 파일 구조

```text
DepalAdmin.Feature.Operation/
 ├─ DepalAdmin.Feature.Operation.csproj
 ├─ OperationModule.cs              # DI 등록 (ControlPanelViewModel, IPanel -> line.controls)
 ├─ README.md
 └─ Panel/
     ├─ ControlPanelView.xaml       # 2열 6버튼 조작 UI 및 상태·이벤트 안내 배너 (ISA-101 스타일)
     ├─ ControlPanelView.xaml.cs
     └─ ControlPanelViewModel.cs    # 상태별 버튼 활성화(CanExecute), 확인창, 서버 메시지/이벤트 연동
```

---

## 2. 통신 및 권한 규약

* **수신 (`ILineClient`)**
  * `LineStatusReceived` (`server/line`): `Mode` (`idle`, `running`, `paused`, `draining`, `estop`), `Step` (`wait_operator`, `reset` 등), `Message`를 수신하여 버튼 활성화 상태와 안내 배너(`StatusGuide`)를 갱신합니다.
  * `EventReceived` (`server/events`): `controller`, `arm1`, `mega`에서 발생한 `WARN`/`ERROR` 이벤트(`start 거부`, `해제 거부: 비상정지 버튼이 아직 눌려 있음`, `ARM2_FAILED` 등)를 수신하여 안내 배너 및 하단 결과 줄(`CommandResult`)에 즉시 표시합니다.
* **발행 (`ILineClient.SendCommandAsync`)**
  * 토픽: `ui/cmd` (`UiActions` 상수 사용)
  * 명령: `start`, `pause`, `resume`, `stop`, `skip`, `reset` (※ 상시 활성인 비상정지 `estop` 버튼은 상단 `DepalAdmin.Shell` 헤더에 위치)
* **권한 (`Session`)**
  * `Permission.OperateLine` 권한(작업자 `Operator` 이상)이 있을 때만 조작 버튼이 활성화됩니다.
  * 보기 전용(`Viewer`) 계정 로그인 시 모든 조작 버튼이 비활성화되며 `"보기 전용 계정이라 비상정지만 쓸 수 있습니다."` 안내를 표시합니다.

---

## 3. 상태별 버튼 활성화 (`CanExecute`) 규칙

`server/line_controller.py` 및 `FakeLineClient.cs`의 실제 상태 전이 규약과 일치하도록 구성되어 있습니다.

| 버튼 | 명령 (`ui/cmd`) | 활성화 조건 (`CanOperate == true` 필수) | 확인창 여부 및 동작 설명 |
| :--- | :--- | :--- | :--- |
| **시작** | `start` | `Mode == "idle"` | 즉시 전송 (장비 이상 시 서버가 `start 거부` WARN 반환) |
| **일시정지** | `pause` | `Mode is "running" or "draining"` | 즉시 전송 (가동 중 또는 마무리 중에 벨트 일시정지) |
| **재개** | `resume` | `Mode == "paused"` | 즉시 전송 (일반 일시정지 해제 또는 `wait_operator` 재시도) |
| **회차 종료** | `stop` | `Mode is "running" or "paused"` | 확인창 표시 후 전송 (새 퍽 투입 중단, 벨트 위 퍽만 분류 후 종료) |
| **건너뛰기** | `skip` | `Mode == "paused"` 및 `Step == "wait_operator"` (집기 실패 시) | 확인창 표시 후 전송 (실패한 칸을 포기하고 다음 칸 진행) |
| **비상정지 해제** | `reset` | `Mode == "estop"` 및 `Step != "reset"` | 물리 비상정지 버튼 복귀 및 벨트 위 퍽 회수 확인창 표시 후 전송 |

---

## 4. 주요 안전 및 예외 안내 연동

1. **비상정지 해제(`reset`) 사전 안내**:
   * 물리 비상정지 버튼이 눌려 있는 동안(`BUTTON_PRESSED` ~ `BUTTON_RELEASED` 전)에는 컨트롤러가 `reset`을 거부합니다.
   * 비상정지 진입 시 빨간색 배너(`alarm`)로 물리 버튼 복귀 안내를 띄우고, `[비상정지 해제]` 클릭 시 확인창에서 물리 버튼 복귀 여부와 벨트 위 잔여 퍽 회수 여부를 다시 확인합니다.
   * 해제 진행 중(`Step == "reset"`)에는 중복 클릭을 방지하기 위해 버튼을 비활성화합니다.
2. **장비 이상 및 작업자 대기(`wait_operator`) 안내**:
   * 로봇팔 1 집기 실패(`pick 실패`), 팔레트 인식 실패, 로봇팔 2 분류 실패(`FAULT ARM2_FAILED`), 벨트 구동 실패 등 서버가 보내는 `LineStatus.Message` 및 `LineEvent`를 호박색(`warn`)/빨간색(`alarm`) 배너로 즉시 표시합니다.