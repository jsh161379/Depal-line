-- ui_reader 계정이 grading_rules를 읽을 수 있는지 확인 (통신 규약 v1.5.2, 주문 통신 규약 6-4). 읽기만 함
-- check_grants.bat이 root로 실행, 결과는 logs\check_grants.txt
-- ① 결론: 계정(호스트)별로 grading_rules를 읽을 수 있는지 (1 = 읽을 수 있음, 0 = 권한 없음)
SELECT u.Host AS host,
       (u.Select_priv = 'Y'
        OR EXISTS (SELECT 1 FROM mysql.db d
                   WHERE d.User = u.User AND d.Host = u.Host
                     AND d.Db = 'aa_battery_inspection' AND d.Select_priv = 'Y')
        OR EXISTS (SELECT 1 FROM mysql.tables_priv t
                   WHERE t.User = u.User AND t.Host = u.Host AND t.Db = 'aa_battery_inspection'
                     AND t.Table_name = 'grading_rules' AND FIND_IN_SET('Select', t.Table_priv) > 0)
       ) AS can_read_grading_rules
FROM mysql.user u
WHERE u.User = 'ui_reader'
ORDER BY u.Host;

-- ② 참고: 표별 권한 목록 (grading_rules 줄이 있는지)
SELECT Host AS host, Table_name AS table_name, Table_priv AS priv
FROM mysql.tables_priv
WHERE User = 'ui_reader' AND Db = 'aa_battery_inspection'
ORDER BY Host, Table_name;

-- ③ 참고: DB 전체 읽기 권한이 있는지
SELECT Host AS host, Db AS db, Select_priv AS can_select_all FROM mysql.db WHERE User = 'ui_reader';

-- ④ grading_rules에 기준값이 들어 있는지 (root로 조회)
SELECT rule_id, rule_version, effective_at FROM aa_battery_inspection.grading_rules ORDER BY rule_id;
