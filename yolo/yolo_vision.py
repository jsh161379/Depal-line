r"""
YOLO 검사 프로그램 (yolo_vision.py) — 통신 규약 v1.4, 3-2
============================================================
카메라 3대(위 top · 앞 front · 뒤 back)로 검사 부스를 지나는 퍽을 판정해서
vision/result를 퍽 하나당 1건 보낸다. 서버 PC에서 돈다. client_id = yolo

  - 퍽이 "기준 카메라" 화면의 기준선을 지나면 → 세 카메라로 몇 장씩 찍어 판정 → 합쳐서 1건 전송
  - 외관 합치기: 앞·뒤 중 한 대라도 불량이면 불량 (conf는 불량 쪽 최고값), 둘 다 정상이면 정상 (conf는 낮은 값)
  - 극 방향 합치기: 위 카메라 신뢰도 ≥ TOP_CONF → 위 카메라 값
                    아니면 앞·뒤가 같은 값일 때만 채택 → 그 외 unknown
  - 카메라 한 대라도 화면이 안 오면 status failed (error camera_error)
    앞·뒤에서 건전지를 못 찾으면 status failed (error low_confidence)
  - 같은 퍽 중복 방지: 화면 안 물체를 이전 화면과 짝지어 기준선 통과를 한 번만 셈 + 최소 간격(MIN_GAP_SEC)
  - cmd/yolo scan_pallet: 팔레트 카메라가 정해지기 전까지는 "가득 찬 팔레트에서 집어 간 칸을 뺀 목록"으로 답함
  - 1초마다 hb/yolo (카메라별 상태 cams 포함), 끊기면 status/yolo offline (LWT)

모델 자리
  - 앞·뒤는 models\side_best.pt (YOLO11n)를 공유한다. 실제 카메라 번호를 CAMERAS의 source에 지정한다.
  - 상단 모델이 학습되면 top의 "model"에 파일 경로를 넣는다.
  - 학습 클래스 이름 약속 (아래 "클래스 이름" 설정, 학습 설정은 train\top.yaml · train\side.yaml)
      위(top): minus_up, plus_up  (박스 = 위에서 보이는 건전지 윗면)
      앞·뒤  : 건전지 몸통 박스 = object (극 방향 구분 없음). 기존 모델의 battery, normal도 몸통으로 인식한다.
               불량 부위 박스 = scratch, damage, corrosion → 이름 그대로 defects[].type
      모델을 열 때 약속에 없는 클래스 이름이 있으면 경고를 띄운다 (대문자·띄어쓰기는 자동으로 맞춤)
  - 실제 모드에서 모델이 아직 없는 카메라: 위 = 판정 없음(앞·뒤 값으로), 앞·뒤 = 정상·방향 모름 (무작위 가짜 판정 안 씀)

필요한 것: Python 3.11 이상
  python -m pip install "aiomqtt>=2" pydantic opencv-python
  python -m pip install ultralytics          ← 실제 모델(.pt)을 쓸 때만
  ..\server\line_common.py 를 같이 씀 (이 폴더를 C:\depal\yolo 에 두면 자동으로 찾음)

사용법
  python yolo_vision.py --fake            카메라·모델 없이 가짜 판정. 로봇팔 1이 퍽을 올리면 BOOTH_SEC 뒤 결과 전송
                                          (가짜 장치들과 회차 시험용. run_all.py --yolo-fake가 이것으로 켬)
  python yolo_vision.py                   CAMERAS에 설정한 실제 카메라·모델 사용
  python yolo_vision.py --show            기준 카메라 화면에 기준선·박스를 띄움 (기준선 위치 맞출 때)
  python yolo_vision.py --check           카메라·모델만 열어 보고 종료 (MQTT 없이)
  가짜 판정 확률 조절: --defect 0.15 --plus-up 0.1 --fail 0.0 --seed 1
"""

import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# ===================== 수정할 값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883

# 카메라별 설정. source: 카메라 번호(0, 1, 2) / 동영상 파일 경로 / None(카메라 없음)
#                model : 모델 파일 경로(이 폴더 기준) 또는 "fake"
CAMERAS = {
    "top":   {"source": 0,    "model": "fake"},        # 학습 후 예: r"models\top_best.pt"
    "front": {"source": None, "model": r"models\side_best.pt"},  # 서버 PC의 앞 카메라 번호 입력
    "back":  {"source": None, "model": r"models\side_best.pt"},  # 서버 PC의 뒤 카메라 번호 입력
}
FRAME_WIDTH, FRAME_HEIGHT = 640, 480
USE_DSHOW = True                 # Windows에서 카메라 번호로 열 때 DirectShow 사용

# 기준선 (🟡 실물 시험 후 정함) — 규약 3-2: 앞 카메라 기준
TRIGGER_CAM = "front"
TRIGGER_FALLBACK = "top"         # 앞 카메라에 모델·카메라가 없으면 이 카메라로 기준선을 봄 (측면 모델 전)
LINE_AXIS = "x"                  # "x" = 세로선(벨트가 좌우로 흐를 때), "y" = 가로선
LINE_POS = 0.5                   # 화면 폭(또는 높이)의 비율. 0.5 = 가운데
LINE_DIR = +1                    # +1 = 왼쪽→오른쪽(위→아래)으로 지날 때, -1 = 반대
MAX_JUMP_PX = 150                # 이전 화면의 같은 물체로 볼 최대 이동 거리(픽셀)
MIN_GAP_SEC = 1.0                # 🟡 이 시간 안의 두 번째 통과는 같은 퍽으로 보고 버림 (벨트 속도로 계산)

# 판정
FRAMES_PER_CAM = 3               # 상단 및 기본 모드의 촬영 장수
FRAME_GAP_SEC = 0.05             # 기본 프레임 간격(초)
# 서버 PC에서 측정한 화면 1/3 → 2/3 이동 시간(초). None이면 기본 촬영을 사용한다.
# 값을 지정하면 측면만 2장 촬영하고, 앞 카메라 기준선을 이동 방향의 첫 1/3 지점에 둔다.
SIDE_POSITION_GAP_SEC: float | None = None
CAM_DELAY_SEC = {"top": 0.0, "front": 0.0, "back": 0.0}  # 기준선 통과 뒤 이 카메라를 찍기까지 (카메라 위치가 다를 때)
TOP_CONF = 0.50                  # 🟡 위 카메라 극 방향을 믿을 최소 신뢰도
SIDE_CONF = 0.50                 # 🟡 앞·뒤에서 건전지를 찾았다고 볼 최소 신뢰도
DEFECT_CONF = 0.50               # 🟡 불량으로 볼 최소 신뢰도
TRIGGER_CONF = 0.40              # 기준선 통과를 볼 때 쓰는 최소 신뢰도
IMGSZ = 640                      # 학습 때 이미지 크기와 같게
CAM_TIMEOUT_SEC = 1.0            # 이 시간 넘게 새 화면이 없으면 카메라 오류

# 클래스 이름 (규약 3-2. 라벨링을 시작한 뒤에는 순서·이름을 바꾸지 않는다)
POLARITY_CLASSES = {"minus_up", "plus_up"}
NORMAL_CLASSES = {"object", "battery", "normal"}  # 몸통 클래스 (battery·normal은 기존 모델 호환)
DEFECT_CLASSES = {"scratch", "damage", "corrosion"}
IGNORE_CLASSES: set[str] = set()

# 판정 사진 저장 (확인·추가 학습용). captures\날짜\seq번호_카메라.jpg
SAVE_FRAMES = True
SAVE_DIR = HERE / "captures"

# --fake 모드
BOOTH_SEC = 3.0                  # 퍽이 벨트에 올라간 뒤 부스 기준선까지 (--speed 1 기준, 가짜 Mega와 같은 값)
FAKE_DEFECT_TYPES = sorted(DEFECT_CLASSES)

# 팔레트 (팔레트 카메라 미정 ❓ — 정해지기 전까지 가득 찬 팔레트로 가정)
PALLET_ROWS, PALLET_COLS = 2, 3   # 팔레트 한 판 6개 = 2줄 × 3칸 (11/21 변경, 규약 v1.5.3. v1.5.1은 2×4)
# ====================================================

sys.path.insert(0, str(HERE.parent / "server"))   # line_common.py

import argparse
import asyncio
import contextlib
import json
import random
import threading
import time
from datetime import datetime

import aiomqtt

from line_common import now_str, run_device, run_main

CAM_NAMES = ("top", "front", "back")
SIDE_CAMS = ("front", "back")


def log(msg):
    print(f"{datetime.now():%H:%M:%S} {msg}", flush=True)


def class_name(name) -> str:
    """학습 클래스 이름 → 규약 값 (영문 소문자, 띄어쓰기·- 는 _, 64자 이하). 예: Plus-Up → plus_up"""
    return str(name).strip().lower().replace(" ", "_").replace("-", "_")[:64]


# ---------------------------------------------------------------- 카메라

class Camera(threading.Thread):
    """카메라 하나를 계속 읽어 마지막 화면만 들고 있는다 (끊기면 2초마다 다시 열기)"""

    def __init__(self, name, source):
        super().__init__(daemon=True, name=f"cam-{name}")
        self.cam_name, self.source = name, source
        self.lock = threading.Lock()
        self.frame, self.t = None, 0.0
        self.count = 0
        self.stop_flag = False
        self.is_file = isinstance(source, str) and not source.isdigit()

    def _open(self):
        import cv2
        src = int(self.source) if isinstance(self.source, str) and self.source.isdigit() else self.source
        if isinstance(src, int) and USE_DSHOW and os.name == "nt":
            cap = cv2.VideoCapture(src, cv2.CAP_DSHOW)
        else:
            cap = cv2.VideoCapture(src)
        if isinstance(src, int):
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, FRAME_WIDTH)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, FRAME_HEIGHT)
        return cap

    def run(self):
        reported = False
        while not self.stop_flag:
            cap = self._open()
            if not cap.isOpened():
                if not reported:
                    log(f"[cam {self.cam_name}] 열 수 없음 (source={self.source!r}) → 2초마다 다시 시도")
                    reported = True
                time.sleep(2.0)
                continue
            log(f"[cam {self.cam_name}] 열림 (source={self.source!r}, "
                f"{int(cap.get(3))}x{int(cap.get(4))})")
            reported = False
            fps = cap.get(5) or 30.0
            while not self.stop_flag:
                ok, frame = cap.read()
                if not ok:
                    if self.is_file:            # 동영상 파일은 끝나면 처음부터 다시
                        cap.set(1, 0)
                        continue
                    log(f"[cam {self.cam_name}] 화면을 읽지 못함 → 다시 열기")
                    break
                with self.lock:
                    self.frame, self.t = frame, time.monotonic()
                    self.count += 1
                if self.is_file:
                    time.sleep(1.0 / max(fps, 1.0))
            cap.release()
            time.sleep(0.5)

    def latest(self, newer_than=0.0):
        """(화면, 시각). 화면이 없거나 newer_than보다 오래됐으면 (None, 0)"""
        with self.lock:
            if self.frame is None or self.t <= newer_than:
                return None, 0.0
            return self.frame.copy(), self.t

    def ok(self):
        with self.lock:
            return self.frame is not None and time.monotonic() - self.t < CAM_TIMEOUT_SEC


# ---------------------------------------------------------------- 모델

class Model:
    """YOLO 모델 하나 (카메라마다 따로). 여러 스레드에서 부를 수 있게 잠금"""

    def __init__(self, path):
        from ultralytics import YOLO
        p = Path(path)
        if not p.is_absolute():
            p = HERE / p
        if not p.exists():
            raise FileNotFoundError(f"모델 파일이 없음: {p}")
        self.path = p
        self.yolo = YOLO(str(p))
        self.lock = threading.Lock()
        self.names = {i: class_name(n) for i, n in self.yolo.names.items()}
        unknown = [n for n in self.names.values()
                   if n not in POLARITY_CLASSES | NORMAL_CLASSES | DEFECT_CLASSES | IGNORE_CLASSES]
        if unknown:
            log(f"[yolo] ⚠ {p.name}: 약속에 없는 클래스 {unknown} → 불량 종류로 그대로 보냄. "
                f"오타라면 학습 설정(train\\*.yaml)과 맞춰 다시 학습")

    def detect(self, frame, conf):
        """[(클래스 이름, 신뢰도, (x1, y1, x2, y2)), ...]"""
        with self.lock:
            r = self.yolo.predict(frame, conf=conf, imgsz=IMGSZ, verbose=False)[0]
        out = []
        if r.boxes is None:
            return out
        for c, p, b in zip(r.boxes.cls.tolist(), r.boxes.conf.tolist(), r.boxes.xyxy.tolist()):
            if self.names[int(c)] in IGNORE_CLASSES:
                continue
            out.append((self.names[int(c)], float(p), tuple(b)))
        return out


# ---------------------------------------------------------------- 카메라별 판정

def judge_top(dets_per_frame):
    """위 카메라: 여러 장 중 신뢰도가 가장 높은 극 방향 박스 → (polarity 또는 None, conf)"""
    best = None
    for dets in dets_per_frame:
        for name, conf, _ in dets:
            if name in POLARITY_CLASSES and (best is None or conf > best[1]):
                best = (name, conf)
    if best is None:
        return None, 0.0
    return best[0], round(best[1], 3)


def judge_side(cam, dets_per_frame):
    """앞·뒤 카메라: dict(found, result, conf, defects, polarity, pol_conf)"""
    found_conf = 0.0        # 어떤 클래스든 건전지를 찾음 (불량 부위만 보여도 찾은 것)
    body_conf = 0.0         # 건전지 몸통 박스의 신뢰도 → 정상일 때 conf
    defects: dict[str, float] = {}
    pol = None
    for dets in dets_per_frame:
        for name, conf, _ in dets:
            found_conf = max(found_conf, conf)
            if name in POLARITY_CLASSES or name in NORMAL_CLASSES:
                body_conf = max(body_conf, conf)
            if name in POLARITY_CLASSES:
                if pol is None or conf > pol[1]:
                    pol = (name, conf)
            elif name not in NORMAL_CLASSES and conf >= DEFECT_CONF:
                defects[name] = max(defects.get(name, 0.0), conf)
    found = found_conf >= SIDE_CONF
    d_list = [{"type": t, "conf": round(c, 3), "cam": cam} for t, c in sorted(defects.items(), key=lambda x: -x[1])]
    return {"found": found,
            "result": "defect" if d_list else "normal",
            "conf": round(max(defects.values()) if d_list else (body_conf or found_conf), 3),
            "defects": d_list,
            "polarity": pol[0] if pol else None,
            "pol_conf": round(pol[1], 3) if pol else 0.0}


class FakeJudge:
    """가짜 판정 (모델 자리). 카메라·모델 없이 같은 형식의 결과를 만든다"""

    def __init__(self, rnd, defect, plus_up, fail):
        self.rnd, self.defect, self.plus_up, self.fail = rnd, defect, plus_up, fail
        self.cur = None

    def new_puck(self):
        """퍽 하나의 '정답'을 먼저 정해 두고, 세 카메라가 그것을 보게 한다"""
        r = self.rnd
        x = r.random()
        self.cur = {
            "fail": r.random() < self.fail,
            "polarity": "plus_up" if x < self.plus_up else "minus_up",
            "top_unsure": r.random() < 0.08,            # 가끔 위 카메라가 애매 → 앞·뒤로 넘어감
            "defect_cam": r.choice(SIDE_CAMS) if r.random() < self.defect else None,
            "defect_type": r.choice(FAKE_DEFECT_TYPES),
        }

    def top(self):
        c = self.cur
        conf = self.rnd.uniform(0.25, 0.45) if c["top_unsure"] else self.rnd.uniform(0.85, 0.98)
        return c["polarity"], round(conf, 3)

    def side(self, cam):
        c, r = self.cur, self.rnd
        d = []
        if c["defect_cam"] == cam:
            d = [{"type": c["defect_type"], "conf": round(r.uniform(0.75, 0.97), 3), "cam": cam}]
        return {"found": True, "result": "defect" if d else "normal",
                "conf": d[0]["conf"] if d else round(r.uniform(0.85, 0.98), 3), "defects": d,
                "polarity": c["polarity"] if r.random() < 0.9 else None, "pol_conf": round(r.uniform(0.6, 0.9), 3)}


def neutral_side():
    """실제 모드에서 모델이 아직 없는 앞·뒤 카메라: 정상·방향 모름으로 둔다.
    무작위 가짜 판정을 섞으면 실제 건전지가 불량·재분류로 잘못 갈 수 있어서다 (가짜 판정은 --fake 때만)"""
    return {"found": True, "result": "normal", "conf": 1.0, "defects": [], "polarity": None, "pol_conf": 0.0}


# ---------------------------------------------------------------- 합치기 (규약 3-2)

def combine(seq, top, sides, cam_error):
    """top: (polarity, conf), sides: {"front": dict, "back": dict}, cam_error: 오류 난 카메라 이름 목록"""
    body = {"seq": seq, "ts": now_str()}
    fail = {"status": "failed", "result": None, "conf": None, "polarity": None, "defects": []}
    if cam_error:
        return body | fail | {"error": "camera_error", "cams": cam_error}
    not_found = [cam for cam in SIDE_CAMS if not sides[cam]["found"]]
    if not_found:
        return body | fail | {"error": "low_confidence", "cams": not_found}

    # 외관
    bad = [sides[c] for c in SIDE_CAMS if sides[c]["result"] == "defect"]
    if bad:
        result, conf = "defect", max(s["conf"] for s in bad)
    else:
        result, conf = "normal", min(sides[c]["conf"] for c in SIDE_CAMS)
    defects = [d for c in SIDE_CAMS for d in sides[c]["defects"]]

    # 극 방향: 위 카메라 우선 → 앞·뒤 일치 → unknown
    top_pol, top_conf = top
    if top_pol and top_conf >= TOP_CONF:
        polarity = top_pol
    elif sides["front"]["polarity"] and sides["front"]["polarity"] == sides["back"]["polarity"]:
        polarity = sides["front"]["polarity"]
    else:
        polarity = "unknown"

    per_cam = [{"cam": "top", "result": None, "conf": top_conf, "polarity": top_pol or "unknown"}]
    per_cam += [{"cam": c, "result": sides[c]["result"], "conf": sides[c]["conf"],
                 "polarity": sides[c]["polarity"] or "unknown"} for c in SIDE_CAMS]
    return body | {"status": "ok", "result": result, "conf": round(conf, 3), "polarity": polarity,
                   "defects": defects, "sides": per_cam}


# ---------------------------------------------------------------- 기준선 감시 (스레드)

class LineWatcher(threading.Thread):
    """기준 카메라 화면에서 물체가 기준선을 지나면 on_cross() 호출"""

    def __init__(self, cam: Camera, model: Model, on_cross, show=False, line_pos=LINE_POS):
        super().__init__(daemon=True, name="line-watcher")
        self.cam, self.model, self.on_cross, self.show = cam, model, on_cross, show
        self.line_pos = line_pos
        self.stop_flag = False
        self.last_fire = 0.0

    def run(self):
        import cv2
        prev: list[float] = []      # 이전 화면 물체들의 위치(기준 축 좌표)
        last_t = 0.0
        while not self.stop_flag:
            frame, t = self.cam.latest(newer_than=last_t)
            if frame is None:
                time.sleep(0.01)
                continue
            last_t = t
            h, w = frame.shape[:2]
            line = (w if LINE_AXIS == "x" else h) * self.line_pos
            dets = self.model.detect(frame, TRIGGER_CONF)
            # 건전지 몸통 박스만 센다 (앞·뒤 모델은 몸통 + 불량 부위 박스가 같이 나오므로 한 퍽이 두 번 세지지 않게)
            bodies = [d for d in dets if d[0] in POLARITY_CLASSES | NORMAL_CLASSES]
            cur = []
            for _, _, (x1, y1, x2, y2) in bodies:
                cur.append((x1 + x2) / 2 if LINE_AXIS == "x" else (y1 + y2) / 2)
            fired = False
            for c in cur:
                if fired:
                    break
                if not prev:
                    break
                p = min(prev, key=lambda v: abs(v - c))           # 이전 화면의 같은 물체 (가장 가까운 것)
                if abs(p - c) > MAX_JUMP_PX:
                    continue
                if LINE_DIR * (p - line) < 0 <= LINE_DIR * (c - line):   # 이번 화면에서 기준선을 넘음
                    now = time.monotonic()
                    fired = True
                    if now - self.last_fire >= MIN_GAP_SEC:
                        self.last_fire = now
                        self.on_cross()
                    else:
                        log(f"[yolo] 기준선 통과 {now - self.last_fire:.2f}초 만에 또 → 같은 퍽으로 보고 버림")
            prev = cur
            if self.show:
                view = frame.copy()
                if LINE_AXIS == "x":
                    cv2.line(view, (int(line), 0), (int(line), h), (0, 255, 255), 2)
                else:
                    cv2.line(view, (0, int(line)), (w, int(line)), (0, 255, 255), 2)
                for name, conf, (x1, y1, x2, y2) in dets:
                    cv2.rectangle(view, (int(x1), int(y1)), (int(x2), int(y2)), (0, 255, 0), 2)
                    cv2.putText(view, f"{name} {conf:.2f}", (int(x1), max(15, int(y1) - 5)),
                                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
                cv2.imshow(f"trigger: {self.cam.cam_name} (q = close window)", view)
                if (cv2.waitKey(1) & 0xFF) in (ord("q"), 27):
                    cv2.destroyAllWindows()
                    self.show = False


# ---------------------------------------------------------------- 본체

class Vision:
    def __init__(self, args):
        self.a = args
        self.rnd = random.Random(args.seed)
        self.fake = FakeJudge(self.rnd, args.defect, args.plus_up, args.fail)
        self.cams: dict[str, Camera] = {}
        self.models: dict[str, Model | None] = {}
        self.queue: asyncio.Queue | None = None
        self.loop: asyncio.AbstractEventLoop | None = None
        self.client: aiomqtt.Client | None = None
        self.seq = 0
        self.watcher: LineWatcher | None = None
        # 팔레트 (scan_pallet)
        self.all_cells = [(r, c) for r in range(PALLET_ROWS) for c in range(PALLET_COLS)]
        self.pallet = set(self.all_cells)
        self.pick_cmds: dict[str, int] = {}

    # ----- 준비 -----
    def setup(self):
        if self.a.fake:
            log("[yolo] --fake: 카메라·모델 없이 가짜 판정 (로봇팔 1이 퍽을 올리면 결과 전송)")
            return
        if SIDE_POSITION_GAP_SEC is not None and not (0 < SIDE_POSITION_GAP_SEC < float("inf")):
            raise SystemExit("[yolo] SIDE_POSITION_GAP_SEC에 양수 초 단위의 실측값을 넣으세요.")
        for name in CAM_NAMES:
            cfg = CAMERAS[name]
            if name in SIDE_CAMS and cfg["source"] is None:
                self.models[name] = None
                log(f"[yolo] {name}: 카메라 번호 미설정")
                continue
            if cfg["model"] == "fake":
                self.models[name] = None
            else:
                self.models[name] = Model(cfg["model"])
                log(f"[yolo] {name} 모델: {self.models[name].path.name} 클래스 {list(self.models[name].names.values())}")
            if cfg["source"] is not None:
                self.cams[name] = Camera(name, cfg["source"])
                self.cams[name].start()
            elif self.models[name] is not None:
                raise SystemExit(f"[yolo] {name}: 모델은 있는데 카메라(source)가 없음 → CAMERAS 확인")
        self.trigger = next((c for c in (TRIGGER_CAM, TRIGGER_FALLBACK)
                             if self.models.get(c) is not None and c in self.cams), None)
        if self.trigger is None:
            raise SystemExit(f"[yolo] 기준 카메라 {TRIGGER_CAM}(없으면 {TRIGGER_FALLBACK})에 카메라와 모델이 있어야 "
                             f"기준선 통과를 볼 수 있음\n       → CAMERAS를 확인하거나, 시험만 하려면 --fake")
        if SIDE_POSITION_GAP_SEC is not None and (
            self.trigger != "front" or not all(self.models[n] is not None for n in SIDE_CAMS)
        ):
            raise SystemExit("[yolo] 측면 2장 시험에는 앞 기준 카메라와 앞·뒤 모델을 설정해야 합니다.")
        if self.trigger != TRIGGER_CAM:
            log(f"[yolo] 기준 카메라 {TRIGGER_CAM}에 모델이 없어 {self.trigger}로 기준선을 봄")
        fakes = [n for n in CAM_NAMES if self.models[n] is None]
        if fakes:
            log(f"[yolo] 가짜 판정 카메라: {fakes} (모델이 생기면 CAMERAS에 경로를 넣으세요)")

    def cam_state(self):
        if self.a.fake:
            return {n: "fake" for n in CAM_NAMES}
        out = {}
        for n in CAM_NAMES:
            if n in self.cams:
                out[n] = "ok" if self.cams[n].ok() else "error"
            else:
                out[n] = "fake"
        return out

    # ----- 기준선 통과 → 판정 대기열 -----
    def on_cross(self):
        """다른 스레드에서 부름"""
        if self.loop and self.queue:
            self.loop.call_soon_threadsafe(self.queue.put_nowait, time.monotonic())

    # ----- 퍽 하나 판정 -----
    def _shoot(self, name):
        """카메라 하나로 프레임을 모은 뒤 판정한다. 필요한 프레임이 없으면 None."""
        cam = self.cams[name]
        side_gap = SIDE_POSITION_GAP_SEC if name in SIDE_CAMS else None
        frame_count = 2 if side_gap is not None else FRAMES_PER_CAM
        frame_gap = side_gap if side_gap is not None else FRAME_GAP_SEC
        frames = []
        next_capture = time.monotonic() + CAM_DELAY_SEC.get(name, 0.0)
        while len(frames) < frame_count and time.monotonic() < next_capture + CAM_TIMEOUT_SEC:
            f, t = cam.latest(newer_than=next_capture)
            if f is None:
                time.sleep(0.005)
                continue
            frames.append(f)
            next_capture = t + frame_gap
        if not frames or (side_gap is not None and len(frames) != frame_count):
            return None
        conf = min(TOP_CONF, SIDE_CONF, DEFECT_CONF) * 0.5 if name == "top" else min(SIDE_CONF, DEFECT_CONF) * 0.5
        dets = [self.models[name].detect(f, conf) for f in frames]
        return frames, dets

    def _judge_real(self, seq):
        """세 카메라 판정 (블로킹 → 스레드에서 실행)"""
        self.fake.new_puck()
        shots, cam_error = {}, []
        threads = {}

        def work(n):
            shots[n] = self._shoot(n)

        for n in CAM_NAMES:
            if self.models[n] is not None:
                threads[n] = threading.Thread(target=work, args=(n,))
                threads[n].start()
        for th in threads.values():
            th.join()

        top = (None, 0.0)            # 위 모델 없음 → 극 방향은 앞·뒤 값 또는 unknown(측정으로 확인)
        sides = {}
        for n in CAM_NAMES:
            if self.models[n] is None:
                if n != "top":
                    sides[n] = neutral_side()
                continue
            if shots.get(n) is None:
                cam_error.append(n)
                continue
            _, dets = shots[n]
            if n == "top":
                top = judge_top(dets)
            else:
                sides[n] = judge_side(n, dets)
        body = combine(seq, top, sides, cam_error)
        if SAVE_FRAMES and not self.a.no_save:
            self._save(seq, {n: s[0][len(s[0]) // 2] for n, s in shots.items() if s})
        return body

    def _judge_fake(self, seq):
        self.fake.new_puck()
        if self.fake.cur["fail"]:
            return combine(seq, (None, 0.0), {}, ["front"])
        return combine(seq, self.fake.top(), {c: self.fake.side(c) for c in SIDE_CAMS}, [])

    def _save(self, seq, frames):
        import cv2
        folder = SAVE_DIR / datetime.now().strftime("%Y%m%d")
        folder.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%H%M%S")
        for n, f in frames.items():
            cv2.imwrite(str(folder / f"{stamp}_seq{seq:04d}_{n}.jpg"), f)

    async def judge_loop(self, client):
        self.client = client
        self.loop = asyncio.get_running_loop()
        self.queue = asyncio.Queue()
        if not self.a.fake and self.watcher is None:
            line_pos = LINE_POS
            if SIDE_POSITION_GAP_SEC is not None:
                line_pos = 1 / 3 if LINE_DIR > 0 else 2 / 3
            self.watcher = LineWatcher(self.cams[self.trigger], self.models[self.trigger], self.on_cross,
                                       self.a.show, line_pos=line_pos)
            self.watcher.start()
            log(f"[yolo] 기준선 감시 시작: {self.trigger} 카메라, {LINE_AXIS}={line_pos:.3f}, 방향 {LINE_DIR:+d}")
        while True:
            queued_at = await self.queue.get()   # 퍽 하나 → 순서대로 처리 (서버는 결과 순서로 퍽을 짝지음)
            self.seq += 1
            t0 = time.monotonic()
            if self.a.fake:
                body = self._judge_fake(self.seq)
            else:
                body = await asyncio.to_thread(self._judge_real, self.seq)
            ms = (time.monotonic() - t0) * 1000
            log(f"[yolo] vision/result seq {body['seq']}: {body['status']} {body.get('result')} "
                f"{body.get('polarity')}" + (f" {[d['type'] for d in body['defects']]}" if body.get("defects") else "")
                + (f" ({body.get('error')} {body.get('cams')})" if body.get("error") else "") + f"  [{ms:.0f}ms]")
            with contextlib.suppress(aiomqtt.MqttError):
                await client.publish("vision/result", json.dumps(body, ensure_ascii=False), qos=1)
                if not self.a.fake:
                    elapsed_ms = (time.monotonic() - queued_at) * 1000
                    log(f"[yolo] seq {body['seq']}: 기준선 감지 → 브로커 발행 확인 {elapsed_ms:.0f}ms")

    # ----- MQTT: 다른 장치 메시지 -----
    async def _fake_booth(self):
        await asyncio.sleep(BOOTH_SEC * self.a.speed)
        if self.queue:
            self.queue.put_nowait(time.monotonic())

    def on_extra(self, topic, text):
        try:
            msg = json.loads(text)
        except ValueError:
            return
        if not isinstance(msg, dict):
            return
        if topic == "cmd/arm1" and msg.get("action") == "pick":
            self.pallet.discard(tuple((msg.get("params") or {}).get("cell", ())))
            self.pick_cmds[msg.get("cmd_id")] = msg.get("battery_id")
        elif topic == "res/arm1" and msg.get("cmd_id") in self.pick_cmds:
            if msg.get("status") == "done":
                self.pick_cmds.pop(msg["cmd_id"])
                if self.a.fake:                  # 가짜 모드: 퍽이 벨트에 올라감 → 곧 부스 통과
                    asyncio.get_running_loop().create_task(self._fake_booth())
            elif msg.get("status") in ("failed", "rejected"):
                self.pick_cmds.pop(msg["cmd_id"], None)
        elif topic == "ui/cmd" and msg.get("action") == "start" and not self.pallet:
            self.pallet = set(self.all_cells)
            log("[yolo] 새 팔레트로 봄 (팔레트 카메라 정해지기 전 임시)")

    async def handler(self, action, params):
        if action == "scan_pallet":
            cells = sorted(self.pallet)
            return "done", {"cells": [list(c) for c in cells], "conf": [1.0 for _ in cells]}, None
        return "rejected", {}, f"unknown action {action}"

    async def run(self):
        await run_device("yolo", self.handler, host=BROKER_HOST, port=BROKER_PORT,
                         info_fn=lambda: {"cams": self.cam_state(), "fake": bool(self.a.fake)},
                         extra_topics=("cmd/arm1", "res/arm1", "ui/cmd"), on_extra=self.on_extra,
                         extra_tasks=(self.judge_loop,), log=log)


def check(v: Vision):
    """--check: 카메라·모델 확인 후 종료"""
    v.setup()
    time.sleep(3.0)
    for n in CAM_NAMES:
        if n in v.cams:
            c = v.cams[n]
            f, _ = c.latest()
            size = f"{f.shape[1]}x{f.shape[0]}" if f is not None else "-"
            print(f"  {n:5s}: 카메라 {'OK' if c.ok() else '화면 없음'} ({size}, 3초 동안 {c.count}장)"
                  f", 모델 {v.models[n].path.name if v.models[n] else 'fake'}")
            if f is not None and v.models[n]:
                t0 = time.monotonic()
                d = v.models[n].detect(f, 0.25)
                print(f"         추론 {(time.monotonic() - t0) * 1000:.0f}ms (첫 실행은 느림), 찾은 것 {[(x[0], round(x[1], 2)) for x in d]}")
        else:
            print(f"  {n:5s}: 카메라 미설정")
    os._exit(0)


def parse(argv=None):
    p = argparse.ArgumentParser(description="YOLO 검사 프로그램 (vision/result)")
    p.add_argument("--fake", action="store_true", help="카메라·모델 없이 가짜 판정")
    p.add_argument("--show", action="store_true", help="기준 카메라 화면에 기준선·박스 표시")
    p.add_argument("--check", action="store_true", help="카메라·모델만 확인하고 종료")
    p.add_argument("--no-save", action="store_true", help="판정 사진을 저장하지 않음")
    p.add_argument("--speed", type=float, default=0.3, help="--fake 때 시간 배율 (가짜 Mega와 같게, 1=실제)")
    p.add_argument("--defect", type=float, default=0.15, help="가짜 판정: 외관 불량 확률")
    p.add_argument("--plus-up", type=float, default=0.1, help="가짜 판정: +극이 위 확률")
    p.add_argument("--fail", type=float, default=0.0, help="가짜 판정(--fake): 검사 실패 확률")
    p.add_argument("--seed", type=int, default=None, help="난수 시드")
    return p.parse_args(argv)


if __name__ == "__main__":
    args = parse()
    if sys.stdout and hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    vision = Vision(args)
    if args.check:
        check(vision)
    vision.setup()
    run_main(vision.run)
