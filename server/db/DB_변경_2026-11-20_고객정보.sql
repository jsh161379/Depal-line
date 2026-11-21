-- 고객 유형·연령대·지역 칸 추가 (2026-11-20, 홈페이지 고객 정보 제안)
-- DB가 이미 있는 PC(서버 PC)에서 한 번 실행합니다. 이미 있는 칸은 건너뛰므로 다시 실행해도 됩니다.
-- 기존 주문은 고객 유형 personal, 연령대 none으로 채우고, 지역은 배송 주소 앞부분(서울·경기도·충청북도 …)으로 채웁니다 (모르면 none).

USE `aa_battery_inspection`;
SET @OLD_SQL_SAFE_UPDATES = @@SQL_SAFE_UPDATES;
SET SQL_SAFE_UPDATES = 0;

-- 1) 칸 추가 (배송 주소 뒤)
SET @s = IF((SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' AND COLUMN_NAME = 'customer_type') = 0,
  'ALTER TABLE `orders` ADD COLUMN `customer_type` enum(''personal'',''company'') NOT NULL DEFAULT ''personal'' COMMENT ''고객 유형: 개인 / 기업'' AFTER `shipping_address`', 'SELECT ''customer_type 이미 있음''');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @s = IF((SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' AND COLUMN_NAME = 'age_group') = 0,
  'ALTER TABLE `orders` ADD COLUMN `age_group` enum(''10s'',''20s'',''30s'',''40s'',''50s+'',''none'') NOT NULL DEFAULT ''none'' COMMENT ''연령대. 선택 안 함·기업은 none'' AFTER `customer_type`', 'SELECT ''age_group 이미 있음''');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @s = IF((SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' AND COLUMN_NAME = 'region') = 0,
  'ALTER TABLE `orders` ADD COLUMN `region` varchar(10) NOT NULL DEFAULT ''none'' COMMENT ''시·도 17개 (서울·부산·…·제주). 모르면 none'' AFTER `age_group`', 'SELECT ''region 이미 있음''');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

-- 11-20 첫 버전(지역 NULL 허용)을 이미 실행한 DB: NULL을 none으로 바꾸고 NOT NULL DEFAULT 'none'으로 맞춤
UPDATE `orders` SET `region` = 'none' WHERE `region` IS NULL;
SET @s = IF((SELECT IS_NULLABLE FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' AND COLUMN_NAME = 'region') = 'YES',
  'ALTER TABLE `orders` MODIFY `region` varchar(10) NOT NULL DEFAULT ''none'' COMMENT ''시·도 17개 (서울·부산·…·제주). 모르면 none''', 'SELECT ''region 이미 NOT NULL''');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @s = IF((SELECT COUNT(*) FROM information_schema.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' AND INDEX_NAME = 'idx_orders_region') = 0,
  'ALTER TABLE `orders` ADD KEY `idx_orders_region` (`region`,`received_at`)', 'SELECT ''idx_orders_region 이미 있음''');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;
SET @s = IF((SELECT COUNT(*) FROM information_schema.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' AND INDEX_NAME = 'idx_orders_age') = 0,
  'ALTER TABLE `orders` ADD KEY `idx_orders_age` (`age_group`,`received_at`)', 'SELECT ''idx_orders_age 이미 있음''');
PREPARE st FROM @s; EXECUTE st; DEALLOCATE PREPARE st;

-- 2) 지역이 none인 주문을 배송 주소로 채움 (이미 지역이 있는 주문은 그대로)
UPDATE `orders` SET `region` = CASE
    WHEN shipping_address LIKE '서울%' THEN '서울'
    WHEN shipping_address LIKE '부산%' THEN '부산'
    WHEN shipping_address LIKE '대구%' THEN '대구'
    WHEN shipping_address LIKE '인천%' THEN '인천'
    WHEN shipping_address LIKE '광주%' THEN '광주'
    WHEN shipping_address LIKE '대전%' THEN '대전'
    WHEN shipping_address LIKE '울산%' THEN '울산'
    WHEN shipping_address LIKE '세종%' THEN '세종'
    WHEN shipping_address LIKE '경기%' THEN '경기'
    WHEN shipping_address LIKE '강원%' THEN '강원'
    WHEN shipping_address LIKE '충북%' OR shipping_address LIKE '충청북도%' THEN '충북'
    WHEN shipping_address LIKE '충남%' OR shipping_address LIKE '충청남도%' THEN '충남'
    WHEN shipping_address LIKE '전북%' OR shipping_address LIKE '전라북도%' THEN '전북'
    WHEN shipping_address LIKE '전남%' OR shipping_address LIKE '전라남도%' THEN '전남'
    WHEN shipping_address LIKE '경북%' OR shipping_address LIKE '경상북도%' THEN '경북'
    WHEN shipping_address LIKE '경남%' OR shipping_address LIKE '경상남도%' THEN '경남'
    WHEN shipping_address LIKE '제주%' THEN '제주'
    ELSE 'none' END
WHERE `region` = 'none';

SET SQL_SAFE_UPDATES = @OLD_SQL_SAFE_UPDATES;

-- ===== 테스트용 가짜 데이터 시작 (실제 운영 DB에서는 이 아래를 실행하지 않음) =====
-- 고객 정보 테스트용 가짜 데이터 (2026-11-20). 가짜 주문에 고객 유형·연령대·지역을 넣습니다. 여러 번 실행해도 됩니다.
USE `aa_battery_inspection`;
SET @OLD_SQL_SAFE_UPDATES = @@SQL_SAFE_UPDATES;
SET SQL_SAFE_UPDATES = 0;

UPDATE `orders` SET customer_type = 'company', age_group = 'none', region = '서울'
  WHERE order_no IN ('BR-20261113-4Y88', 'BR-20261116-UQZS');
UPDATE `orders` SET customer_type = 'personal', age_group = '10s', region = '경기'
  WHERE order_no IN ('BR-20261111-Y6AJ');
UPDATE `orders` SET customer_type = 'personal', age_group = '20s', region = '광주'
  WHERE order_no IN ('BR-20261112-5WWH', 'BR-20261112-XLES', 'BR-20261118-V25C');
UPDATE `orders` SET customer_type = 'personal', age_group = '20s', region = '대구'
  WHERE order_no IN ('BR-20261111-PNYQ', 'BR-20261113-V73K', 'BR-20261118-RBF6');
UPDATE `orders` SET customer_type = 'personal', age_group = '30s', region = '대전'
  WHERE order_no IN ('BR-20261110-CGWJ', 'BR-20261110-UV9G', 'BR-20261114-DSAF', 'BR-20261114-G8HX', 'BR-20261116-F8JZ', 'BR-20261116-3CBP', 'BR-20261116-8QBE', 'BR-20261117-HJDH', 'BR-20261117-LSMS');
UPDATE `orders` SET customer_type = 'personal', age_group = '30s', region = '서울'
  WHERE order_no IN ('BR-20261118-HW7T', 'BR-20261118-U2AF');
UPDATE `orders` SET customer_type = 'personal', age_group = '40s', region = '부산'
  WHERE order_no IN ('BR-20261115-TP4B');
UPDATE `orders` SET customer_type = 'personal', age_group = '50s+', region = '인천'
  WHERE order_no IN ('BR-20261114-AJHH');
UPDATE `orders` SET customer_type = 'personal', age_group = 'none', region = '서울'
  WHERE order_no IN ('BR-20261112-EKUC', 'BR-20261113-P7V3', 'BR-20261115-LF49', 'BR-20261116-PAHK');

SET SQL_SAFE_UPDATES = @OLD_SQL_SAFE_UPDATES;
-- ===== 테스트용 가짜 데이터 끝 =====
