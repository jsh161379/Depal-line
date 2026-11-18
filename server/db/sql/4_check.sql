-- 주문 테이블 확인 (2026-11-17, 11-18 cert_no·visual_inspections 권한): 표·칸, 건수, 최근 주문·상태 기록·문의
USE `aa_battery_inspection`;
SELECT TABLE_NAME AS `table`, COUNT(*) AS columns_n
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = 'aa_battery_inspection'
  AND TABLE_NAME IN ('orders','order_items','order_status_log','order_runs','stock','stock_log','inquiries')
GROUP BY TABLE_NAME ORDER BY TABLE_NAME;
SELECT 'orders' AS tbl, COUNT(*) AS n FROM orders
UNION ALL SELECT 'order_items', COUNT(*) FROM order_items
UNION ALL SELECT 'order_status_log', COUNT(*) FROM order_status_log
UNION ALL SELECT 'stock', COUNT(*) FROM stock
UNION ALL SELECT 'stock_log', COUNT(*) FROM stock_log
UNION ALL SELECT 'inquiries', COUNT(*) FROM inquiries;
SELECT order_no, received_at, accepted_at, order_status, courier, tracking_no, cert_no, total_amount FROM orders ORDER BY received_at DESC LIMIT 5;
SELECT order_no, from_status, to_status, changed_at, changed_by FROM order_status_log ORDER BY status_log_id DESC LIMIT 8;
SELECT inquiry_id, asked_at, inquiry_type, LEFT(question, 20) AS question, LEFT(answer, 20) AS answer, answered_at, answered_by FROM inquiries ORDER BY asked_at DESC LIMIT 5;
SELECT product_id, stock_packs, updated_at FROM stock ORDER BY product_id;
SELECT GRANTEE, TABLE_NAME FROM information_schema.TABLE_PRIVILEGES
WHERE TABLE_SCHEMA = 'aa_battery_inspection' AND TABLE_NAME IN ('orders','inquiries','visual_inspections') ORDER BY GRANTEE, TABLE_NAME;
