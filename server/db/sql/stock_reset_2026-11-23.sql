-- ============================================================
-- 2026-11-23  ⑪-③ 배송 완료 재고 차감 끄기 뒤 정리
--   stock 표에서 음수인 상품만 0으로 맞춘다.
--   맞춘 기록은 stock_log에 reason='adjust'로 한 줄씩 남긴다 (나중에 왜 0이 됐는지 보이게).
--   0 이상인 상품, 다른 표는 건드리지 않는다. 여러 번 실행해도 두 번째부터는 바뀌는 게 없다.
--
-- 순서: 반드시 새 mqtt_db_writer.py(DEDUCT_STOCK_ON_DELIVERY = False)로 바꿔 띄운 뒤 실행.
--       (옛 프로그램이 돌고 있으면 배송 완료가 들어올 때 다시 음수가 된다)
-- ============================================================
USE aa_battery_inspection;

-- 1) 실행 전 확인: 음수인 줄
SELECT product_id, stock_packs, updated_at FROM stock WHERE stock_packs < 0;

START TRANSACTION;

-- 2) 기록 먼저: 음수 → 0 (delta = 빠진 만큼 되돌림)
INSERT INTO stock_log (source_event_id, product_id, delta_packs, packs_after, reason, order_no, changed_by, logged_at)
SELECT NULL, product_id, -stock_packs, 0, 'adjust', NULL, 'db:11-3 reset negative', NOW(3)
FROM stock
WHERE stock_packs < 0;

-- 3) 0으로 맞추기 (Workbench 안전 모드 때문에 잠깐 끄고, 바로 다시 켬)
SET @old_safe = @@SQL_SAFE_UPDATES;
SET SQL_SAFE_UPDATES = 0;
UPDATE stock SET stock_packs = 0, updated_at = NOW(3) WHERE stock_packs < 0;
SET SQL_SAFE_UPDATES = @old_safe;

COMMIT;

-- 4) 실행 후 확인: 결과가 0줄이어야 함
SELECT product_id, stock_packs FROM stock WHERE stock_packs < 0;
SELECT * FROM stock_log WHERE reason = 'adjust' ORDER BY stock_log_id DESC LIMIT 10;
