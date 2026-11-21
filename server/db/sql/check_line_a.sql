-- 라인 A 등급 확인 (11/20 결정 ⑦ 조건: A 등급 + 실제 배출 + LINE 회차). check_line_a.bat이 실행
-- ① 최근 회차 5개: 회차 종류와 A 개수
SELECT ru.source_run_id AS run_id, ru.run_type,
       COUNT(b.cycle_id) AS batteries,
       SUM(b.final_grade = 'A' AND b.sorted_destination IS NOT NULL) AS a_count
FROM runs ru
LEFT JOIN battery_cycles b ON b.run_id = ru.run_id
GROUP BY ru.run_id, ru.source_run_id, ru.run_type, ru.started_at
ORDER BY ru.started_at DESC
LIMIT 5;

-- ② 주문 관리 아래 줄 "라인 A 남음"과 같아야 하는 값 (최근 30일 LINE 회차)
SELECT COALESCE(SUM(GREATEST(x.a_count - COALESCE(l.linked, 0), 0)), 0) AS line_a_available
FROM (
  SELECT ru.source_run_id, COUNT(*) AS a_count
  FROM runs ru
  JOIN battery_cycles b ON b.run_id = ru.run_id
   AND b.final_grade = 'A' AND b.sorted_destination IS NOT NULL
  WHERE ru.run_type = 'LINE' AND ru.started_at >= NOW() - INTERVAL 30 DAY
  GROUP BY ru.source_run_id
) x
LEFT JOIN (SELECT source_run_id, SUM(battery_count) AS linked
           FROM order_runs GROUP BY source_run_id) l
  ON l.source_run_id = x.source_run_id;

-- ③ 판정 기준 (server/rules → grading_rules)
SELECT rule_version, voltage_and_resistance_thresholds, effective_at FROM grading_rules ORDER BY rule_id;
