-- DB 변경 2026-11-21: 서버 PC DB에 인수인계 표 만들기 + ui_reader 읽기 권한 (장성찬)
-- 대상: 이미 운영 중인 aa_battery_inspection (서버 PC). 새로 만드는 PC는 teamproject.sql에 이미 들어 있음
-- 여러 번 실행해도 됨: 표는 없을 때만 만들고, GRANT는 다시 해도 그대로
--
-- 왜:
--   1) 저장 프로그램(mqtt_db_writer.py)이 "Handover tables missing (handover_notes, handover_reads)" 경고
--      → 표가 없으면 handover/note·handover/read를 구독하지 않음 (라인·주문 저장은 그대로)
--   2) 11/20 결정 ⑩: 판정 기준값(server/rules)을 grading_rules에 저장 → 주문 관리 F6(검사 성적서)이 읽음
--      → ui_reader에 grading_rules SELECT가 없었음
--   3) 관리자 프로그램 인수인계 탭이 handover_notes·handover_reads를 읽음
--
-- 실행: MySQL Workbench에서 root로 이 파일 전체 실행
--   ui_reader 계정이 'localhost'와 '%' 중 한쪽만 있으면 없는 쪽 GRANT는 오류가 남 → 그 줄만 건너뛰면 됨
--   (명령줄: mysql -u root -p --force < 이 파일)
-- 끝나면 저장 프로그램을 다시 실행 → 시작 로그에 Handover 경고가 없어야 함

USE `aa_battery_inspection`;

-- 1) 인수인계 표 (teamproject.sql과 같은 구조)
CREATE TABLE IF NOT EXISTS `handover_notes` (
  `note_id` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'HandoverNote.Id (GUID 32자리 16진수)',
  `written_at` datetime(3) NOT NULL COMMENT '작성 시각 (메시지의 ts)',
  `author_id` varchar(255) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL COMMENT 'depaladmin_users.id와 같은 규칙',
  `author_name` varchar(100) NOT NULL COMMENT '작성자 표시 이름',
  `note_text` mediumtext NOT NULL COMMENT 'BuildFormattedNoteText() 본문',
  `line_mode` varchar(20) DEFAULT NULL COMMENT '작성 시점 라인 모드 (idle / running / paused / draining / estop / manual)',
  `source_run_id` char(20) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL COMMENT '작성 시점 회차 (runs.source_run_id 형식)',
  `unacked_alarms` int unsigned NOT NULL DEFAULT '0' COMMENT '작성 시점 미확인 알람 수',
  `received_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) COMMENT 'DB 기록 시각',
  PRIMARY KEY (`note_id`),
  KEY `idx_handover_notes_written_at` (`written_at`),
  KEY `idx_handover_notes_run` (`source_run_id`),
  KEY `idx_handover_notes_author` (`author_id`,`written_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `handover_reads` (
  `note_id` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` varchar(255) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL COMMENT '읽음을 누른 계정 (depaladmin_users.id 규칙)',
  `read_at` datetime(3) NOT NULL COMMENT '읽음 시각 (메시지의 ts)',
  PRIMARY KEY (`note_id`,`user_id`),
  KEY `idx_handover_reads_user` (`user_id`,`read_at`),
  CONSTRAINT `handover_reads_ibfk_1` FOREIGN KEY (`note_id`) REFERENCES `handover_notes` (`note_id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 2) ui_reader 읽기 권한
GRANT SELECT ON `aa_battery_inspection`.`grading_rules`  TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`handover_notes` TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`handover_reads` TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`grading_rules`  TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`handover_notes` TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`handover_reads` TO 'ui_reader'@'%';

-- 3) 확인
SHOW TABLES LIKE 'handover%';
SHOW GRANTS FOR 'ui_reader'@'localhost';
-- SHOW GRANTS FOR 'ui_reader'@'%';
