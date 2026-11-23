-- 11/20 이전 판정 기준(temp-2026-11-10)의 기준값 채우기 (2026-11-23, 안 1)
-- 이유: 기준값 발행(server/rules)은 11/21부터라, 그 전 버전은 grading_rules에 이름만 있고 기준값이 비어 있음
--       → 옛 회차를 연결한 성적서가 버전은 temp-2026-11-10, 기준값은 코드 안 임시값(11/20 값)으로 서로 맞지 않음
-- 값: 통신 규약 v1.1~v1.5.1 4장 (A 무부하 >= 1.50 V·내부저항 <= 0.9 Ω, B 1.30 V·1.5 Ω, 전압·내부저항 동시 판정)
-- 모양: 컨트롤러 server/rules의 thresholds와 같게 (order만 'both' = 전압·내부저항 동시, 11/20부터는 v_open_first)
-- 안전: 기준값이 비어 있는 줄만 고침 (이미 값이 있으면 아무것도 바꾸지 않음). temp-2026-11-06은 값 기록이 확실하지 않아 그대로 둠

SELECT '고치기 전' AS step, rule_id, rule_version, effective_at, voltage_and_resistance_thresholds AS thresholds
FROM grading_rules WHERE rule_version = 'temp-2026-11-10';

UPDATE grading_rules
SET voltage_and_resistance_thresholds =
      JSON_OBJECT('A', JSON_OBJECT('v_open_min', 1.50, 'r_int_max', 0.9),
                  'B', JSON_OBJECT('v_open_min', 1.30, 'r_int_max', 1.5),
                  'order', 'both'),
    effective_at = COALESCE(effective_at, '2026-11-10 00:00:00.000')
WHERE rule_version = 'temp-2026-11-10'
  AND voltage_and_resistance_thresholds IS NULL;

SELECT ROW_COUNT() AS changed_rows;

SELECT '고친 뒤' AS step, rule_id, rule_version, effective_at, voltage_and_resistance_thresholds AS thresholds
FROM grading_rules ORDER BY rule_id;
