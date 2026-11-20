# BareunOrder.Feature.Certificate (F6 검사 성적서 · 기간 보고서)

- **담당**: 유동호
- **메뉴**: `F6 검사 성적서` (`Order = 60`)
- **역할**: 출고된(배송 중·배송 완료) 주문에 연결된 생산 회차의 라인 검사 결과를 조회해 검사 성적서를 발급·인쇄·CSV로 내보내고, 기간별(일일·주간·월간·지정 기간) 주문 관리 통합 보고서를 미리보기·인쇄(PDF)합니다.
- **프로젝트 참조**: `BareunOrder.Core`, `BareunOrder.Feature.Stats` (`StatsCalculator`, `StatsPeriod`, `PeriodKind` 공용 활용)

---

## 1. 파일 구성

| 파일 | 역할 |
|---|---|
| `CertificateModule.cs` | DI 등록 및 `F6 검사 성적서` 탭(`CertificatePage`) 정의 |
| `CertificateView.xaml` / `.cs` | 상단 기간 선택 바, 왼쪽 출고 주문 목록(`전체 기간` 토글·발급 상태 칩), 오른쪽 `[성적서]`/`[보고서]` 탭 프리뷰 및 내부 참고 정보 패널 UI |
| `CertificateViewModel.cs` | 기간 필터링(초기값 월간), 비동기 DB 조회, 수량·합격 판정, 성적서 번호 발급, 성적서·보고서 출력 커맨드 |
| `CertificateDbReader.cs` | MySQL `orders`·`order_runs`·라인 검사 테이블 조인 조회 |
| `CertificatePrinter.cs` | 검사 성적서 A4 규격(`793×1122`) `FlowDocument` 조립 및 인쇄·PDF 출력 |
| `CertificateCsvExporter.cs` | 검사 성적서 UTF-8 BOM 인코딩 Excel 호환 CSV 파일 내보내기 |
| `OrderPeriodReportPrinter.cs` | 일일·주간·월간·지정 기간 주문 관리 보고서 집계 및 A4 `FlowDocument` 인쇄·PDF 출력 |

---

## 2. 주요 동작

### 성적서 번호 규약 및 발급 조건
- `OrderJson.CertNoFor(order.No)`를 사용해 `BR-YYYYMMDD-XXXX` → `CERT-YYYYMMDD-XXXX` 형식으로 생성합니다.
- **발급 차단 조건 (실제 DB 모드)**:
  - 연결된 생산 회차(`order_runs`)가 없는 경우 (`미충족`)
  - 연결된 회차의 A등급 합계 수량이 주문 건전지 수량보다 부족한 경우 (`수량 부족`)
- **성적서 발급 / 재발급** 시 `OrderStore.SetCertNoAsync()`를 호출해 `order/status/<no>`(retained)를 발행합니다.
  - 목록 칩이 `미발급` → `발급완료`로 전환되며 보고서의 성적서 발급 현황도 함께 갱신됩니다.
  - 실제 서버 연동 시 DB `orders.cert_no` 저장 및 홈페이지 주문 조회 화면에 반영됩니다.

### DB 실측 조회 및 성적서 구성 (`CertificateDbReader` · `CertificatePrinter`)
- **조회 테이블**: `orders`, `order_runs`, `runs`, `battery_cycles(final_grade='A')`, `electrical_tests`, `visual_inspections`
- **고객용 성적서 본문 (화면 카드 · 인쇄/PDF)**:
  - **주문 상품 내역 표**: 주문에 포함된 상품명 · 팩 수 · 건전지 수량 및 합계 표시
  - **검사 항목 및 결과 표**: 고객이 이해하기 쉬운 표현 사용 (극 방향은 `정상 N/N` 또는 `확인 필요`로 표시)
  - **연결된 생산 회차 내역**: 회차별 수량·측정값 표 + 하단 합계/요약 행 표시
  - **하단 서명란**: 합격 기준 안내 문구, 발행일, `검사자`(`AppSettings.Operator`) / `승인자 (품질)` 칸 포함
- **관리자 참고 정보 (화면 전용 · 고객 인쇄 제외)**:
  - 내부 용어 및 상세 수치(카메라 극 방향 `음극 상향/양극 상향`, YOLO 신뢰도 `%`, `order_runs`, 누락 건수 등)는 고객용 성적서 카드 아래 별도 내부 패널로 분리해 표시합니다.
- **종합 판정 문구 통일**: 화면 · 인쇄(PDF) · CSV 모두 **`합격` / `수량 부족` / `불합격` / `미충족`**으로 통일했습니다.
- **합격 판정 기준** (연결 회차 존재 + A등급 수량 충족 + 판정 항목 모두 적합 시 `합격`):
  1. 외관 (스크래치·스티커·훼손·부식): 외관 검사 기록 존재 및 불량(`FAIL`)·오류(`ERROR`) `0건`
  2. 극 방향: 카메라 극 방향 미상(`unknown`) `0건` 및 전기 정극성(`normal`) 전체 일치
  3. 무부하 전압 (평균): `1.50 V` 이상 *(임시 기준 — 서버 `grading_rules` 확정 시 동기화)*
  4. 부하 전압 (평균): **`참고` 항목** (측정값만 표시, 합격 판정에서 제외)
  5. 내부저항 (평균): `0.35 Ω` 이하 *(임시 기준 — 서버 `grading_rules` 확정 시 동기화)*
  6. 등급 (A등급 출하): 연결 회차 A등급 합계 수량 ≥ 주문 건전지 수량

### 주문 관리 기간 보고서 (`OrderPeriodReportPrinter`)
- 상단 바에서 **일일 / 주간 / 월간** 전환, `DatePicker` 자유 기간 지정, `◀` / `▶` / `오늘` 이동을 지원합니다.
- 초기 진입 시 이번 달(`월간`) 출고 주문을 표시하며, 왼쪽 상단 **`전체 기간`** 체크박스로 기간과 무관하게 전체 출고 주문을 볼 수 있습니다.
- **보고서 구성 및 표시 규칙**:
  1. **기간 요약**: 총 주문·매출·건전지 수, 취소율(분모 포함), **미출고 주문 수 · 미출고 건전지 수**, 전기 대비 증감(이전 기간 데이터가 없으면 **`비교 자료 없음`**으로 표시)
  2. **검사 성적서 현황**: 출고 주문 기준 발급 완료 / 미발급 건수
  3. **상품별 매출**: 표의 **`매출 비중` 열 안에 인라인 막대**를 함께 표시해 중복 차트 제거
  4. **기간 추이 (2일 이상 조회 시)**:
     - 일별 매출 그래프: 제목과 그래프를 단일 블록으로 묶어 인쇄 시 페이지 분리 방지, **Y축 눈금 및 막대 상단 금액 라벨** 표시 (31일 초과 시 생략 안내)
     - 요약 표: **14일 이하**는 일별 표(오늘 이후 미래 날짜는 **`집계 전`** 표기), **15일 이상(월간 등)**은 **주차별(`1~5주차`) 요약 표**로 자동 전환
- `[보고서]` 탭이 열려 있을 때만 `FlowDocument` 미리보기를 재생성해 불필요한 UI 부하를 줄입니다.

### 출력 기능
- **성적서 / 보고서 인쇄 · PDF**: `PrintDialog`에서 `Microsoft Print to PDF` 선택 시 A4 PDF로 저장됩니다. *(PDF 출력 안내는 문서 본문이 아닌 화면 상단 툴바에 표시)*
- **성적서 CSV 저장**: 기본 정보, 주문 상품 내역, 검사 항목(`적합`/`부적합`/`참고`), 연결 회차 내역을 UTF-8 BOM CSV로 저장합니다.

---

## 3. 로컬 테스트 방법 (`settings.local.json`)

실행 파일 폴더(`order\src\BareunOrder.Shell\bin\Debug\net8.0-windows\settings.local.json`, **Git 제외**) 설정에 따라 두 모드로 동작합니다.  
`Operator` 값은 성적서 `검사자` 및 기간 보고서 `작성자` 이름에 그대로 반영됩니다.

- **Fake 모드** (DB/브로커 없이 UI·발급·출력 확인):
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

## 4. 추후 반영 예정 (공통·서버 확정 후)
- 공통(`BareunOrder.Core`) 및 DB에 `customer_type`, `age_group`, `region` 추가 시:
  - 검사 성적서: 받는 곳 칸에 고객 유형(`개인`/`기업`) 표시
  - 기간 보고서: 연령대별·지역별 주문 수/매출 표(`none`은 `선택 안 함`으로 묶음) 및 개인/기업 주문 비율 추가
- 서버 `grading_rules` A등급 확정 기준값 결정 시 `AGradeVOpenMin` / `AGradeRIntMax` 동기화