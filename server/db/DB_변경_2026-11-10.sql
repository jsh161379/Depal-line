-- DB 변경 2026-11-10 (DB 담당 제공). DB가 이미 있는 PC에서 한 번 실행
USE `aa_battery_inspection`;

-- 등급 5종 (이미 했으면 다시 해도 같음)
ALTER TABLE `battery_cycles`
  MODIFY `final_grade` enum('A','B','NG','RESORT','NG_VISUAL','NG_VOLTAGE') DEFAULT NULL
  COMMENT 'NG는 이전 데이터용. 새 데이터는 NG_VISUAL(외관 불량) / NG_VOLTAGE(전압·내부저항 불량)',
  ALGORITHM=INSTANT;

-- 불량을 찾은 카메라 (이미 있으면 Duplicate column 오류 → 건너뜀)
ALTER TABLE `visual_defects`
  ADD COLUMN `camera` varchar(16) DEFAULT NULL COMMENT '불량을 찾은 카메라 (top / front / back)' AFTER `confidence`,
  ALGORITHM=INSTANT;

-- 알림 토픽 기록용 EVENT
ALTER TABLE `communication_logs`
  MODIFY `message_kind` enum('COMMAND','RESPONSE','OPERATOR_COMMAND','EVENT') NOT NULL,
  ALGORITHM=INSTANT;
