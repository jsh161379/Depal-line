# DepalAdmin.Feature.Handover (교대 인수인계)

* **담당**: 유동호
* **역할**: 관리자 프로그램(`DepalAdmin`)의 F7 **인수인계** 탭을 담당하며, 교대 근무자 간 라인 실적·설비 점검·품질 이슈·분류함 상태·안전/환경 체크리스트 및 전달사항을 기록·공유하고 읽음 확인 상태를 추적합니다.

## 1. 폴더 및 파일 구조

```
DepalAdmin.Feature.Handover/
 ├─ DepalAdmin.Feature.Handover.csproj  # MySqlConnector, MQTTnet 및 DepalAdmin.Core 참조
 ├─ HandoverModule.cs                   # DI 등록 (UseFakeDb 분기: JsonHandoverStore / MySqlHandoverStore, Page Order=70)
 ├─ JsonHandoverStore.cs                # 로컬 단독 개발용 JSON 저장소 (data/handover.json)
 ├─ MySqlHandoverStore.cs               # 운영·다중 PC용 MySQL(읽기) + MQTT(발행 우선 쓰기/실시간 동기화) 저장소
 ├─ HandoverView.xaml                   # 좌측 메모 목록(DataGrid) + 우측 6개 섹션 교대 인수인계 폼
 ├─ HandoverView.xaml.cs                # 빈 영역 클릭 시 선택 해제(본문 복사·검색·날짜 선택·정렬 클릭은 유지)
 ├─ HandoverViewModel.cs                # 실시간 라인·장치·환경 스냅샷, 직렬화/하위 호환 파싱, 백업·복원, 미확인 배지
 └─ readme.md
```

## 2. 저장소 및 다중 PC 동기화 아키텍처

`AppSettings.UseFakeDb` 및 `AppSettings.UseFakeMqtt` 설정에 따라 `HandoverModule.cs`에서 저장소 구현체를 자동 선택합니다. (`IHandoverStore` 인터페이스를 유지하므로 조시현 담당의 `DepalAdmin.Feature.Report`와 그대로 호환됩니다.)

* **로컬 개발 모드 (`UseFakeDb == true`)**: `JsonHandoverStore`를 사용하여 `data/handover.json` 파일에 원자적(`*.tmp` → `Move`)으로 저장합니다.
* **실운영 DB·MQTT 모드 (`UseFakeDb == false`)**: `MySqlHandoverStore`를 사용합니다 (`DB_인수인계_변경사항_2026-11-19.md` 규약 준수).
  * **조회 (`GetAsync`)**: 읽기 전용 계정(`ui_reader`)으로 MySQL `handover_notes` 및 `handover_reads` 테이블에서 기간 내 메모와 읽음 기록을 `SELECT` 합니다.
  * **작성 및 읽음 처리 (`AddAsync`, `MarkReadAsync`)**: WPF가 DB에 직접 쓰지 않고 MQTT 토픽으로 먼저 발행(`CultureInfo.InvariantCulture` 타임스탬프)한 뒤 성공 시에만 로컬 캐시에 반영하여, 브로커 끊김 시 캐시 오염 및 `[읽음]` 재시도 누락을 방지합니다. 실제 DB 저장은 `server/db/mqtt_db_writer.py`가 수행합니다.
  * **다중 PC 실시간 갱신**: 각 관리자 PC가 `{EffectiveClientId}-handover` 클라이언트 ID로 `handover/note`와 `handover/read` 토픽을 구독하여, 다른 PC에서 남긴 메모나 읽음 처리를 수신 즉시 캐시에 반영하고 `Changed` 이벤트를 발생시킵니다.

### MQTT 메시지 규약 (QoS 1, `retain = false`)

| 토픽 | 용도 | 주요 필드 (`snake_case` JSON) |
|---|---|---|
| `handover/note` | 새 인수인계 메모 작성 | `note_id`(GUID 32자), `ts`, `author_id`(ASCII), `author_name`, `text`, `line_mode`, `run_id`, `unacked_alarms` |
| `handover/read` | 메모 읽음 확인 처리 | `note_id`, `user_id`(ASCII), `ts` |

## 3. 화면 구성 및 주요 기능 (규약 v1.5.1 / v2 반영)

1. **6개 섹션 구조화 체크리스트 폼**:
   * **1. 검사 회차(Run) 실적 및 진행 현황**: 작성 모드에서는 실시간 라인 상태·미확인 알람·현재 회차·등급별 분류 개수(`A`, `B`, `외관불량`, `전압불량`, `재분류`)를 표시하고, 과거 메모 선택 시에는 **해당 메모 작성 시점의 기록 수치**로 자동 전환합니다. 인계 시점 작업 상태 라디오 버튼 및 후번 이월 팔레트 수(`2×4칸`, 규약 v1.5.1 기준) 입력을 지원합니다.
   * **2. 로봇팔 · 컨베이어 · 비전 · 측정 · AGV 상태**: `ILineClient.DevicesReceived`를 구독해 주요 장치(`arm1 · mega · yolo · agv · env`) 상태를 자동 표시(이상 시에만 호박색 강조 — ISA-101)하며, 로봇팔 1(집게), 로봇팔 2(분류), 컨베이어·재분류 서보, 전압 측정 헤드(ADS1115), 카메라 3대·암막조명, AGV(포크·주행) 점검 체크 및 AGV 상태를 기록합니다.
   * **3. 건전지 품질 및 재분류(RESORT) 이슈**: 외관 불량 다발, 상단 +극 위(역방향) 다발, 측정 접촉 불량(`abnormal`), 비전/배출 실패 체크 및 재분류함 보관 잔여 수량 입력을 지원합니다.
   * **4. 팔레트 및 분류함 비우기 확인**: 실시간 AGV 적재 현황 표시 및 도착지 5곳 기준(B 분류함 `B_BOX`, 외관불량함 `NG_VISUAL_BOX`, 전압불량함 `NG_VOLTAGE_BOX`, 재분류함(서보) `RESORT_BOX`, 컨베이어 퍽 원점 확인) 체크를 제공합니다.
   * **5. 비상정지 이력 및 안전/환경 센서**: `ILineClient.EnvReceived`를 구독해 환경 센서(Uno: 온도·습도·가스) 실시간 수치를 자동 표시하며, 비상정지 발생·복귀 여부, AGV 주행로, 로봇팔 반경 간섭, 가스(MQ-9)·온습도(Uno) 정상 여부를 기록합니다.
   * **6. 판정 기준 변경 및 상세 전달사항**: 인계 구분(`일반 인계`, `설비 주의`, `기준·설정 변경`, `긴급 확인`) 및 상세 전달 메모를 작성합니다.
2. **직렬화 및 하위 호환 파싱**:
   * 저장 시 1~6번 항목을 구조화된 문자열(`BuildFormattedNoteText`)로 직렬화하여 `HandoverNote.Text`에 저장하며(수량 입력은 음수·비숫자 보정), 목록에서 과거 메모를 선택하면 폼의 체크박스와 입력값(`ApplyNoteToForm`)을 그대로 복원합니다.
   * 구버전 표기(`1번 적재팔(집게)`, `2번 분류팔(전자석)`, `컨베이어·원점스위치`, `AGV 지게차·리프트`, `불량함 비움` 등)도 자동으로 하위 호환 파싱합니다.
3. **작성 중 내용 자동 백업·복원 (`FormSnapshot`) 및 탐색 편의성**:
   * 새 메모를 작성하다가 좌측 목록에서 기존 메모를 확인한 뒤 빈 영역이나 `[새 메모 쓰기]`를 누르면 작성 중이던 체크리스트와 본문이 그대로 복원됩니다.
   * 본문 복사(텍스트 상자), 검색(내용·작성자·회차 ID), 날짜 선택기, 열 정렬 헤더 클릭 시에는 선택 상태가 유지됩니다.
4. **권한 · 알림 · 예외 처리**:
   * 로그인 시 본인이 작성하지 않은 안 읽은 메모가 있으면 상단 경고 배너(`Notice`)와 탭 숫자 배지(`IHasBadge`)를 갱신하고 **인수인계** 탭으로 자동 이동합니다. 로그아웃 시에는 목록과 배지를 즉시 초기화합니다.
   * 보기 전용(`Viewer`) 계정도 선택한 메모의 본문 스크롤·복사 및 읽음 확인이 가능하며, 작업자(`Operator`) 이상 권한(`Permission.OperateLine`)부터 새 메모를 남길 수 있습니다.
   * MQTT 미연결 등 저장·읽음 처리 실패 시 경고 메시지를 표시하고 작성 중이던 폼 내용을 보존합니다.