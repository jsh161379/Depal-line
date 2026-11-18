# 바른전지 주문 관리 (BareunOrder)

바른전지 홈페이지에서 들어온 주문을 받아 처리하는 WPF 프로그램.
관리자 프로그램(DepalAdmin)과 **같은 구조**라서, 각자 자기 탭 폴더만 고치면 충돌 없이 합칠 수 있다.

```
홈페이지 ──MQTT(WebSocket)──▶ Mosquitto(서버 PC) ◀──MQTT(1883)── 주문 관리 프로그램
         ◀── 상태·송장 (order/status/주문번호) ───────────────────
```

## 역할 분담

담당 칸은 비워 두었다. 팀에서 정해서 채운다.

| 탭 | 키 | 담당 | 폴더 | 지금 들어 있는 것 | 할 일 |
|---|---|---|---|---|---|
| 공통 | — |  | `BareunOrder.Core`, `BareunOrder.Shell` | 메인 창, 탭, 새 주문 알림, 주문 모델, 가짜 주문, MQTT 연결, 연결 상태 표시, 채팅 연결(ChatStore) | 홈페이지 → MQTT 연동, 공통 요청 처리 |
| 주문 | F1 |  | `Feature.Orders` | 목록·상태 거르기·검색, 상세, 다음 단계·취소 (완성 예시) | 다른 탭 참고용으로 유지 |
| 출고·배송 | F2 |  | `Feature.Shipping` | 출고 대기 목록, 필요한 건전지 수, 송장 입력 → 배송 중, [라인에 요청] 버튼 | 라인 A 등급 수 연동, 일괄 출고, 출고 지시서·거래명세서 인쇄 |
| 재고 | F3 |  | `Feature.Stock` | 상품별 창고·주문에 묶인·판매 가능 팩, 부족 표시, 입고 | 재고 저장, A 등급 포장 입고, 배송 완료 시 차감 |
| 통계 | F4 |  | `Feature.Stats` | 기간별 주문 수·매출·판매 팩·취소율, 상품별·시간대별 막대 | DB 기간 조회, 날짜 고르기, 엑셀 내보내기, 마감 보고서 |
| 고객·문의 | F5 |  | `Feature.Customers` | 연락처로 묶은 고객 목록, 주문 이력 (두 번 누르면 주문 탭) | 문의 받기·답변, 자주 쓰는 답변, 고객 메모 |
| 검사 성적서 | F6 |  | `Feature.Certificate` | 출고한 주문 목록, 주문별 검사 성적서 미리보기 (예시 값) | 라인 검사 결과(DB)와 연결, 성적서 인쇄·PDF, 홈페이지 주문 조회에서 보기 |
| 라인 메시지 | F7 |  | `Feature.Messages` | 현장 관리자와 메시지 (관리자 프로그램 채팅과 같은 방), 주문 붙여 보내기·[주문 열기], 자주 쓰는 문구, 안 읽은 수 배지 | 형식 확인(서버 담당), 메시지 기록 저장, 새 메시지 알림, @부르기 |

- 담당이 정해지면 이 표와 각 `.csproj` 맨 위 주석 `담당: (미정)`을 고친다.
- 각 탭 ViewModel 파일 맨 위에 **할 일(TODO)** 목록이 있다.

## 실행

1. Visual Studio 2022에서 `BareunOrder.sln` 열기
2. 시작 프로젝트: `BareunOrder.Shell`
3. 실행 → 가짜 주문 8건이 들어 있고, 30초마다 새 주문이 하나씩 들어온다 (오른쪽 아래 알림)

실제 홈페이지 주문을 받으려면 `settings.json`에서 `"UseFakeOrders": false`, `MqttHost`를 서버 PC 주소로.
개인 설정은 `settings.local.json`에 (git에 안 올라감).

## 규칙 (관리자 프로그램과 같음)

- **자기 탭 폴더만 고친다.** `Core`, `Shell`, `Directory.*.props`는 공통 담당에게 요청.
- 주문 상태를 바꿀 때는 꼭 `OrderStore.ChangeStatusAsync()`를 쓴다 → 홈페이지에 알리고 작업 기록이 남는다.
- 주문 목록은 `OrderStore.Orders` 하나를 모든 탭이 같이 본다. 거를 때는 `new ListCollectionView(store.Orders)`로 **자기 탭 전용 보기**를 만든다 (`GetDefaultView`는 다른 탭과 섞임).
- 다른 탭으로 넘어가기: `navigator.Navigate("주문")`, 특정 주문 열기: `store.RequestFocus(order)` (고객·문의 탭 참고).
- 탭 배지: ViewModel에 `IHasBadge`를 붙이고 `BadgeCount`를 바꾸면 탭 옆에 숫자가 뜬다 (주문·출고 탭 참고).
- 색·버튼·카드는 `Core/Themes/Common.xaml` 스타일만 쓴다 → 화면이 하나처럼 보인다.
- 브랜치에서 작업 → PR. 공통 부분을 건드렸으면 PR 제목에 `[공통]`.

## 화면 원칙 (현장용)

- 본문 14, 큰 숫자 20~30. 버튼 높이 40 이상 (주요 버튼 48).
- 상태는 **색 + 글자**를 같이 쓴다. 상태 색은 모든 탭에서 같다.
  - 주문 접수 파랑 / 출고 검사 주황 / 배송 중 청록 / 배송 완료 초록 / 취소 회색
- 위쪽 줄: 연결 상태, 오늘 주문·처리 대기·매출, 시계. 아래 줄: 단축키, 마지막 작업.
- 키보드: F1~F7 탭 이동, 주문 목록에서 Enter = 다음 단계.
- 연락처는 가려서 보이고, 필요할 때만 [번호 보기].

## 통신 (주문_통신_규약 v1.0, 조시현)

토픽·메시지 형식은 **주문_통신_규약 v1.0**을 따른다. Core가 처리하는 것:

| 받는 토픽 | Core가 하는 일 |
|---|---|
| `order/new` | 형식 검사 → 같은 번호 확인(내용 같으면 ack만 다시, 다르면 `DUPLICATE_NO`) → 가격표로 다시 계산(`PRICE_MISMATCH`) → 목록 추가 → `order/accepted` → `order/status/<no>`(received, 시각 = 주문 ts) → `order/ack` |
| `order/cancel` | 연락처 숫자가 같고 "주문 접수" 상태일 때만 취소 → `order/status/<no>` canceled. 아니면 아무것도 안 함 |
| `inquiry/new` | 형식 검사 → 같은 번호 확인 → `Inquiries`에 추가 → `inquiry/accepted` → `inquiry/ack` |

탭에서 쓰는 Core 메서드 (`OrderStore`):

| 메서드 | 보내는 토픽 | 쓰는 탭 |
|---|---|---|
| `ChangeStatusAsync(order, 상태)` | `order/status/<no>` (retained, `by` 포함) | 주문, 출고·배송 |
| `SetShippingInfoAsync(order, 택배사, 송장)` | `order/status/<no>` | 출고·배송 |
| `SetCertNoAsync(order, 성적서번호)` | `order/status/<no>` (`cert_no`) | 검사 성적서 |
| `AnswerAsync(inquiry, 답변)` | `inquiry/answer/<no>` (retained) | 고객·문의 (`store.Inquiries`, `store.NewInquiry`) |
| `PublishStockAsync(상품id, "restock"/"adjust", 팩)` | `order/stock` | 재고 |
| `PublishRunAsync(order, run_id, 개수)` | `order/run` | 출고·배송 |

- 주문번호 `BR-YYYYMMDD-XXXX`, 문의번호 `Q-YYYYMMDD-XXXX` (XXXX = 영문 대문자·숫자 4자, 0 O 1 I 제외). 검사 규칙은 `Core/Comm/OrderJson.cs`
- 브로커에 못 붙은 동안 보낼 메시지는 모아 뒀다가 다시 붙으면 보낸다.

## 라인 메시지

관리자 프로그램 채팅과 같은 토픽 `chat/messages`, 같은 형식(snake_case)을 쓴다. 주문 관리 프로그램은 `station: "주문 관리"`로 보내고, 주문을 붙이면 `ref_kind: "order"`, `ref_text: 주문번호`.
저장은 DB가 아니라 실행 폴더 `data/chat/날짜.jsonl` (관리자 프로그램과 같은 방식), 켤 때 지난 7일을 불러온다.
다른 탭에서 라인에 요청할 때: `chatStore.SendAsync("…", order.No)` (출고·배송 탭 참고).

## 범위 밖

실제 결제, 회원 인증(비회원 주문만), 실제 택배사 연동.
화면의 이름·주소·연락처·금액은 모두 예시 데이터.
