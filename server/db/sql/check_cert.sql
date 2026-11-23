-- 성적서 발급 시험 확인: 가장 최근에 회차를 연결한 주문 1건 (읽기만 함). check_cert.bat이 실행, 결과는 logs\check_cert.txt
-- ① 주문·성적서 번호·상태
SELECT o.order_no, o.order_status, o.cert_no
FROM orders o
WHERE o.order_no = (SELECT order_no FROM order_runs ORDER BY linked_at DESC LIMIT 1);

-- ② 연결된 회차별: 연결 개수, 그 회차 A 개수(배출 완료), A에 적용된 판정 기준 버전, 기준값이 DB에 있는지
SELECT r.source_run_id AS run_id, r.battery_count AS linked,
       COUNT(b.cycle_id) AS a_count,
       GROUP_CONCAT(DISTINCT g.rule_version) AS rule_version,
       MAX(g.voltage_and_resistance_thresholds IS NOT NULL) AS has_thresholds
FROM order_runs r
JOIN runs ru ON ru.source_run_id = r.source_run_id AND ru.run_type = 'LINE'
LEFT JOIN battery_cycles b ON b.run_id = ru.run_id AND b.final_grade = 'A' AND b.sorted_destination IS NOT NULL
LEFT JOIN grading_rules g ON g.rule_id = b.rule_id
WHERE r.order_no = (SELECT order_no FROM order_runs ORDER BY linked_at DESC LIMIT 1)
GROUP BY r.source_run_id, r.battery_count
ORDER BY r.source_run_id;

-- ③ 판정 기준표 전체 (기준값이 비어 있는 버전 확인)
SELECT rule_id, rule_version, voltage_and_resistance_thresholds IS NOT NULL AS has_thresholds, effective_at
FROM grading_rules ORDER BY rule_id;
