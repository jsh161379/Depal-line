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
3. 앞·뒤 모델도 같은 방식. 아직 없는 카메라는 `"model": "fake"`로 두면 가짜 판정으로 채운다

**클래스 이름 약속**
- 위(top): `minus_up`, `plus_up` (박스 = 위에서 보이는 건전지 윗면)
- 앞·뒤: 정상 건전지 클래스(`NORMAL_CLASSES`, 기본 `battery`·`normal`). 그 밖의 클래스는 이름 그대로 불량 종류(`scratch`, `dent`, `sticker` …). `minus_up`·`plus_up` 클래스가 있으면 극 방향으로도 씀

## 모델 시험 (학습 후)
확인용 사진(학습에 안 쓴 것)을 `minus_up` / `plus_up` / `empty` 폴더로 나눠 두고:
```
python eval_top.py --model models\top_best.pt --data D:\top_test
```
결과는 `eval_결과\날짜_시각\` (report.txt, results.csv, 틀린 사진 wrong\). `TOP_CONF`는 report의 "기준 신뢰도별 정답률"을 보고 정한다.

## 정할 값 (🟡 실물 시험 후)
| 값 | 기본 | 뜻 |
|---|---|---|
| `TRIGGER_CAM` | `top` | 기준선을 볼 카메라. 규약은 `front`. 앞 모델이 생기면 바꿀 수 있음 |
| `LINE_AXIS` / `LINE_POS` / `LINE_DIR` | `x` / 0.5 / +1 | 기준선 방향·위치·통과 방향 (`--show`로 보며 맞춤) |
| `MIN_GAP_SEC` | 1.0 | 이 시간 안의 두 번째 통과는 같은 퍽으로 보고 버림 |
| `TOP_CONF` / `SIDE_CONF` / `DEFECT_CONF` | 0.5 | 위 카메라 극 방향, 앞·뒤 건전지, 불량으로 인정할 신뢰도 |
| `CAM_DELAY_SEC` | 0 | 기준선 통과 뒤 그 카메라를 찍기까지 (카메라끼리 위치가 다를 때) |

## 실패 처리
- 카메라 한 대라도 화면이 안 옴 → `status: failed`, `error: camera_error` (서버는 재분류)
- 앞·뒤에서 건전지를 못 찾음 → `status: failed`, `error: low_confidence`
- 위 카메라가 애매(신뢰도 미달)함 → 실패가 아니라 앞·뒤 값으로, 그것도 다르면 `unknown`(측정 자리에서 다시 확인)
