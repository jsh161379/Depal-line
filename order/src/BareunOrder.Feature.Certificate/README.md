# BareunOrder.Feature.Certificate (F6 검사 성적서 · 기간 보고서)

- **담당**: 유동호
- **메뉴**: `F6 검사 성적서` (`Order = 60`)
- **역할**: 출고된(배송 중·배송 완료) 주문에 연결된 생산 회차의 라인 검사 결과를 조회해 검사 성적서를 발급·인쇄·CSV로 내보내고, 기간별(일일·주간·월간·지정 기간) 주문·매출·고객·성적서 통합 보고서를 미리보기·인쇄(PDF)합니다.
- **프로젝트 참조**: `BareunOrder.Core`, `BareunOrder.Feature.Stats` (`StatsCalculator`, `StatsPeriod`, `PeriodKind` 공용 활용)

---

## 1. 파일 구성

| 파일 | 역할 |
|---|---|
| `CertificateModule.cs` | DI 등록 및 `F6 검사 성적서` 탭(`CertificatePage`) 정의 |
| `CertificateView.xaml` / `.cs` | 상단 기간 선택 바, 왼쪽 출고 주문 목록(`전체 기간` 토글·발급 상태 칩), 오른쪽 `[성적서]`/`[보고서]` 탭 프리뷰 및 내부 참고 정보 패널 UI |
| `CertificateViewModel.cs` | 기간 필터링(초기값 월간), 비동기 DB 조회, `grading_rules` 기반 합격 판정, 성적서 번호 발급, 성적서·보고서 출력 커맨드 |
| `CertificateDbReader.cs` | MySQL `orders`·`order_runs`·`runs(LINE)`·`battery_cycles(분류 완료 A등급)`·`electrical_tests`·`visual_inspections`·`grading_rules` 조회 |
| `CertificatePrinter.cs` | 검사 성적서 A4 규격(`793×1122`) `FlowDocument` 조립 및 인쇄·PDF 출력 |
| `CertificateCsvExporter.cs` | 검사 성적서 UTF-8 BOM 인코딩 Excel 호환 CSV 파일 내보내기 |
| `OrderPeriodReportPrinter.cs` | 일일·주간·월간·지정 기간 주문·고객·매출 보고서 집계 및 A4 `FlowDocument` 인쇄·PDF 출력 |

---

## 2. 주요 동작

### 성적서 번호 규약 및 발급 조건
- `OrderJson.CertNoFor(order.No)`를 사용해 `BR-YYYYMMDD-XXXX` → `CERT-YYYYMMDD-XXXX` 형식으로 생성합니다.
- **발급 차단 조건 (실제 DB 모드)**:
  - 연결된 생산 회차(`order_runs` · `LINE`)가 없는 경우 (`미충족`)
  - 연결된 회차의 분류 완료 A등급 합계 수량이 주문 건전지 수량보다 부족한 경우 (`수량 부족`)
  - 검사 기준 미달 항목이 있는 경우 (`불합격`)
- **성적서 발급 / 재발급** 시 `OrderStore.SetCertNoAsync()`를 호출해 `order/status/<no>`(retained)를 발행합니다.
  - 목록 칩이 `미발급` → `발급완료`로 전환되며 보고서의 성적서 발급 현황도 함께 갱신됩니다.
  - 실제 서버 연동 시 DB `orders.cert_no` 저장 및 홈페이지 주문 조회 화면에 반영됩니다.

### DB 실측 조회 및 성적서 구성 (`CertificateDbReader` · `CertificatePrinter`)
- **조회 대상 (11/20 결정 ⑦⑧⑩ 반영)**:
  - `runs.run_type = 'LINE'` 회차만 집계 (`BENCH_TEST` 제외)
  - `battery_cycles.final_grade = 'A'` 이면서 `b.sorted_destination IS NOT NULL`(실제 배출 완료분)만 집계
  - `grading_rules`에서 연결 회차(`battery_cycles.rule_id`)의 `rule_version`과 `voltage_and_resistance_thresholds` JSON(`A.v_open_min`, `A.r_int_max`)을 자동 조회 (미연결·권한 없음·JSON 누락 시 최신 유효 기준 → 임시 폴백 `1.50 V` / `1.20 Ω` 순으로 안전 폴백)
- **고객용 성적서 본문 (화면 카드 · 인쇄/PDF · CSV 공통)**:
  - **기본 정보**: 성적서 번호, 주문번호, 받는 분(`이름 (개인/기업)`), 총 주문 수량, 검사 일시, 검사 회차, **기준 버전(`rule_version`)**, 발급 상태
  - **주문 상품 내역 표**: 상품명 · 팩 구성 · 수량(팩) · 건전지 수 및 합계
  - **검사 항목 및 결과 표**:
    - 무부하 전압·부하 전압·내부저항은 **연결 회차 A등급 평균값만** 표시 (최소~최대·최대 괄호 표기 제외)
    - 극 방향은 고객용 표현(`정상 N/N` 또는 `확인 필요`)으로 표시
  - **연결된 생산 회차 내역**: 회차별 수량·측정값 표 + 하단 합계/요약 행
  - **하단 서명란**: 합격 기준 안내 문구, `검사자`(`AppSettings.Operator`) / `승인자 (품질)` 칸, 발행일
- **관리자 참고 정보 (화면 전용 · 고객 인쇄 제외)**:
  - 데이터 출처, 적용된 `grading_rules` 기준 상세(`rule_version`·임계값), YOLO 외관 평균 신뢰도(`%`), 카메라 극 방향 상세(`음극 상향/양극 상향`), 누락 건수를 별도 내부 패널에 표시합니다.
- **종합 판정 문구 통일**: 화면 · 인쇄(PDF) · CSV 모두 **`합격` / `수량 부족` / `불합격` / `미충족`**으로 통일했습니다.
- **합격 판정 기준** (연결 회차 존재 + A등급 수량 충족 + 판정 항목 모두 적합 시 `합격`):
  1. 외관 (스크래치·스티커·훼손·부식): 외관 검사 기록 존재 및 불량(`FAIL`)·오류(`ERROR`) `0건`
  2. 극 방향: 카메라 극 방향 미상(`unknown`) `0건` 및 전기 정극성(`normal`) 전체 일치
  3. 무부하 전압 (평균): DB `grading_rules`의 `A.v_open_min` 이상 *(조회 불가 시 폴백 `1.50 V`)*
  4. 부하 전압 (평균): **`참고` 항목** (측정값만 표시, 합격 판정에서 제외)
  5. 내부저항 (평균): DB `grading_rules`의 `A.r_int_max` 이하 *(조회 불가 시 폴백 `1.20 Ω`)*
  6. 등급 및 검사 수량: 연결 회차 분류 완료 A등급 합계 수량 ≥ 주문 건전지 수량

### 주문 관리 기간 보고서 (`OrderPeriodReportPrinter`)
- 상단 바에서 **일일 / 주간 / 월간** 전환, `DatePicker` 자유 기간 지정, `◀` / `▶` / `오늘` 이동을 지원합니다.
- 초기 진입 시 이번 달(`월간`) 출고 주문을 표시하며, 왼쪽 상단 **`전체 기간`** 체크박스로 기간과 무관하게 전체 출고 주문을 볼 수 있습니다.
- **보고서 구성 및 표시 규칙 (취소 주문은 매출·팩·건전지 수·고객 분석 합계에서 제외)**:
  1. **기간 요약**: 총 주문·매출·팩·건전지 수, 주문당 평균, 취소율(분모 포함), 출고·배송 건수, 전기 대비 증감(이전 기간 데이터가 없으면 **`비교 자료 없음`**)
  2. **출고 및 검사 성적서 현황**: 판매·출고·미출고 건전지 수, 출고/미출고 주문 수, 성적서 발급 완료/미발급 건수 및 발급률
  3. **상품별 매출**: 표의 **`매출 비중` 열 안에 인라인 막대** 통합. `Products.OnSale == false`인 판매 중지 상품(`aar2`)은 과거 매출이 있을 때만 `(판매 중지)` 표기로 표시하고, 매출이 0이면 생략
  4. **고객 분석 (11/21 고객 정보 반영)**:
     - **개인 · 기업 비율**: 고객 유형별 주문 수, 주문 비율(인라인 막대), 매출, 매출 비중
     - **연령대별 주문 수 · 매출**: `10대 / 20대 / 30대 / 40대 / 50대 이상 / 선택 안 함`(`none` → `선택 안 함`, 기업 주문 포함)
     - **지역별 주문 수 · 매출**: 주문이 있는 시·도 매출순 표시 + `선택 안 함`(`none`) 하단 배치
  5. **기간 추이 (2일 이상 조회 시)**:
     - 일별 매출 그래프: 제목과 그래프를 단일 블록으로 묶어 인쇄 시 페이지 분리 방지, Y축 눈금 및 막대 상단 금액 라벨 표시 (월간 15일 초과는 겹침 방지를 위해 반올림 축약 표기, 31일 초과 시 생략 안내)
     - 요약 표: **14일 이하**는 일별 표(오늘 이후 미래 날짜는 **`집계 전`** 표기), **15일 이상(월간 등)**은 **주차별(`1~5주차`) 요약 표**로 자동 전환
- `[보고서]` 탭이 열려 있을 때만 `FlowDocument` 미리보기를 재생성해 불필요한 UI 부하를 줄입니다.

### 출력 기능
- **성적서 / 보고서 인쇄 · PDF**: `PrintDialog`에서 `Microsoft Print to PDF` 선택 시 A4 PDF로 저장됩니다.
- **성적서 CSV 저장**: 기본 정보(받는 분 고객 유형·기준 버전 포함), 주문 상품 내역, 검사 항목(`적합`/`부적합`/`참고`), 연결 회차 내역을 UTF-8 BOM CSV로 저장합니다.

---

## 3. 로컬 테스트 방법 (`settings.local.json`)

실행 파일 폴더(`order\src\BareunOrder.Shell\bin\Debug\net8.0-windows\settings.local.json`, **Git 제외**) 설정에 따라 두 모드로 동작합니다.  
`Operator` 값은 성적서 `검사자` 및 기간 보고서 `작성자` 이름에 그대로 반영됩니다.

- **Fake 모드** (DB/브로커 없이 UI·발급·고객 분석·출력 확인):
  ```json
  {
    "UseFakeOrders": true,
    "Operator": "유동호"
  }
  ```

- **실제 DB 연동 모드** (읽기 전용 `ui_reader` 계정 사용):
  ```json
  {
    "UseFakeOrders": false,
    "MqttHost": "192.168.0.158",
    "MqttPort": 1883,
    "Operator": "유동호",
    "MySqlConnection": "Server=192.168.0.158;Port=3306;Database=aa_battery_inspection;User=ui_reader;Password=123456;"
  }
  ```

---

## 4. 참고 사항
- 하드웨어 실측 후 서버 `server/rules`로 최종 판정 기준이 발행되면 DB `grading_rules`에 자동 저장되며, F6 성적서는 별도 코드 수정 없이 새 기준 버전과 임계값을 자동으로 읽어 표시합니다.