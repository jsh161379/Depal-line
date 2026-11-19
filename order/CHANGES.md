# 바뀐 점

## v4 (11/17) — 주문_통신_규약 v1.0 반영 (Core)
- 주문 받기 순서: 형식 검사 → 같은 번호 확인 → 가격 다시 계산 → order/accepted → order/status(received) → order/ack
- ack 오류: BAD_FORMAT / PRICE_MISMATCH / DUPLICATE_NO
- order/cancel 처리 (연락처 일치 + 주문 접수 상태일 때만)
- 고객 문의: inquiry/new 받기 → inquiry/accepted, inquiry/ack. 답변 AnswerAsync → inquiry/answer/<no>
- 상태 메시지에 by, cert_no 추가. 재고 order/stock, 회차 order/run 보내는 메서드 추가
- 주문번호 인식: 끝 4자를 영문·숫자로 (BR-\d{8}-[A-HJ-NP-Z2-9]{4})
- 라인 메시지 저장: data/chat/날짜.jsonl, 켤 때 지난 7일 불러오기
- 브로커가 끊긴 동안 보낼 메시지는 모아 뒀다가 다시 붙으면 보냄
- 연결(IOrderClient)은 받기·보내기만, 검사·판단은 OrderStore가 함

## v4.1 (11/17, 조시현) — 규약 시각 규칙 + F5
- order/accepted·inquiry/accepted의 ts = 주문 관리가 받은 시각(서버 PC 시계), 홈페이지 시각은 client_ts (규약 3·4장 11/17 규칙)
  - 고객 PC 시계가 빠르면 뒤에 오는 상태 메시지가 DB에서 '오래된 것'으로 거부됨 → 실제 MySQL 시험에서 확인
  - 첫 order/status(received)의 ts도 받은 시각(AcceptedAt) → DB 첫 상태 기록과 같아 한 줄만 남음
- F5 고객·문의: Core의 Inquiries·AnswerAsync를 쓰는 문의 목록·답변 화면, 자주 쓰는 답변, 고객 메모, 문의 기록 파일(data/inquiries.json), 탭 배지 = 답변 대기 수

## v4.2 (11/17, 조시현) — 켤 때 DB에서 불러오기
- 실제 모드에서 켜면 MySQL(ui_reader, 읽기 전용)에서 최근 30일 주문·상품·문의를 읽어 옴 → 껐다 켜도 고객·주문·문의가 남음
  - 30일보다 오래돼도 안 끝난 주문(접수·검사·배송 중)과 답변 안 한 문의는 읽음
  - 브로커에 붙기 전에 읽어서, 홈페이지가 다시 보낸 주문을 같은 번호로 알아봄
- 설정 추가: MySqlConnection(비밀번호 포함 → settings.local.json에만), DbLoadDays(기본 30)
- DB 연결이 안 되면 작업 기록에 '실패'만 남기고 평소처럼 새 주문을 받음
- 패키지 추가: MySqlConnector 2.6.1 (2.3.7은 보안 경고 GHSA-473q-m89c-ghf8 → 고친 버전)
- 성적서 번호(cert_no)는 orders 표에 없어서 아직 안 불러옴

## v4.3 (11/17, 조시현) — F7 라인 메시지 마무리 + 제목 줄
- @이름으로 사람 부르기 (관리자 프로그램 채팅과 같은 규칙, chat/messages의 mentions)
  - 글 속 @이름 자동 인식, [부를 사람]에서 고르면 입력칸에 @이름이 들어감 → Shipping 탭에서 보내는 메시지도 똑같이 적용(ChatStore)
  - 나 = settings의 MqttClientId(아이디) 또는 Operator(이름). 나를 부른 메시지는 노란색 강조 + 소리
  - 탭을 안 보는 동안 나를 부른 메시지가 있으면 탭을 열 때 위에 안내, [나를 부른 것만] 보기
- 라인 메시지 알림: 다른 탭을 보고 있을 때 오른쪽 아래(주문 알림 위)에 8초 동안 표시, [메시지 보기]로 탭 이동
- 가짜 모드에 주문 관리를 부르는 라인 메시지 1종 추가 (시험용)
- 메인 창: 윈도우 기본 흰 제목 줄 대신 위쪽 상태줄을 제목 줄로 (최소화·최대화·닫기 직접 그림)

## v4.4 (11/18, 조시현) — 성적서 번호 형식
- 성적서 번호 `CERT-YYYYMMDD-XXXX` (주문번호의 날짜·4자 그대로, F6 담당 결정 → 주문 통신 규약 v1.0.1)
- `OrderJson.CertNo`(형식 검사), `OrderJson.CertNoFor(주문번호)`(만들기) 추가. F6에서는 `store.SetCertNoAsync(order, OrderJson.CertNoFor(order.No)!)`
- `SetCertNoAsync`는 형식이 틀리면 ArgumentException
- 켤 때 DB에서 `orders.cert_no`도 불러옴 (DB 담당 수정본으로 칸 추가됨)

## v4.5 (11/19, 조시현) — 라인 A 등급 남은 수 (F2·F3 공통)
- `Core/Data/LineSupply.cs` 추가: `LineSupplyReader` (DI 싱글톤, App 시작 때 StartAsync)
  - 출고 가능 A = Σ 회차별 (A 등급 수 − order_runs로 이미 연결된 수). 주문 통신 규약 v1.0.2 6장 안 A
  - 실제 모드: ui_reader로 DB 조회, 켤 때 + 1분마다 + `RefreshAsync()`. 최근 DbLoadDays일, run_type 'LINE'만
  - 가짜 모드(또는 MySqlConnection 없음): 예시 회차 3개 (남은 A 43개, 오늘 A 37개)
  - 화면용: `Available`, `TodayA`, `Runs`, `StatusText`, `Changed` 이벤트
  - 출고용: `Plan(개수)` → 오래된 회차부터 나눈 `RunTake` 목록 (모자라면 null). 각각 `store.PublishRunAsync(order, t.RunId, t.Count)`
  - 재고용: `PacksFor(product)` = 남은 A로 포장할 수 있는 팩 수
- `OrderStore.RunLinked` 이벤트: PublishRunAsync 뒤에 알림 → 가짜 모드는 바로 빼고, 실제 모드는 2초 뒤 DB 다시 읽음
- 메인 창 아래 줄 왼쪽에 "라인 A 남음 N개 · 오늘 A N개 · 시각" 항상 표시 (읽기 실패면 빨간 글씨로 이유). 작업 기록에도 값이 바뀔 때 한 줄
- 확인 (11/19 서버 PC): 가짜 모드 43개(예시), 실제 DB 40개 (시험 데이터 3 + 그동안 돌린 실제 회차 37)
