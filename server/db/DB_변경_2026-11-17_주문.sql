-- 주문 관리 테이블 추가 (2026-11-17, 주문 통신 규약 v1.0 기준)
-- DB가 이미 있는 PC에서 한 번 실행합니다. 이미 있는 테이블은 건너뛰므로 다시 실행해도 됩니다.
-- 기존 라인 테이블과 데이터는 바꾸지 않습니다.

USE `aa_battery_inspection`;

CREATE TABLE IF NOT EXISTS `orders` (
  `order_no` varchar(20) NOT NULL COMMENT 'BR-YYYYMMDD-XXXX',
  `received_at` datetime(3) NOT NULL COMMENT '홈페이지에서 주문한 시각 (메시지의 ts)',
  `accepted_at` datetime(3) DEFAULT NULL COMMENT '주문 관리 프로그램이 검사를 통과시킨 시각',
  `customer_name` varchar(50) NOT NULL,
  `customer_phone` varchar(20) NOT NULL,
  `shipping_address` varchar(255) NOT NULL,
  `memo` varchar(255) DEFAULT NULL,
  `total_amount` int unsigned NOT NULL COMMENT '주문 금액(원)',
  `order_status` enum('received','inspect','shipping','delivered','canceled') NOT NULL DEFAULT 'received',
  `courier` varchar(30) DEFAULT NULL,
  `tracking_no` varchar(30) DEFAULT NULL,
  `updated_at` datetime(3) NOT NULL COMMENT '마지막 상태 변경 시각',
  PRIMARY KEY (`order_no`),
  KEY `idx_orders_received` (`received_at`),
  KEY `idx_orders_status` (`order_status`,`received_at`),
  KEY `idx_orders_phone` (`customer_phone`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `order_items` (
  `order_item_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `order_no` varchar(20) NOT NULL,
  `product_id` varchar(10) NOT NULL COMMENT 'aa4 / aa12 / aa24 / aar2',
  `product_name` varchar(50) NOT NULL,
  `quantity` int unsigned NOT NULL COMMENT '팩 수',
  `unit_price` int unsigned NOT NULL COMMENT '팩당 가격(원)',
  PRIMARY KEY (`order_item_id`),
  KEY `idx_order_items_order` (`order_no`),
  KEY `idx_order_items_product` (`product_id`),
  CONSTRAINT `order_items_ibfk_1` FOREIGN KEY (`order_no`) REFERENCES `orders` (`order_no`),
  CONSTRAINT `order_items_chk_1` CHECK ((`quantity` > 0))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `order_status_log` (
  `status_log_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `order_no` varchar(20) NOT NULL,
  `from_status` enum('received','inspect','shipping','delivered','canceled') DEFAULT NULL COMMENT '새 주문이면 NULL',
  `to_status` enum('received','inspect','shipping','delivered','canceled') NOT NULL,
  `changed_at` datetime(3) NOT NULL,
  `changed_by` varchar(50) DEFAULT NULL,
  PRIMARY KEY (`status_log_id`),
  UNIQUE KEY `uq_order_status_log` (`order_no`,`to_status`,`changed_at`),
  KEY `idx_order_status_log_time` (`to_status`,`changed_at`),
  CONSTRAINT `order_status_log_ibfk_1` FOREIGN KEY (`order_no`) REFERENCES `orders` (`order_no`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `order_runs` (
  `order_no` varchar(20) NOT NULL,
  `source_run_id` char(20) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT '라인 회차 (runs.source_run_id와 같은 형식)',
  `battery_count` int unsigned NOT NULL COMMENT '이 회차에서 주문으로 나간 건전지 수',
  `linked_at` datetime(3) NOT NULL,
  PRIMARY KEY (`order_no`,`source_run_id`),
  KEY `idx_order_runs_run` (`source_run_id`),
  CONSTRAINT `order_runs_ibfk_1` FOREIGN KEY (`order_no`) REFERENCES `orders` (`order_no`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `stock` (
  `product_id` varchar(10) NOT NULL,
  `stock_packs` int NOT NULL DEFAULT '0' COMMENT '창고 팩 수. 음수면 입고 기록보다 많이 나간 것',
  `updated_at` datetime(3) NOT NULL,
  PRIMARY KEY (`product_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `stock_log` (
  `stock_log_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `source_event_id` char(32) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL COMMENT 'order/stock 메시지의 event_id. 배송 완료 차감은 NULL',
  `product_id` varchar(10) NOT NULL,
  `delta_packs` int NOT NULL COMMENT '입고 +, 출고 -',
  `packs_after` int NOT NULL COMMENT '변경 뒤 창고 팩 수',
  `reason` enum('restock','delivered','adjust') NOT NULL COMMENT '입고 / 배송 완료 / 수정',
  `order_no` varchar(20) DEFAULT NULL,
  `changed_by` varchar(50) DEFAULT NULL,
  `logged_at` datetime(3) NOT NULL,
  PRIMARY KEY (`stock_log_id`),
  UNIQUE KEY `source_event_id` (`source_event_id`),
  KEY `idx_stock_log_product_time` (`product_id`,`logged_at`),
  KEY `idx_stock_log_order` (`order_no`),
  CONSTRAINT `stock_log_ibfk_1` FOREIGN KEY (`order_no`) REFERENCES `orders` (`order_no`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `inquiries` (
  `inquiry_id` varchar(20) NOT NULL COMMENT '문의번호 Q-YYYYMMDD-XXXX (메시지의 no)',
  `asked_at` datetime(3) NOT NULL,
  `customer_name` varchar(50) NOT NULL,
  `customer_phone` varchar(20) NOT NULL,
  `inquiry_type` enum('delivery','cancel','product','bulk','etc') NOT NULL COMMENT '주문·배송 / 취소·교환 / 제품 / 대량 구매 / 기타',
  `order_no` varchar(20) DEFAULT NULL COMMENT '고객이 적은 주문번호. 틀릴 수 있어 외래 키를 걸지 않음',
  `question` text NOT NULL,
  `answer` text,
  `answered_at` datetime(3) DEFAULT NULL,
  `answered_by` varchar(50) DEFAULT NULL,
  PRIMARY KEY (`inquiry_id`),
  KEY `idx_inquiries_asked` (`asked_at`),
  KEY `idx_inquiries_type` (`inquiry_type`,`asked_at`),
  KEY `idx_inquiries_order` (`order_no`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 11-17 첫 버전 스크립트를 이미 실행한 DB만: 아래 두 줄의 주석을 풀고 실행 (주문·문의 데이터가 없을 때).
-- 문의 표가 주문 통신 규약 v1.0 형식(문의번호 Q-…, 문의 유형)으로 바뀌었고, orders에 accepted_at이 추가됐습니다.
-- DROP TABLE IF EXISTS `inquiries`;   -- 지운 뒤 이 스크립트를 처음부터 다시 실행하면 새 형식으로 만들어짐
-- ALTER TABLE `orders` ADD COLUMN `accepted_at` datetime(3) DEFAULT NULL COMMENT '주문 관리 프로그램이 검사를 통과시킨 시각' AFTER `received_at`;

-- (선택) 주문 관리 프로그램이 조회할 계정에 읽기 권한 주기.
-- 계정 이름과 접속 위치('localhost')가 다르면 맞게 바꿔서 실행하세요.
-- GRANT SELECT ON `aa_battery_inspection`.`orders`           TO 'ui_reader'@'localhost';
-- GRANT SELECT ON `aa_battery_inspection`.`order_items`      TO 'ui_reader'@'localhost';
-- GRANT SELECT ON `aa_battery_inspection`.`order_status_log` TO 'ui_reader'@'localhost';
-- GRANT SELECT ON `aa_battery_inspection`.`order_runs`       TO 'ui_reader'@'localhost';
-- GRANT SELECT ON `aa_battery_inspection`.`stock`            TO 'ui_reader'@'localhost';
-- GRANT SELECT ON `aa_battery_inspection`.`stock_log`        TO 'ui_reader'@'localhost';
-- GRANT SELECT ON `aa_battery_inspection`.`inquiries`        TO 'ui_reader'@'localhost';
-- 검사 성적서 탭은 라인 테이블도 읽습니다 (runs, battery_cycles, electrical_tests는 이미 권한 있음).
