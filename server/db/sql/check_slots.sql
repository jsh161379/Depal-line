-- 팔레트 2×3 칸 번호 확인 (통신 규약 v1.5.3). check_slots.bat이 실행, 결과는 logs\check_slots.txt
-- ① 최근 회차 2개의 건전지별 칸 번호 (1~6이어야 함)
SELECT ru.source_run_id AS run_id, b.source_battery_id AS no, b.pallet_slot AS slot,
       b.final_grade AS grade, b.sorted_destination AS dest
FROM battery_cycles b
JOIN runs ru ON ru.run_id = b.run_id
WHERE ru.run_id IN (SELECT run_id FROM (SELECT run_id FROM runs ORDER BY started_at DESC LIMIT 2) t)
ORDER BY ru.started_at, b.source_battery_id;

-- ② 같은 회차 요약: 개수, 칸 번호 최소·최대, 겹친 칸 수 (개수 6, 최소 1, 최대 6, 겹침 0이면 정상)
SELECT ru.source_run_id AS run_id, COUNT(*) AS batteries,
       MIN(b.pallet_slot) AS min_slot, MAX(b.pallet_slot) AS max_slot,
       COUNT(*) - COUNT(DISTINCT b.pallet_slot) AS duplicated
FROM battery_cycles b
JOIN runs ru ON ru.run_id = b.run_id
WHERE ru.run_id IN (SELECT run_id FROM (SELECT run_id FROM runs ORDER BY started_at DESC LIMIT 2) t)
GROUP BY ru.run_id, ru.source_run_id, ru.started_at
ORDER BY ru.started_at;
