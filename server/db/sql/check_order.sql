-- 주문 하나 확인 (check_order.bat이 실행, @arg = 주문번호 또는 빈칸)
SET @no = IF(@arg = '', (SELECT order_no FROM orders ORDER BY received_at DESC LIMIT 1), @arg);

-- ① 주문
SELECT order_no, received_at, order_status, total_amount, cert_no
FROM orders WHERE order_no = @no;

-- ② 상품
SELECT product_id, quantity, unit_price FROM order_items WHERE order_no = @no;

-- ③ 연결된 회차: in_db = 1이어야 실제 회차 (0이면 예시 회차 번호로 연결된 것 → 성적서 발급 불가)
SELECT r.source_run_id, r.battery_count, ru.run_id IS NOT NULL AS in_db, ru.run_type
FROM order_runs r LEFT JOIN runs ru ON ru.source_run_id = r.source_run_id
WHERE r.order_no = @no;

-- ④ 오늘 주문 수·매출 (취소 제외) — 주문 관리 F4 통계 "오늘"과 같아야 함
SELECT COUNT(*) AS today_orders, COALESCE(SUM(total_amount), 0) AS today_sales
FROM orders WHERE DATE(received_at) = CURDATE() AND order_status <> 'canceled';

-- ⑤ 창고 stock 표 — 11/23 ⑪ 안 A: 배송 완료 전후로 숫자가 같아야 함 (차감 끔)
SELECT product_id, stock_packs FROM stock;
