-- 주문 테이블 적용 전 정리 (2026-11-17, 조시현)
-- 오늘 오전 DB 담당 첫 버전 스크립트를 이미 실행한 DB만 손본다. 처음 적용하는 DB에서는 아무것도 바꾸지 않는다.
--  ① orders에 accepted_at이 없으면 추가
--  ② inquiries가 첫 버전 모양(inquiry_type 없음)이고 비어 있으면 지움 → 다음 스크립트(2_order_tables.sql)가 새 모양으로 다시 만듦
--     (데이터가 있으면 지우지 않고 멈춤 표시만 → 직접 확인)
USE `aa_battery_inspection`;

SET @db := 'aa_battery_inspection';

-- ① orders.accepted_at
SET @has_orders := (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'orders');
SET @has_acc := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'orders' AND COLUMN_NAME = 'accepted_at');
SET @sql := IF(@has_orders = 1 AND @has_acc = 0,
  'ALTER TABLE `orders` ADD COLUMN `accepted_at` datetime(3) DEFAULT NULL COMMENT ''주문 관리 프로그램이 검사를 통과시킨 시각'' AFTER `received_at`',
  'SELECT ''prep 1: orders.accepted_at 손볼 것 없음'' AS info');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

-- ② inquiries 첫 버전 모양
SET @has_inq := (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'inquiries');
SET @has_type := (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'inquiries' AND COLUMN_NAME = 'inquiry_type');
SET @old_inq := (@has_inq = 1 AND @has_type = 0);
SET @n := 0;
SET @sql := IF(@old_inq, 'SELECT COUNT(*) INTO @n FROM `inquiries`', 'SELECT 0 INTO @n');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;
SET @sql := IF(@old_inq AND @n = 0, 'DROP TABLE `inquiries`',
            IF(@old_inq, 'SELECT ''prep 2: 첫 버전 inquiries에 데이터가 있어 지우지 않음 — 직접 확인 필요'' AS warning',
                         'SELECT ''prep 2: inquiries 손볼 것 없음'' AS info'));
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;
