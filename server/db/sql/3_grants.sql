-- 주문 관리 프로그램 조회용 읽기 전용 계정(ui_reader)에 주문 테이블 SELECT 권한 (2026-11-17)
-- 계정이 localhost / % 중 있는 쪽만 적용된다. 없는 쪽은 오류가 나지만 --force로 실행해서 넘어간다.
GRANT SELECT ON `aa_battery_inspection`.`orders`           TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`order_items`      TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`order_status_log` TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`order_runs`       TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`stock`            TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`stock_log`        TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`inquiries`        TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`orders`           TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`order_items`      TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`order_status_log` TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`order_runs`       TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`stock`            TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`stock_log`        TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`inquiries`        TO 'ui_reader'@'%';
-- 11-18: 검사 성적서 탭이 외관·극 방향 결과를 읽음 (DB 담당 수정본)
GRANT SELECT ON `aa_battery_inspection`.`visual_inspections` TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`visual_inspections` TO 'ui_reader'@'%';
-- 11-21: F6 검사 성적서가 판정 기준값(server/rules → grading_rules)을 읽음 / 관리자 프로그램 인수인계 탭 (DB 담당)
GRANT SELECT ON `aa_battery_inspection`.`grading_rules`  TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`grading_rules`  TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`handover_notes` TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`handover_notes` TO 'ui_reader'@'%';
GRANT SELECT ON `aa_battery_inspection`.`handover_reads` TO 'ui_reader'@'localhost';
GRANT SELECT ON `aa_battery_inspection`.`handover_reads` TO 'ui_reader'@'%';
