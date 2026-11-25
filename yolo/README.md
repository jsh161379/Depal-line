# yolo — YOLO 검사 프로그램

카메라 3대(위 `top` · 앞 `front` · 뒤 `back`)로 검사 부스를 지나는 퍽을 판정해 `vision/result`를 퍽 하나당 1건 보낸다.
형식·합치기 규칙은 `server/docs`의 통신 규약 3-2. 서버 PC에서 돈다 (client_id `yolo`).

| 파일 | 내용 |
|---|---|
| `yolo_vision.py` | 본 프로그램. 설정은 파일 맨 위 "수정할 값" |
| `capture_top.py` | 상단 카메라 학습 사진 찍기 (1·2·0 키로 폴더, SPACE 1장, A 자동) |
| `eval_top.py` | 상단 모델 시험: 확인용 사진으로 정답률·헷갈림 표·기준 신뢰도별 정답률·처리 시간, 틀린 사진 저장 |
| `models/` | 학습된 모델(`top_best.pt` 등)을 넣는 곳 |
| `captures/` | 판정할 때 찍은 사진 (확인·추가 학습용, Git에 안 올림) |

## 설치
```
cd C:\depal\yolo
python -m pip install -r requirements.txt
```
`..\server\line_common.py`를 같이 쓰므로 폴더 위치(`C:\depal\yolo`)를 바꾸지 않는다.

## 실행

| 명령 | 하는 일 |
|---|---|
| `python yolo_vision.py --fake` | 카메라·모델 없이 가짜 판정. 로봇팔 1이 퍽을 올리면 3초(×speed) 뒤 결과 전송 |
| `python yolo_vision.py --check` | 카메라·모델만 열어 보고 종료 (해상도, 추론 시간, 찾은 것) |
| `python yolo_vision.py --show` | 실제 모드 + 기준 카메라 화면에 기준선·박스 표시 (기준선 맞출 때) |
| `python yolo_vision.py` | 실제 모드 (`CAMERAS` 설정대로) |

서버 실행기와 함께 켤 때 (`server` 폴더):
- `start_all.bat --yolo-fake` : 가짜 YOLO 대신 `yolo_vision.py --fake` (가짜 로봇팔·Mega와 회차 시험)
- `start_all.bat --yolo` : 가짜 YOLO 대신 실제 카메라·모델 모드
- 출력: `server\logs\yolo.log`

## 모델 넣기
1. 학습 결과 `best.pt`를 `models\top_best.pt`로 복사
2. `yolo_vision.py` 맨 위 `CAMERAS`의 top을 `{"source": 0, "model": r"models\top_best.pt"}`로 (카메라 번호는 `--check`로 확인)
3. 앞·뒤는 YOLO11n 모델 `models/side_best.pt`를 공유한다. 서버 PC에서 확인한 카메라 번호를 `front`와 `back`의 `source`에 각각 넣고 `--check`를 실행한다. `source`가 `None`인 측면 카메라는 모델을 로드하지 않는다. `models/side_best_backup_yolo11s.pt`는 백업이다.

**클래스 이름 약속**
- 위(top): `minus_up`, `plus_up` (박스 = 위에서 보이는 건전지 윗면)
- 앞·뒤: `object`(몸통), `scratch`(긁힘), `damage`(손상), `corrosion`(부식). 기존 모델의 `battery`, `normal`도 몸통으로 인식한다. 기준선 통과에는 몸통 박스를 사용하고, 불량 부위는 `defects[].type`에 기록한다.
- 외관불량은 앞·뒤를 합쳐 신뢰도 0.45 이상의 흠이 잡힌 프레임이 2장 이상일 때 판정한다. 한 프레임에서 불량 박스가 여러 개 나와도 1장으로 센다. 외관이 정상이고 상단 극성이 `plus_up`이면 컨트롤러가 재분류한다.

## 모델 시험 (학습 후)
확인용 사진(학습에 안 쓴 것)을 `minus_up` / `plus_up` / `empty` 폴더로 나눠 두고:
```
python eval_top.py --model models\top_best.pt --data D:\top_test
```
결과는 `eval_결과\날짜_시각\` (report.txt, results.csv, 틀린 사진 wrong\). `TOP_CONF`는 report의 "기준 신뢰도별 정답률"을 보고 정한다.

## 실제 카메라 촬영 시험

1. 앞·뒤 카메라 번호와 모델을 설정하고 `python yolo_vision.py --check`로 각 카메라의 실제 해상도를 확인한다. 요청 해상도는 세 카메라 모두 640×480이다.
2. `SIDE_POSITION_GAP_SEC = None`으로 두고 `python yolo_vision.py --show`로 기본 촬영을 확인한다. 기본 촬영은 카메라당 3장이다.
3. 벨트 속도가 일정하면 앞 화면의 1/3과 2/3 사이 이동 시간을 초 단위로 측정해 `SIDE_POSITION_GAP_SEC`에 넣는다. 시험 모드는 앞 카메라가 이동 방향의 첫 1/3 지점을 감지하면 측면 카메라별로 실측 간격을 두고 2장을 모은 뒤 추론한다. 상단은 3장 설정을 사용한다. 앞·뒤 카메라 설치 위치 차이는 `CAM_DELAY_SEC`로 맞춘다.
4. 같은 퍽과 투입 간격으로 두 방식을 비교한다. 퍽당 `vision/result`가 한 건인지, 연속 투입에서 누락되는지, Mega 측정 위치 도착 전에 결과가 나오는지 확인한다. 로그에는 기준선 감지부터 브로커 발행 확인까지 걸린 시간이 나온다. 서버에서 Ollama를 실행할 때도 같은 조건으로 재측정한다.

속도 변화 때문에 촬영 위치가 어긋나면 다중 기준선 촬영을 별도로 검토한다. 측면 2장 시험은 일정 속도를 전제로 한다.

### SC-200 백엔드와 화면 확인

- SC-200 세 대를 동시에 열 때는 검증에 사용한 `sc200_fixed_settings.py`를 이 폴더에 넣고, `SC200_FRONT_SIDE`에 앞 카메라의 실제 위치(`"left"` 또는 `"right"`)를 지정한다. `SC200_SOURCES`의 초기값은 시험 PC에서 사용한 상단 1 / 오른쪽 2 / 왼쪽 3이다.
- 이 모드에서는 시작할 때 카메라를 한 대씩 DSHOW로 열어 프리셋을 적용하고 닫는다. 세 대의 초기화가 끝나면 MSMF로 촬영을 시작한다. 왼쪽 영상은 자동으로 좌우 반전한다. USB를 다시 연결한 날은 번호를 먼저 확인하고, 카메라 설정창에서 낮은 빛 보상이 꺼져 있는지도 확인한다. 프리셋은 프로그램을 재시작할 때 다시 적용한다.
- Windows에서 카메라 번호를 쓰면 기본적으로 DSHOW부터 시도하고, 첫 프레임을 읽을 수 없으면 MSMF를 시도한다. 하나씩 확인하려면 해당 카메라의 `backend`를 `"dshow"` 또는 `"msmf"`로 지정한다.
- `--check` 로그에서 실제 백엔드·해상도와 카메라 제어 읽기값을 확인한다. `CAMERAS`의 `controls`에 현장에서 확인한 `gain`, `auto_exposure`, `exposure`, `auto_wb`, `wb_temperature` 값만 넣으면 요청값·설정 호출 성공 여부·읽기값을 함께 출력한다. 백엔드마다 값의 범위와 의미가 다를 수 있다. 읽기값과 조명 아래 실제 밝기·색을 함께 확인한다.
- 수집할 때 좌우를 뒤집은 L 카메라가 `front`와 `back` 중 어느 쪽인지 확인하고, 그 항목만 `flip_horizontal: True`로 지정한다. 반전은 영상 표시와 추론에 함께 적용된다. 앞 카메라가 L이면 `--show`에서 반전된 화면의 이동 방향을 보고 `LINE_DIR`를 맞춘다.
- 하드웨어 측에서 전달한 벨트 설정은 기본 200 µs, YOLO 구간 이후 측정기까지 50 µs, 측정 후 로봇팔 2까지 200 µs, 재분류 시 50 µs다. 이 값과 별개로 화면 1/3→2/3 실제 이동 시간과 기준선→측정기 도착 시간을 각각 측정한다. 후자는 로그의 기준선→MQTT 발행 확인 시간 및 컨트롤러 로그와 대조한다.

## 현장에서 맞출 값
| 값 | 기본 | 뜻 |
|---|---|---|
| `TRIGGER_CAM` | `front` | 기준선을 볼 카메라. 앞 카메라와 모델을 설정하지 않으면 `top`으로 대체 |
| `LINE_AXIS` / `LINE_POS` / `LINE_DIR` | `x` / 0.5 / +1 | 기본 기준선 방향·위치·통과 방향 (`--show`로 보며 맞춤). 측면 2장 시험에서는 정방향 1/3, 역방향 2/3 위치를 사용 |
| `SIDE_POSITION_GAP_SEC` | `None` | 측면 2장 시험을 켤 때 화면 1/3과 2/3 사이의 실측 이동 시간(초) |
| `MIN_GAP_SEC` | 1.0 | 이 시간 안의 두 번째 통과는 같은 퍽으로 보고 버림 |
| `TOP_CONF` / `SIDE_CONF` / `DEFECT_CONF` | 0.50 / 0.50 / 0.45 | 위 카메라 극 방향, 앞·뒤 건전지, 불량 후보로 인정할 신뢰도 |
| `CAM_DELAY_SEC` | 0 | 기준선 통과 뒤 그 카메라를 찍기까지 (카메라끼리 위치가 다를 때) |

## 실패 처리
- 카메라 한 대라도 화면이 안 옴 → `status: failed`, `error: camera_error` (서버는 재분류)
- 앞·뒤에서 건전지를 못 찾음 → `status: failed`, `error: low_confidence`
- 위 카메라가 애매(신뢰도 미달)함 → 실패가 아니라 앞·뒤 값으로, 그것도 다르면 `unknown`(측정 자리에서 다시 확인)
