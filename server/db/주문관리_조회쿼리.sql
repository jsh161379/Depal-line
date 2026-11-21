-- 주문 관리 프로그램 탭별 조회 쿼리 (2026-11-17, 주문 통신 규약 v1.0 기준 / 11-20 고객 유형·연령대·지역 통계 추가)
-- 제안서 쿼리를 확정한 칸 이름으로 바꾼 것. ? 자리는 프로그램에서 값을 넣는다.
-- 기간 조건은 "시작일 <= 시각 < 종료일 다음 날" 형태로 써서 인덱스를 쓰게 했다.

-- [주문] 목록 (최근 순)
SELECT order_no, received_at, customer_name, customer_phone, total_amount, order_status, courier, tracking_no
FROM orders
WHERE received_at >= ? AND received_at < ?
ORDER BY received_at DESC;

-- [주문] 상세: 상품 줄
SELECT product_id, product_name, quantity, unit_price, quantity * unit_price AS amount
FROM order_items WHERE order_no = ? ORDER BY order_item_id;

-- [주문] 상세: 상태 이력
SELECT from_status, to_status, changed_at, changed_by
FROM order_status_log WHERE order_no = ? ORDER BY changed_at, status_log_id;

-- [통계] 기간별 주문 수·매출 (취소 제외)
SELECT DATE(received_at) AS day, COUNT(*) AS orders, SUM(total_amount) AS sales
FROM orders
WHERE received_at >= ? AND received_at < ? AND order_status <> 'canceled'
GROUP BY DATE(received_at) ORDER BY day;

-- [통계] 상품별 매출 (취소 제외)
SELECT i.product_id, SUM(i.quantity) AS packs, SUM(i.quantity * i.unit_price) AS sales
FROM order_items i JOIN orders o ON o.order_no = i.order_no
WHERE o.received_at >= ? AND o.received_at < ? AND o.order_status <> 'canceled'
GROUP BY i.product_id;

-- [통계] 고객 유형별 주문 수·매출 (personal / company, 취소 제외)  — 11-20 추가
SELECT customer_type, COUNT(*) AS orders, SUM(total_amount) AS sales
FROM orders
WHERE received_at >= ? AND received_at < ? AND order_status <> 'canceled'
GROUP BY customer_type;

-- [통계] 연령대별 주문 수·매출 (개인 고객만, 취소 제외). none = 선택 안 함 (화면에서 따로 묶어 표시)
SELECT age_group, COUNT(*) AS orders, SUM(total_amount) AS sales
FROM orders
WHERE received_at >= ? AND received_at < ? AND order_status <> 'canceled' AND customer_type = 'personal'
GROUP BY age_group
ORDER BY FIELD(age_group, '10s', '20s', '30s', '40s', '50s+', 'none');

-- [통계] 지역별 주문 수·매출 (취소 제외). 지역을 모르는 주문은 none (화면에는 '선택 안 함')
SELECT region, COUNT(*) AS orders, SUM(total_amount) AS sales
FROM orders
WHERE received_at >= ? AND received_at < ? AND order_status <> 'canceled'
GROUP BY region
ORDER BY orders DESC;

-- [통계] 지역별 많이 팔린 상품 순위 (지역마다 상위 3개, 판매 팩 수 기준, 취소 제외)
SELECT region, product_id, product_name, packs, sales, rank_no
FROM (
    SELECT o.region AS region, i.product_id, MAX(i.product_name) AS product_name,
           SUM(i.quantity) AS packs, SUM(i.quantity * i.unit_price) AS sales,
           ROW_NUMBER() OVER (PARTITION BY o.region
                              ORDER BY SUM(i.quantity) DESC, SUM(i.quantity * i.unit_price) DESC) AS rank_no
    FROM order_items i JOIN orders o ON o.order_no = i.order_no
    WHERE o.received_at >= ? AND o.received_at < ? AND o.order_status <> 'canceled'
    GROUP BY o.region, i.product_id
) ranked
WHERE rank_no <= 3
ORDER BY region, rank_no;

-- [통계] 연령대별 많이 팔린 상품 순위 (개인 고객, 연령대마다 상위 3개, 취소 제외)
SELECT age_group, product_id, product_name, packs, sales, rank_no
FROM (
    SELECT o.age_group, i.product_id, MAX(i.product_name) AS product_name,
           SUM(i.quantity) AS packs, SUM(i.quantity * i.unit_price) AS sales,
           ROW_NUMBER() OVER (PARTITION BY o.age_group
                              ORDER BY SUM(i.quantity) DESC, SUM(i.quantity * i.unit_price) DESC) AS rank_no
    FROM order_items i JOIN orders o ON o.order_no = i.order_no
    WHERE o.received_at >= ? AND o.received_at < ? AND o.order_status <> 'canceled' AND o.customer_type = 'personal'
    GROUP BY o.age_group, i.product_id
) ranked
WHERE rank_no <= 3
ORDER BY age_group, rank_no;

-- [재고] 창고 팩 수, 주문에 묶인 팩 수, 바로 팔 수 있는 팩 수
-- 재고는 배송 완료(delivered) 때 빠지므로, 배송 중(shipping)인 주문도 묶인 수에 넣는다.
SELECT s.product_id, s.stock_packs,
       COALESCE(r.reserved, 0) AS reserved,
       s.stock_packs - COALESCE(r.reserved, 0) AS available,
       s.updated_at
FROM stock s
LEFT JOIN (
    SELECT i.product_id, SUM(i.quantity) AS reserved
    FROM order_items i JOIN orders o ON o.order_no = i.order_no
    WHERE o.order_status IN ('received', 'inspect', 'shipping')
    GROUP BY i.product_id
) r ON r.product_id = s.product_id
ORDER BY s.product_id;

-- [재고] 입고·출고 기록
SELECT logged_at, product_id, delta_packs, packs_after, reason, order_no, changed_by
FROM stock_log
WHERE logged_at >= ? AND logged_at < ?
ORDER BY logged_at DESC, stock_log_id DESC;

-- [출고·재고] 오늘 라인 검사 통과 A 등급 수
SELECT COUNT(*) AS a_grade_today
FROM battery_cycles
WHERE final_grade = 'A'
  AND finished_at >= CURDATE() AND finished_at < CURDATE() + INTERVAL 1 DAY;

-- [검사 성적서] 주문 정보와 성적서 번호
SELECT order_no, received_at, order_status, courier, tracking_no, cert_no
FROM orders WHERE order_no = ?;

-- [검사 성적서] 주문에 나간 회차들의 A 등급 요약 (전기 측정 + 외관·극 방향 검사)
-- cnt는 A 등급 전체 수. 측정·검사 기록이 없는 건전지는 평균에서 빠지고 *_missing으로 따로 센다.
-- visual_polarity는 카메라가 본 극 방향(minus_up / plus_up / unknown), electrical polarity는 측정으로 본 극성이다.
SELECT COUNT(*)                                          AS cnt,
       COUNT(e.cycle_id)                                 AS measured,
       AVG(e.no_load_voltage_v)                          AS v_open,
       MIN(e.no_load_voltage_v)                          AS v_open_min,
       MAX(e.no_load_voltage_v)                          AS v_open_max,
       AVG(e.loaded_voltage_v)                           AS v_load,
       AVG(e.estimated_internal_resistance_ohm)          AS r_int,
       MAX(e.estimated_internal_resistance_ohm)          AS r_int_max,
       SUM(e.polarity = 'normal')                        AS elec_polarity_normal,
       COUNT(v.cycle_id)                                 AS visual_inspected,
       SUM(v.visual_result = 'PASS')                     AS visual_pass,
       SUM(v.visual_result = 'FAIL')                     AS visual_fail,
       SUM(v.visual_result = 'ERROR')                    AS visual_error,
       AVG(v.confidence)                                 AS visual_confidence,
       SUM(v.polarity = 'minus_up')                      AS visual_minus_up,
       SUM(v.polarity = 'plus_up')                       AS visual_plus_up,
       SUM(v.cycle_id IS NOT NULL AND (v.polarity = 'unknown' OR v.polarity IS NULL)) AS visual_polarity_unknown,
       COUNT(*) - COUNT(e.cycle_id)                      AS electrical_missing,
       COUNT(*) - COUNT(v.cycle_id)                      AS visual_missing
FROM order_runs r
JOIN runs ru            ON ru.source_run_id = r.source_run_id
JOIN battery_cycles b   ON b.run_id = ru.run_id AND b.final_grade = 'A'
LEFT JOIN electrical_tests e   ON e.cycle_id = b.cycle_id
LEFT JOIN visual_inspections v ON v.cycle_id = b.cycle_id
WHERE r.order_no = ?;

-- [검사 성적서] 회차별로 나눠 보기 (위와 같은 값을 회차마다)
SELECT r.source_run_id, r.battery_count, ru.started_at,
       COUNT(b.cycle_id)                        AS a_grade,
       AVG(e.no_load_voltage_v)                 AS v_open,
       AVG(e.loaded_voltage_v)                  AS v_load,
       AVG(e.estimated_internal_resistance_ohm) AS r_int,
       SUM(v.visual_result = 'PASS')            AS visual_pass,
       AVG(v.confidence)                        AS visual_confidence,
       SUM(v.polarity = 'minus_up')             AS visual_minus_up,
       SUM(v.polarity = 'plus_up')              AS visual_plus_up
FROM order_runs r
JOIN runs ru            ON ru.source_run_id = r.source_run_id
LEFT JOIN battery_cycles b     ON b.run_id = ru.run_id AND b.final_grade = 'A'
LEFT JOIN electrical_tests e   ON e.cycle_id = b.cycle_id
LEFT JOIN visual_inspections v ON v.cycle_id = b.cycle_id
WHERE r.order_no = ?
GROUP BY r.source_run_id, r.battery_count, ru.started_at
ORDER BY ru.started_at;

-- [검사 성적서] 주문에 연결된 회차 목록
SELECT r.source_run_id, r.battery_count, ru.started_at, ru.ended_at
FROM order_runs r LEFT JOIN runs ru ON ru.source_run_id = r.source_run_id
WHERE r.order_no = ? ORDER BY ru.started_at;

-- [고객] 연락처별 주문 수·누적 금액 (취소 제외)
SELECT customer_phone, MAX(customer_name) AS name, COUNT(*) AS orders,
       SUM(total_amount) AS spent, MAX(received_at) AS last_order
FROM orders WHERE order_status <> 'canceled'
GROUP BY customer_phone ORDER BY last_order DESC;

-- [고객·문의] 문의 목록 (답변 안 한 것 먼저). inquiry_id = 문의번호 Q-YYYYMMDD-XXXX
SELECT inquiry_id, asked_at, inquiry_type, customer_name, customer_phone, order_no, question, answer, answered_at, answered_by
FROM inquiries
ORDER BY answered_at IS NOT NULL, asked_at DESC;

-- [통계] 문의 유형별 건수 (delivery / cancel / product / bulk / etc)
SELECT inquiry_type, COUNT(*) AS inquiries, SUM(answered_at IS NULL) AS unanswered
FROM inquiries
WHERE asked_at >= ? AND asked_at < ?
GROUP BY inquiry_type;
