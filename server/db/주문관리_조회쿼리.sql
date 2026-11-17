-- 주문 관리 프로그램 탭별 조회 쿼리 (2026-11-17, 주문 통신 규약 v1.0 기준)
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

-- [검사 성적서] 주문에 나간 회차들의 A 등급 측정 평균
-- (전압을 측정한 A 등급만 들어간다. 조인 칸 run_id, cycle_id는 지금 DB 구조와 맞는 것을 확인함)
SELECT COUNT(*) AS cnt,
       AVG(e.no_load_voltage_v) AS v_open,
       AVG(e.loaded_voltage_v) AS v_load,
       AVG(e.estimated_internal_resistance_ohm) AS r_int
FROM order_runs r
JOIN runs ru ON ru.source_run_id = r.source_run_id
JOIN battery_cycles b ON b.run_id = ru.run_id AND b.final_grade = 'A'
JOIN electrical_tests e ON e.cycle_id = b.cycle_id
WHERE r.order_no = ?;

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
