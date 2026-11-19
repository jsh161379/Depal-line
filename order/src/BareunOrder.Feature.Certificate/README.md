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
| `CertificateView.xaml` / `.cs` | 상단 기간 선택 바, 왼쪽 출고 주문 목록(`전체 기간` 토글·발급 상태 칩), 오른쪽 `[성적서]`/`[보고서]` 탭 프리뷰 UI |
| `CertificateViewModel.cs` | 기간 필터링(초기값 월간), 비동기 DB 조회, 성적서 번호 발급, 성적서·보고서 출력 커맨드 |
| `CertificateDbReader.cs` | MySQL `orders`·`order_runs`·라인 검사 테이블 조인 조회 |
| `CertificatePrinter.cs` | 검사 성적서 A4 규격(`793×1122`) `FlowDocument` 조립 및 인쇄·PDF 출력 |
| `CertificateCsvExporter.cs` | 검사 성적서 UTF-8 BOM 인코딩 Excel 호환 CSV 파일 내보내기 |
| `OrderPeriodReportPrinter.cs` | 일일·주간·월간·지정 기간 주문 관리 보고서 집계 및 A4 `FlowDocument` 인쇄·PDF 출력 |

---

## 2. 주요 동작

### 성적서 번호 규약 및 발급
- `OrderJson.CertNoFor(order.No)`를 사용해 `BR-YYYYMMDD-XXXX` → `CERT-YYYYMMDD-XXXX` 형식으로 생성합니다.
- **성적서 발급 / 재발급** 시 `OrderStore.SetCertNoAsync()`를 호출해 `order/status/<no>`(retained)를 발행합니다.
  - 목록 칩이 `미발급` → `발급완료`로 전환되며 보고서의 성적서 발급 현황도 함께 갱신됩니다.
  - 실제 서버 연동 시 DB `orders.cert_no` 저장 및 홈페이지 주문 조회 화면에 반영됩니다.

### DB 실측 조회 및 판정 기준 (`CertificateDbReader`)
- 조회 테이블: `orders`, `order_runs`, `runs`, `battery_cycles(final_grade='A')`, `electrical_tests`, `visual_inspections`
- 연결된 `order_runs`가 없는 주문은 발급 버튼을 비활성화하고 `미충족`으로 표시합니다.
- 화면 · 인쇄(PDF) · CSV의 종합 판정 문구를 **`합격` / `불합격` / `미충족`**으로 통일했습니다.
- **합격 판정 기준 (6개 항목 모두 적합 시 `합격`)**:
  1. 외관 (스크래치·스티커·훼손·부식): 불량(`FAIL`)·오류(`ERROR`) `0건`
  2. 극 방향 (카메라·전기): 카메라 `unknown` `0건` 및 전기 `normal` 전체 일치
  3. 무부하 전압 (평균): `1.50 V` 이상 *(임시 기준 — 하드웨어 실측 후 서버 `grading_rules` 확정 시 동기화)*
  4. 부하 전압 (평균): 측정 완료 (참고, `> 0 V`)
  5. 내부저항 (평균): `0.35 Ω` 이하 *(임시 기준 — 하드웨어 실측 후 서버 `grading_rules` 확정 시 동기화)*
  6. 등급: A등급 출하 수량 `1개` 이상

### 주문 관리 기간 보고서 (`OrderPeriodReportPrinter`)
- 상단 바에서 **일일 / 주간 / 월간** 전환, `DatePicker` 자유 기간 지정, `◀` / `▶` / `오늘` 이동을 지원합니다.
- 초기 진입 시 이번 달(`월간`) 출고 주문을 표시하며, 왼쪽 상단 **`전체 기간`** 체크박스로 기간과 무관하게 전체 출고 주문을 볼 수 있습니다.
- 표준 일/주/월과 다른 임의 기간을 직접 선택하면 실제 조회 구간과 직전 동일 일수 기간을 자동으로 매칭해 비교합니다.
- 보고서 구성: ① 기간 요약(합계·평균·전기 대비), ② 검사 성적서 현황(출고 주문 기준), ③ 상품별 매출 표·막대 그래프, ④ 월간 지난달 비교, ⑤ 2일 이상 조회 시 일별 매출 그래프(31일 초과 시 생략 안내)·일별 주문·매출 표.
- `[보고서]` 탭이 열려 있을 때만 `FlowDocument` 미리보기를 재생성해 불필요한 UI 부하를 줄입니다.

### 출력 기능
- **성적서 / 보고서 인쇄 · PDF**: `PrintDialog`에서 `Microsoft Print to PDF` 선택 시 A4 PDF로 저장됩니다.
- **성적서 CSV 저장**: 기본 정보, 검사 항목 6줄, 연결 회차 내역을 UTF-8 BOM CSV로 저장합니다.

---

## 3. 로컬 테스트 방법 (`settings.local.json`)

실행 파일 폴더(`order\src\BareunOrder.Shell\bin\Debug\net8.0-windows\settings.local.json`, Git 제외) 설정에 따라 두 모드로 동작합니다.
파일이 없는 경우 해당 경로에 `settings.local.json`을 생성하고 아래 내용을 붙여 넣어 테스트합니다.

- **Fake 모드** (DB/브로커 없이 UI·발급·출력 확인):
  ```json
  {
    "UseFakeOrders": true
  }