-- MySQL dump 10.13  Distrib 8.2.0, for Win64 (x86_64)
--
-- Host: 127.0.0.1    Database: aa_battery_inspection
-- ------------------------------------------------------
-- Server version	8.2.0

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Current Database: `aa_battery_inspection`
--

CREATE DATABASE /*!32312 IF NOT EXISTS*/ `aa_battery_inspection` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */ /*!80016 DEFAULT ENCRYPTION='N' */;

USE `aa_battery_inspection`;

--
-- Table structure for table `battery_cycles`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `battery_cycles` (
  `cycle_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `run_id` bigint unsigned NOT NULL,
  `source_battery_id` bigint unsigned DEFAULT NULL,
  `rule_id` bigint unsigned DEFAULT NULL,
  `pallet_slot` smallint unsigned DEFAULT NULL,
  `started_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `processing_status` varchar(32) NOT NULL DEFAULT 'STARTED',
  `electrical_status` enum('PENDING','SKIPPED','FAILED','COMPLETED') NOT NULL DEFAULT 'PENDING',
  `final_grade` enum('A','B','NG','RESORT','NG_VISUAL','NG_VOLTAGE') DEFAULT NULL COMMENT 'NG는 이전 데이터용. 새 데이터는 NG_VISUAL(외관 불량) / NG_VOLTAGE(전압·내부저항 불량)',
  `result_reason` text,
  `finished_at` datetime(3) DEFAULT NULL,
  `graded_at` datetime(3) DEFAULT NULL,
  `sorted_destination` varchar(64) DEFAULT NULL,
  `sorted_at` datetime(3) DEFAULT NULL,
  PRIMARY KEY (`cycle_id`),
  UNIQUE KEY `uq_battery_cycles_run_cycle` (`run_id`,`cycle_id`),
  UNIQUE KEY `uq_battery_cycles_source` (`run_id`,`source_battery_id`),
  KEY `idx_battery_cycles_run_started` (`run_id`,`started_at`),
  KEY `rule_id` (`rule_id`),
  CONSTRAINT `battery_cycles_ibfk_1` FOREIGN KEY (`run_id`) REFERENCES `runs` (`run_id`),
  CONSTRAINT `battery_cycles_ibfk_2` FOREIGN KEY (`rule_id`) REFERENCES `grading_rules` (`rule_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `communication_logs`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `communication_logs` (
  `log_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `topic` text NOT NULL,
  `message_kind` enum('COMMAND','RESPONSE','OPERATOR_COMMAND','EVENT') NOT NULL,
  `device` varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL,
  `cmd_id` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL,
  `source_run_id` varchar(20) DEFAULT NULL,
  `source_battery_id` bigint unsigned DEFAULT NULL,
  `action` varchar(64) DEFAULT NULL,
  `response_status` varchar(32) DEFAULT NULL,
  `error_text` varchar(512) DEFAULT NULL,
  `sent_at` datetime(3) DEFAULT NULL,
  `received_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `mqtt_qos` tinyint unsigned NOT NULL,
  `mqtt_dup` tinyint(1) NOT NULL,
  `mqtt_retained` tinyint(1) NOT NULL,
  `payload_text` longtext NOT NULL,
  PRIMARY KEY (`log_id`),
  KEY `idx_communication_command` (`device`,`cmd_id`,`received_at`),
  KEY `idx_communication_run` (`source_run_id`,`received_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `depaladmin_users`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `depaladmin_users` (
  `id` varchar(255) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
  `display_name` text NOT NULL,
  `role` tinyint unsigned NOT NULL,
  `password_hash` char(44) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `salt` char(24) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `must_change_password` tinyint(1) NOT NULL,
  `disabled` tinyint(1) NOT NULL,
  `created_at` datetime(6) NOT NULL,
  `last_login_at` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_depaladmin_users_active_admin` (`role`,`disabled`),
  CONSTRAINT `chk_depaladmin_users_role` CHECK ((`role` in (0,1,2)))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `electrical_tests`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `electrical_tests` (
  `electrical_test_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `cycle_id` bigint unsigned NOT NULL,
  `polarity` enum('normal','reversed','abnormal') DEFAULT NULL,
  `no_load_voltage_v` decimal(9,5) DEFAULT NULL,
  `loaded_voltage_v` decimal(9,5) DEFAULT NULL,
  `load_resistance_ohm` decimal(10,5) unsigned DEFAULT NULL,
  `estimated_internal_resistance_ohm` decimal(12,6) DEFAULT NULL,
  `load_duration_ms` int unsigned DEFAULT NULL,
  `measured_at` datetime(3) DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`electrical_test_id`),
  UNIQUE KEY `cycle_id` (`cycle_id`),
  CONSTRAINT `electrical_tests_ibfk_1` FOREIGN KEY (`cycle_id`) REFERENCES `battery_cycles` (`cycle_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `emergency_stop_events`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `emergency_stop_events` (
  `stop_event_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `source_event_id` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  `source_run_id` char(20) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL,
  `device_name` varchar(64) NOT NULL,
  `button_id` varchar(64) DEFAULT NULL,
  `trigger_source` enum('PHYSICAL_BUTTON','WPF','OTHER') DEFAULT NULL,
  `event_kind` enum('BUTTON_PRESSED','BUTTON_RELEASED','STOP_CONFIRMED','RESET_CONFIRMED') NOT NULL,
  `details` varchar(512) DEFAULT NULL,
  `occurred_at` datetime(3) NOT NULL,
  `received_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`stop_event_id`),
  UNIQUE KEY `source_event_id` (`source_event_id`),
  KEY `idx_emergency_stop_kind_time` (`event_kind`,`occurred_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `environment_readings`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `environment_readings` (
  `reading_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `source_reading_id` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  `source_run_id` char(20) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL,
  `device_name` varchar(64) NOT NULL,
  `sensor_name` varchar(64) NOT NULL,
  `metric` enum('GAS','TEMPERATURE','HUMIDITY') NOT NULL,
  `reading_value` decimal(14,6) NOT NULL,
  `unit` varchar(24) NOT NULL,
  `measured_at` datetime(3) NOT NULL,
  `received_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`reading_id`),
  UNIQUE KEY `uq_environment_reading` (`device_name`,`sensor_name`,`metric`,`source_reading_id`),
  KEY `idx_environment_metric_time` (`metric`,`measured_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `equipment_events`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `equipment_events` (
  `event_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `source_event_id` char(32) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL,
  `run_id` bigint unsigned DEFAULT NULL,
  `cycle_id` bigint unsigned DEFAULT NULL,
  `equipment_name` varchar(64) NOT NULL,
  `event_type` varchar(64) NOT NULL,
  `severity` enum('INFO','WARN','ERROR') DEFAULT NULL,
  `details` varchar(512) DEFAULT NULL,
  `occurred_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`event_id`),
  UNIQUE KEY `source_event_id` (`source_event_id`),
  KEY `run_id` (`run_id`,`cycle_id`),
  CONSTRAINT `equipment_events_ibfk_1` FOREIGN KEY (`run_id`) REFERENCES `runs` (`run_id`),
  CONSTRAINT `equipment_events_ibfk_2` FOREIGN KEY (`run_id`, `cycle_id`) REFERENCES `battery_cycles` (`run_id`, `cycle_id`),
  CONSTRAINT `equipment_events_chk_1` CHECK (((`cycle_id` is null) or (`run_id` is not null)))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `grading_rules`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `grading_rules` (
  `rule_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `rule_version` varchar(40) NOT NULL,
  `voltage_and_resistance_thresholds` json DEFAULT NULL,
  `effective_at` datetime(3) DEFAULT NULL,
  PRIMARY KEY (`rule_id`),
  UNIQUE KEY `rule_version` (`rule_version`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `inquiries`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `inquiries` (
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
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `order_items`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `order_items` (
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
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `order_runs`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `order_runs` (
  `order_no` varchar(20) NOT NULL,
  `source_run_id` char(20) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT '라인 회차 (runs.source_run_id와 같은 형식)',
  `battery_count` int unsigned NOT NULL COMMENT '이 회차에서 주문으로 나간 건전지 수',
  `linked_at` datetime(3) NOT NULL,
  PRIMARY KEY (`order_no`,`source_run_id`),
  KEY `idx_order_runs_run` (`source_run_id`),
  CONSTRAINT `order_runs_ibfk_1` FOREIGN KEY (`order_no`) REFERENCES `orders` (`order_no`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `order_status_log`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `order_status_log` (
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
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `orders`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `orders` (
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
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `runs`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `runs` (
  `run_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `source_run_id` char(20) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL,
  `run_type` enum('BENCH_TEST','LINE') NOT NULL,
  `started_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `ended_at` datetime(3) DEFAULT NULL,
  PRIMARY KEY (`run_id`),
  UNIQUE KEY `source_run_id` (`source_run_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `stock`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `stock` (
  `product_id` varchar(10) NOT NULL,
  `stock_packs` int NOT NULL DEFAULT '0' COMMENT '창고 팩 수. 음수면 입고 기록보다 많이 나간 것',
  `updated_at` datetime(3) NOT NULL,
  PRIMARY KEY (`product_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `stock_log`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `stock_log` (
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
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `visual_defects`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `visual_defects` (
  `defect_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `visual_inspection_id` bigint unsigned NOT NULL,
  `defect_type` varchar(64) NOT NULL,
  `confidence` decimal(7,6) DEFAULT NULL,
  `camera` varchar(16) DEFAULT NULL COMMENT '불량을 찾은 카메라 (top / front / back)',
  PRIMARY KEY (`defect_id`),
  KEY `visual_inspection_id` (`visual_inspection_id`),
  CONSTRAINT `visual_defects_ibfk_1` FOREIGN KEY (`visual_inspection_id`) REFERENCES `visual_inspections` (`visual_inspection_id`),
  CONSTRAINT `visual_defects_chk_1` CHECK ((`confidence` between 0 and 1))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `visual_inspections`
--

/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `visual_inspections` (
  `visual_inspection_id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `cycle_id` bigint unsigned NOT NULL,
  `visual_result` enum('PASS','FAIL','ERROR') NOT NULL,
  `polarity` enum('minus_up','plus_up','unknown') DEFAULT NULL,
  `confidence` decimal(7,6) DEFAULT NULL,
  `image_path` varchar(1024) DEFAULT NULL,
  `model_version` varchar(80) DEFAULT NULL,
  `inspected_at` datetime(3) DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`visual_inspection_id`),
  UNIQUE KEY `cycle_id` (`cycle_id`),
  CONSTRAINT `visual_inspections_ibfk_1` FOREIGN KEY (`cycle_id`) REFERENCES `battery_cycles` (`cycle_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-11-09  9:21:21
-- 2026-11-10: 등급 NG_VISUAL/NG_VOLTAGE, visual_defects.camera, message_kind EVENT 추가
-- 2026-11-17: 주문 테이블 7개 추가 (orders, order_items, order_status_log, order_runs, stock, stock_log, inquiries), 주문 통신 규약 v1.0 반영
