# DB 저장 프로그램 v1.2 전달

v1.2 메시지를 기준으로 MySQL과 DB 저장 프로그램을 수정했습니다. 토픽과 JSON 필드는 공유해 주신 명세대로 사용하면 됩니다.

| 항목 | 반영 내용 |
|---|---|
| `rule_version` | `grading_rules.rule_version`에 등록하고 `battery_cycles.rule_id`로 연결합니다. 서버에서 버전만 받은 경우 기준값과 적용 시각은 `NULL`로 둡니다. 이미 등록된 기준값은 유지합니다. |
| `r_load` | 정상 극성의 실측 부하 저항을 `electrical_tests.load_resistance_ohm`에 Ω 단위로 저장합니다. 역극성이나 부하 측정을 생략한 경우는 `NULL`입니다. |
| 중복 수신 | 이벤트는 `event_id`, 검사 결과는 `(run_id, battery_id)`로 중복을 방지합니다. |

## 전달 파일

- `teamproject.sql`은 새 MySQL 인스턴스에 가져올 DB 내보내기 파일입니다.
- `mqtt_db_writer.py`는 MQTT 구독과 MySQL 저장을 담당합니다.
- `sample_results.py`는 v1.2 샘플 7건을 발행합니다. 첨부받은 원본 샘플에 `rule_version`과 `r_load`를 반영했습니다.
- 기존 v1.1 DB를 사용 중이면 해당 DB를 선택하고 `mysql_message_v1_2_migration.sql`을 실행하면 됩니다.

## 서버 PC에서 시험하는 순서

MySQL과 Mosquitto를 실행한 뒤, PowerShell에서 패키지를 설치하고 저장 프로그램을 켭니다. MySQL 비밀번호는 실행할 때 입력합니다. 전용 계정으로 실행할 경우 `--db-user 계정명`을 붙입니다.

```powershell
python -m pip install "paho-mqtt>=2.1,<3" "mysql-connector-python>=9.4,<10" "pydantic>=2,<3" "aiomqtt>=2,<3"
python mqtt_db_writer.py
```

저장 프로그램에 `Subscribed`가 뜨면 다른 PowerShell 창에서 샘플을 발행합니다.

```powershell
python sample_results.py --twice
```

로컬 Mosquitto와 MySQL 시험에서 한 회차의 샘플 7건을 두 번 발행했고, 아래 저장 건수를 확인했습니다. 정상 극성의 부하 저항 `3.31 Ω`과 기준 버전 `temp-2026-11-06`의 연결도 확인했습니다.

| 테이블 | 해당 회차 저장 건수 |
|---|---:|
| `battery_cycles` | 7 |
| `visual_inspections` | 7 |
| `visual_defects` | 1 |
| `electrical_tests` | 4 |

서버 PC에서 통합 시험 가능한 시간을 알려주시면, 실제 라인 컨트롤러가 발행한 이벤트와 검사 결과가 저장되는 것까지 함께 확인하겠습니다.
