-- ============================================================================
-- 통계 탭(F4) 테스트용 가짜 주문  (2026-11-18, 장성찬)
--   오늘 / 최근 7일 / 이번 달 버튼이 각각 맞게 나오는지 보려고 만든 데이터.
--   날짜는 실행한 날 기준으로 계산하므로 아무 날에 실행해도 된다.
--   (단, '이번 달에만 있는 주문'은 1~5일에 넣으므로, 매달 12일 이후에 실행해야 7일과 겹치지 않는다)
--
--   ⚠ 테스트 DB에서만 실행. 주문번호 끝이 -TS로 시작하는 것만 넣고 지운다. 여러 번 실행해도 된다.
--   ⚠ 주문 테이블이 있어야 한다 (DB_변경_2026-11-17_주문.sql 먼저 실행).
--   지울 때: 맨 아래 [지우기]만 실행.
-- ============================================================================
USE `aa_battery_inspection`;

SET @OLD_SQL_SAFE_UPDATES = @@SQL_SAFE_UPDATES;
SET SQL_SAFE_UPDATES = 0;

-- 1) 이전 테스트 주문 지우기 (주문번호 BR-날짜-TS??)
DELETE FROM order_status_log WHERE order_no LIKE 'BR-________-TS__';
DELETE FROM order_items      WHERE order_no LIKE 'BR-________-TS__';
DELETE FROM orders           WHERE order_no LIKE 'BR-________-TS__';

-- 2) 주문 목록
--    base: T = 오늘에서 off일 전 / M = 이번 달 1일에서 off일 뒤 / L = 이번 달 1일에서 off일 전(지난달)
DROP TEMPORARY TABLE IF EXISTS t_order;
CREATE TEMPORARY TABLE t_order (
  code CHAR(2), base CHAR(1), off INT, hhmm TIME, status VARCHAR(10), name VARCHAR(50), phone VARCHAR(20)
);
INSERT INTO t_order VALUES
  -- 오늘
  ('A2', 'T', 0, '09:10', 'delivered', '김오늘', '010-0000-9101'),
  ('A3', 'T', 0, '10:20', 'received',  '이오늘', '010-0000-9102'),
  ('A4', 'T', 0, '13:40', 'canceled',  '박오늘', '010-0000-9103'),
  -- 최근 7일 (오늘 제외: 1·2·4·6일 전)
  ('B2', 'T', 1, '11:00', 'delivered', '김칠일', '010-0000-9201'),
  ('B3', 'T', 2, '15:30', 'shipping',  '이칠일', '010-0000-9202'),
  ('B4', 'T', 4, '09:45', 'delivered', '박칠일', '010-0000-9203'),
  ('B5', 'T', 6, '17:20', 'canceled',  '최칠일', '010-0000-9204'),
  -- 이번 달에만 (1~5일)
  ('C2', 'M', 0, '10:00', 'delivered', '김이달', '010-0000-9301'),
  ('C3', 'M', 1, '14:00', 'delivered', '이이달', '010-0000-9302'),
  ('C4', 'M', 2, '09:30', 'delivered', '박이달', '010-0000-9303'),
  ('C5', 'M', 3, '16:10', 'canceled',  '최이달', '010-0000-9304'),
  ('C6', 'M', 4, '11:50', 'delivered', '정이달', '010-0000-9305'),
  -- 지난달 (어느 버튼에도 나오면 안 됨)
  ('D2', 'L', 5, '10:00', 'delivered', '김지난', '010-0000-9401');

-- 3) 상품 줄 (가격은 프로그램 Products.cs와 같게)
DROP TEMPORARY TABLE IF EXISTS t_item;
CREATE TEMPORARY TABLE t_item (code CHAR(2), product_id VARCHAR(10), qty INT);
INSERT INTO t_item VALUES
  ('A2', 'aa12', 2),
  ('A3', 'aa4', 1), ('A3', 'aar2', 1),
  ('A4', 'aa24', 1),
  ('B2', 'aa24', 2),
  ('B3', 'aa12', 1), ('B3', 'aa4', 2),
  ('B4', 'aar2', 3),
  ('B5', 'aa4', 1),
  ('C2', 'aa24', 3),
  ('C3', 'aa12', 4),
  ('C4', 'aa4', 5),
  ('C5', 'aar2', 1),
  ('C6', 'aa24', 1), ('C6', 'aa12', 1),
  ('D2', 'aa24', 10);

-- 주문 시각·주문번호를 미리 계산
-- (MySQL은 임시 표를 한 쿼리에서 두 번 열 수 없어서, 표를 만든 뒤 UPDATE로 주문번호를 채움)
DROP TEMPORARY TABLE IF EXISTS t_calc;
CREATE TEMPORARY TABLE t_calc AS
SELECT code, status, name, phone,
       TIMESTAMP(CASE base
                   WHEN 'T' THEN CURDATE() - INTERVAL off DAY
                   WHEN 'M' THEN DATE_FORMAT(CURDATE(), '%Y-%m-01') + INTERVAL off DAY
                   ELSE          DATE_FORMAT(CURDATE(), '%Y-%m-01') - INTERVAL off DAY
                 END, hhmm) AS d,
       CAST('' AS CHAR(20)) AS order_no
FROM t_order;
UPDATE t_calc SET order_no = CONCAT('BR-', DATE_FORMAT(d, '%Y%m%d'), '-TS', code);

-- 4) orders
INSERT INTO orders (order_no, received_at, accepted_at, customer_name, customer_phone, shipping_address, memo,
                    total_amount, order_status, courier, tracking_no, cert_no, updated_at)
SELECT c.order_no, c.d, c.d, c.name, c.phone, '테스트 주소 (통계 확인용)', '통계 테스트',
       (SELECT SUM(i.qty * CASE i.product_id WHEN 'aa4' THEN 3200 WHEN 'aa12' THEN 8900
                                             WHEN 'aa24' THEN 15900 WHEN 'aar2' THEN 9800 END)
          FROM t_item i WHERE i.code = c.code),
       c.status,
       IF(c.status IN ('shipping', 'delivered'), 'CJ대한통운', NULL),
       IF(c.status IN ('shipping', 'delivered'), CONCAT('61000000', LPAD(ORD(SUBSTRING(c.code, 1, 1)) * 10 + RIGHT(c.code, 1), 4, '0')), NULL),
       NULL,
       c.d
FROM t_calc c;

-- 5) order_items
INSERT INTO order_items (order_no, product_id, product_name, quantity, unit_price)
SELECT c.order_no, i.product_id,
       CASE i.product_id WHEN 'aa4' THEN 'AA 알카라인 4개입' WHEN 'aa12' THEN 'AA 알카라인 12개입'
                         WHEN 'aa24' THEN 'AA 알카라인 24개입' WHEN 'aar2' THEN 'AA 충전지 2개입' END,
       i.qty,
       CASE i.product_id WHEN 'aa4' THEN 3200 WHEN 'aa12' THEN 8900 WHEN 'aa24' THEN 15900 WHEN 'aar2' THEN 9800 END
FROM t_item i JOIN t_calc c ON c.code = i.code;

-- 6) order_status_log (다른 탭에서 상세를 열어도 이상하지 않게: 접수 → 지금 상태)
INSERT INTO order_status_log (order_no, from_status, to_status, changed_at, changed_by)
SELECT order_no, NULL, 'received', d, '테스트' FROM t_calc;
INSERT INTO order_status_log (order_no, from_status, to_status, changed_at, changed_by)
SELECT order_no, 'received', status, d + INTERVAL 1 HOUR, '테스트' FROM t_calc WHERE status <> 'received';

DROP TEMPORARY TABLE IF EXISTS t_calc;
DROP TEMPORARY TABLE IF EXISTS t_item;
DROP TEMPORARY TABLE IF EXISTS t_order;
SET SQL_SAFE_UPDATES = @OLD_SQL_SAFE_UPDATES;

-- 넣은 것 확인
SELECT order_no, received_at, order_status, total_amount FROM orders
WHERE order_no LIKE 'BR-________-TS__' ORDER BY received_at;

-- ============================================================================
-- [확인] 통계 탭 숫자와 비교하는 쿼리 (DB에 있는 모든 주문 기준 = 화면과 같은 기준)
--   주문 수·매출·판매 팩은 취소 제외, 취소율 = 취소 / 전체
-- ============================================================================
SELECT p.period AS 기간,
       SUM(o.order_status <> 'canceled')                                         AS 주문수_취소제외,
       SUM(IF(o.order_status <> 'canceled', o.total_amount, 0))                  AS 매출,
       SUM(IF(o.order_status <> 'canceled', (SELECT SUM(quantity) FROM order_items i WHERE i.order_no = o.order_no), 0)) AS 판매팩,
       SUM(o.order_status = 'canceled')                                          AS 취소건수,
       ROUND(100 * SUM(o.order_status = 'canceled') / COUNT(*), 1)               AS 취소율
FROM (SELECT '오늘' AS period, CURDATE() AS f
      UNION ALL SELECT '최근 7일', CURDATE() - INTERVAL 6 DAY
      UNION ALL SELECT '이번 달', DATE_FORMAT(CURDATE(), '%Y-%m-01')) p
JOIN orders o ON o.received_at >= p.f AND o.received_at < CURDATE() + INTERVAL 1 DAY
GROUP BY p.period
ORDER BY FIELD(p.period, '오늘', '최근 7일', '이번 달');

-- ============================================================================
-- [지우기] 테스트 주문만 지움 (다른 데이터는 그대로)
-- ============================================================================
-- SET SQL_SAFE_UPDATES = 0;
-- DELETE FROM order_status_log WHERE order_no LIKE 'BR-________-TS__';
-- DELETE FROM order_items      WHERE order_no LIKE 'BR-________-TS__';
-- DELETE FROM orders           WHERE order_no LIKE 'BR-________-TS__';
-- SET SQL_SAFE_UPDATES = 1;
