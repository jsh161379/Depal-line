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

## v4.6 (11/19, 조시현) — F2·F3 라인 연동 (담당자 대신 먼저 반영)
- F2 출고·배송: "라인 A 등급 남음 (출고 가능)" = LineSupplyReader.Available (예시 값 60 삭제)
  - [출고 처리]: 회차 연결(order/run, 오래된 회차부터) → 송장 저장 → 배송 중. A가 모자라면 "회차 연결 없이 출고할까요? (성적서 발급 불가)" 확인
  - 버튼 글자 "출고 처리 (배송 중으로)" → "출고 처리"
- F3 재고: 실제 모드는 켤 때 DB stock 표에서 창고 팩 수 읽기 + [새로 고침], 입고는 order/stock(restock)으로 DB 저장
  - "주문에 묶임"에 배송 중 포함 (재고는 배송 완료 때 빠지므로)
  - 라인 A 숫자 = LineSupplyReader.Available
- 남은 것: 포장 입고와 "남은 A"를 어떻게 맞출지 (주문 통신 규약 7장 결정 ⑦)

## v4.7 (11/21, 조시현) — 11/20 팀 결정 반영 (⑦⑨)
- ⑦ LineSupplyReader: A 등급 = `final_grade = 'A'` **+ `sorted_destination IS NOT NULL`**(실제로 A 자리에 배출된 것만, 배출 실패는 dest가 null), LINE 회차만(BENCH_TEST 제외). "오늘 A"도 같은 조건
- ⑨ 충전지(aar2) 판매 중지: `Product.OnSale`(기본 true), aar2는 `OnSale: false`로 **목록에 남김** (지난 주문·통계·DB 기록 유지)
  - `Products.OnSale` = 판매 중인 것만. 새 주문 검사(OrderJson)는 판매 중지 상품이면 BAD_FORMAT, 가짜 주문·F3 재고 목록은 OnSale만
  - `Products.All`(통계 등)은 그대로 aar2 포함. 화면에 "(판매 중지)"를 붙이려면 `Product.Label`
  - 홈페이지 v9(PRODUCTS에서 aar2 뺌), 가짜 주문 관리(fake_order_admin.py)도 같게
- 성적서 합격 기준(⑩)은 F6 담당: `grading_rules`에서 읽기 (통신 규약 v1.5.2, 메시지_형식 v1.4.4 5-1)

## v4.8 (11/21, 조시현) — 상품 AA 8개입 추가
- `aa8` AA 알카라인 8개입 6,200원 (팔레트 한 판 2×4와 같은 구성, 홈페이지 v10과 같게). 가짜 주문·재고·통계는 상품 목록을 읽어 자동 반영, F3 가짜 모드 예시 재고 20팩
- 가짜 주문 관리(fake_order_admin.py) 가격표·시험 실행 파일 주소(v10)도 같게

## v4.9 (11/21, 조시현) — 상품 8개입 → 6개입 (팔레트 2×3)
- 11/21 팀 결정: 팔레트가 2줄 × 3칸(6개)으로 바뀌어 `aa8` 8개입 6,200원 → **`aa6` AA 알카라인 6개입 4,700원** (개당 약 783원, 4개입 800원과 12개입 742원 사이). 발표 설명은 "한 판 크기 = 한 팩"
- aa8은 같은 날 추가해 실제 주문이 없으므로 판매 중지로 남기지 않고 바꿈. DB 상품 데이터·가짜 주문 데이터의 aa8 → aa6은 장성찬, 홈페이지 8개입 → 6개입은 김초아
- 가짜 주문 관리(fake_order_admin.py) 가격표, F3 가짜 모드 예시 재고(aa6 20팩)도 같게

## v5.0 (11/23, 조시현) — ⑪ 재고 = 라인 A (안 A, 11/23 팀 결정)
- 포장 입고 단계를 두지 않고 라인 검사를 통과한 A가 바로 출고된다. F3 재고는 라인 A 등급 수로 자동 계산
- Core `LineSupplyReader`: `PendingNeed`(접수·출고 검사 주문이 가져갈 A 수, 배송 중은 출고 때 회차 연결로 이미 빠져서 뺌), `Free`(= 남음 − 대기분), `FreePacksFor(상품)` 추가
- F3 `StockViewModel`: 화면 이름은 그대로 두고 뜻만 바꿈
  - `Packs` = 라인 A 남음 ÷ 팩당 개수 (예전: DB stock 창고 팩)
  - `Reserved` = 접수·출고 검사 주문의 이 상품 팩 수 (예전: 배송 중 포함)
  - `Available` = (라인 A − 모든 주문 대기분) ÷ 팩당 개수 → 상품끼리 같은 A를 나눠 쓰므로 "이 상품만 판다면"의 값
  - DB `stock` 읽기·손 입고(`order/stock`)·입고 기록 삭제, [새로 고침]은 라인 A 다시 읽기. 안내 `StockNote`, `PendingNeedA`·`FreeA`·`SupplyText` 추가
- F3 화면(입고 카드 삭제·머리글)은 김하균, 배송 완료 `stock` 차감 끄기는 장성찬 (DB 저장 프로그램)
- `OrderStore.PublishStockAsync`(order/stock)는 지우지 않고 남김 (안 B를 나중에 할 때 쓸 수 있게, 지금은 부르는 곳 없음)

## v5.1 (11/23, 조시현) — 설정 파일이 깨졌을 때 알리기 (포트폴리오 ㊳)
- 11/23 재시험 중 settings.local.json 줄 끝 쉼표가 빠져 파일 전체가 조용히 무시됨 → settings.json으로 켜져 DB 연결 없이 예시 라인 A(43개)가 보이고, F2 출고가 예시 회차 번호로 order/run을 보내 DB에서 버려짐(성적서 발급 불가)
- Core `AppSettings`: 못 읽은 파일과 줄 번호를 `LoadWarning`에, 읽은 파일을 `LoadedFrom`에 남김. 마지막 줄 뒤 쉼표는 봐 줌(AllowTrailingCommas)
- Shell `App`: 켤 때 `LoadWarning`이 있으면 경고 창, 작업 기록에 "설정: settings.local.json"처럼 어느 설정으로 켰는지 남김
- Core `LineSupplyReader`: 실제 모드인데 DB 연결 설정이 없으면 예시 값 대신 0개 + 아래 줄 빨간 글씨("DB 연결 설정이 없습니다") → 예시 회차로 출고되지 않음 (가짜 모드는 예시 값 그대로)
