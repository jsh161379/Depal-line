"""
라인 컨트롤러 - 퍽 줄 관리 방식 (통신 규약 v1.2, 4장)
=====================================================
벨트 위에 퍽이 여러 개 동시에 있다. 컨트롤러는 퍽을 올린 순서(battery_id)대로 줄을 만들고,
장치들이 보내는 알림을 받을 때마다 그 퍽의 다음 단계를 정한다.

  로봇팔 1(cmd/pi pick done) ──▶ 퍽 줄 맨 뒤에 추가
  YOLO(vision/result)       ──▶ 줄에서 YOLO 결과가 없는 첫 퍽에 붙임 → Mega에 plan (PASS/MEASURE + 보낼 곳)
  Mega(event/mega)
    WAIT_PLAN   계획 없이 측정 자리 도착 → 결과 없는 첫 퍽을 "외관 검사 실패" → plan PASS 재분류
    MEASURED    측정값 → 등급 판정 → route (A·B·전압불량·재분류)
    DIVERTED / SORTED  배출 완료 → server/results 발행, 줄에서 뺌
    AT_PICK     기록만 (Mega가 시간으로 계산한 값)
    NO_RESPONSE / FAULT / ENV_ALARM → 이벤트, 필요하면 일시정지

  새 퍽 올리기(feeder): 벨트 위 퍽이 MAX_ON_BELT보다 적고, 마지막으로 올린 뒤 FEED_INTERVAL_SEC가 지났을 때
  일시정지: 새 퍽을 올리지 않고 belt run:false (그 자리 정지)
  회차 종료: 새 퍽을 올리지 않고 belt run:false, drain:true (벨트 위 퍽을 모두 분류한 뒤 정지)
  비상정지: 실제 버튼(event/estop) 또는 작업자 화면 → pi·mega에 estop, STOP_CONFIRMED

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
실행: python line_controller.py      (명령은 관리자 프로그램 버튼, 또는 python line_cmd.py start / pause / resume / skip / stop / estop / reset)
"""

# ===================== 설정값 (여기만 바꾸면 됨) =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
NAME = "controller"

# 벨트 운용 (🟡 실제 구동 후 하드웨어 실측값으로 바꿀 것)
MAX_ON_BELT = 2                 # 벨트 위에 동시에 둘 퍽 수
FEED_INTERVAL_SEC = 3.0         # 로봇팔 1이 퍽을 올리는 최소 간격
PUCK_TIMEOUT_SEC = 60.0         # 퍽을 올린 뒤 배출 완료까지 이 시간이 넘으면 ERROR + 일시정지

ACCEPT_TIMEOUT_SEC = 2.0        # accepted 응답까지
TIMEOUT_SEC = {                 # done/failed 응답까지
    "pi":   {"pick": 20, "estop": 3, "reset": 30},
    "mega": {"plan": 2, "route": 2, "belt": 2, "belt_drain": 60, "estop": 3, "reset": 30},
    "yolo": {"scan_pallet": 5},
    "agv":  {"go": 120, "return": 120},
}

# 등급 판정 기준 (🟡 임시값, 측정 헤드 제작 후 확정) — 바꾸면 RULE_VERSION도 바꿀 것
RULE_VERSION = "temp-2026-11-10"
GRADE_A = {"v_open_min": 1.50, "r_int_max": 0.9}
GRADE_B = {"v_open_min": 1.30, "r_int_max": 1.5}

USE_AGV = False                 # AGV가 준비되면 True (False면 출발 조건만 이벤트로 알림)
AGV_BATCH = 8                   # A 몇 개마다 출발 (잠정)

WATCH_DEVICES = ["pi", "mega", "yolo"]   # 끊기면 일시정지할 장치
# ====================================================================

import asyncio
import contextlib
import json
import time
from dataclasses import dataclass, field

import aiomqtt

from line_common import event_body, estop_body, make_run_id, new_id, now_str, run_main, heartbeat_loop, to_text

GRADES = ["A", "B", "NG_VISUAL", "NG_VOLTAGE", "RESORT"]
DEST = {"A": "AGV", "B": "B_BOX", "NG_VISUAL": "NG_VISUAL_BOX", "NG_VOLTAGE": "NG_VOLTAGE_BOX", "RESORT": "RESORT_BOX"}
GRADE_KR = {"A": "A", "B": "B", "NG_VISUAL": "외관이상", "NG_VOLTAGE": "전압이상", "RESORT": "재분류"}
FAILED_VISION = {"status": "failed", "result": None, "conf": None, "polarity": None, "defects": []}


class CmdError(Exception):
    def __init__(self, kind: str, detail: str):
        super().__init__(f"{kind}: {detail}")
        self.kind, self.detail = kind, detail


class SkipBattery(Exception):
    """작업자가 이 건전지를 건너뛰기로 함"""


class StopRun(Exception):
    """회차를 끝냄"""


@dataclass
class Puck:
    """벨트 위 퍽 1개 (건전지 1개)의 기록"""
    bid: int
    cell: list
    picked_at: str
    placed: float = field(default_factory=time.monotonic)   # 벨트에 놓인 시각 (시간 초과 감시용)
    vision: dict | None = None        # YOLO 결과 (없으면 아직 부스를 안 지남)
    plan: dict | None = None          # Mega에 보낸 계획 {"act", "to"} (없으면 아직 안 보냄)
    measure: dict | None = None       # 측정값 (MEASURED의 data)
    electrical: str = "skipped"       # measured / skipped / failed
    grade: str | None = None
    reason: str | None = None
    timed_out: bool = False


# ---------- 판정 (규약 v1.2 4장 표, 위에서부터) ----------

def judge_before_measure(vision: dict):
    """측정 전에 정해지는 경우 → (등급, 이유). 측정해야 하면 None"""
    if vision.get("status") != "ok":
        return "RESORT", "외관 검사 실패"
    if vision.get("result") != "normal":
        return "NG_VISUAL", "외관 불량"          # 거꾸로 + 외관 불량도 여기 (하드웨어 확정 11/10)
    if vision.get("polarity") == "plus_up":
        return "RESORT", "+극이 위"
    return None                                  # minus_up, unknown → 측정 (2중 확인)


def judge_measure(m: dict | None):
    """측정값으로 정함 → (등급, 이유). 사유는 통계에서 묶이도록 고정 문장만 씀"""
    pol = (m or {}).get("polarity")
    if pol is None:
        return "RESORT", "측정 실패"
    if pol == "reversed":
        return "RESORT", "측정 극성 반대"
    if pol != "normal":
        return "RESORT", "접촉 불량"
    v, r = m.get("v_open"), m.get("r_int")
    if v is None or r is None:
        return "RESORT", "측정 실패"
    if v >= GRADE_A["v_open_min"] and r <= GRADE_A["r_int_max"]:
        return "A", None
    if v >= GRADE_B["v_open_min"] and r <= GRADE_B["r_int_max"]:
        return "B", None
    low_v, high_r = v < GRADE_B["v_open_min"], r > GRADE_B["r_int_max"]
    if low_v and high_r:
        return "NG_VOLTAGE", "전압 낮음, 내부저항 큼"
    return "NG_VOLTAGE", "전압 낮음" if low_v else "내부저항 큼"


def normalize_vision(msg: dict) -> dict:
    """vision/result → server/results의 vision 형식 (메시지_형식 v1.4)"""
    if msg.get("status") != "ok":
        return dict(FAILED_VISION)
    defects = [{k: d.get(k) for k in ("type", "conf", "cam") if d.get(k) is not None}
               for d in (msg.get("defects") or []) if d.get("type")]
    result = "normal" if msg.get("result") == "normal" else "defect"
    return {"status": "ok", "result": result, "conf": msg.get("conf"),
            "polarity": msg.get("polarity"), "defects": defects if result == "defect" else []}


class LineController:
    def __init__(self):
        self.client: aiomqtt.Client | None = None
        self.waiters: dict[str, asyncio.Queue] = {}
        self.cycle: asyncio.Task | None = None
        self.mode = "idle"            # idle / running / paused / draining / estop
        self.step = "idle"
        self.message = None
        self.run_id = None
        self.boot_id = make_run_id()  # 회차 전 이벤트용 번호
        self.battery_id = 0           # 마지막으로 올린 퍽 번호
        self.belt: list[Puck] = []    # 벨트 위 퍽 줄 (올린 순서)
        self.counts = {g: 0 for g in GRADES}
        self.agv_loaded = 0
        self.agv_state = "home"
        self.agv_lock = asyncio.Lock()
        self.stop_requested = False
        self.resume_event = asyncio.Event()
        self.operator: asyncio.Future | None = None
        self.devices: dict[str, str] = {}
        self.estop_pressed: set[str] = set()
        self.estop_source = None
        self.estop_button = None
        self.last_seq = {"yolo": None, "mega": None}

    # ---------- 발행 ----------
    async def publish(self, topic, body, retain=False):
        if self.client is not None:
            with contextlib.suppress(aiomqtt.MqttError):
                await self.client.publish(topic, body, qos=1, retain=retain)

    async def event(self, level, message):
        print(f"[{now_str()}] {level:<5} {message}", flush=True)
        await self.publish("server/events", event_body(self.run_id or self.boot_id, NAME, level, message))

    async def publish_line(self):
        body = {"run_id": self.run_id, "mode": self.mode, "step": self.step,
                "battery_id": self.battery_id or None, "counts": self.counts,
                "on_belt": [p.bid for p in self.belt],
                "agv": {"enabled": USE_AGV, "loaded": self.agv_loaded, "batch": AGV_BATCH, "state": self.agv_state},
                "message": self.message, "ts": now_str()}
        await self.publish("server/line", json.dumps(body, ensure_ascii=False), retain=True)

    async def set_step(self, step, message=None):
        self.step, self.message = step, message
        await self.publish_line()

    # ---------- 명령 보내고 응답 기다리기 (규약 2장) ----------
    async def send(self, device, action, params=None, battery_id=None, timeout_key=None):
        cmd_id = new_id()
        q: asyncio.Queue = asyncio.Queue()
        self.waiters[cmd_id] = q
        body = {"cmd_id": cmd_id, "run_id": self.run_id, "battery_id": battery_id,
                "action": action, "params": params or {}, "ts": now_str()}
        try:
            await self.client.publish(f"cmd/{device}", json.dumps(body, ensure_ascii=False), qos=1)
            try:
                res = await asyncio.wait_for(q.get(), ACCEPT_TIMEOUT_SEC)
                if res["status"] == "accepted":
                    res = await asyncio.wait_for(q.get(), TIMEOUT_SEC[device][timeout_key or action])
            except asyncio.TimeoutError:
                raise CmdError("timeout", f"{device} {action} 응답 없음") from None
            if res["status"] == "done":
                return res.get("data") or {}
            raise CmdError(res["status"], f"{device} {action}: {res.get('error')}")
        finally:
            self.waiters.pop(cmd_id, None)

    async def send_quiet(self, device, action, params=None, battery_id=None, timeout_key=None):
        """실패해도 예외 대신 ERROR 이벤트만 남김 (알림 처리 중에 쓰는 명령)"""
        try:
            return await self.send(device, action, params, battery_id, timeout_key)
        except CmdError as e:
            await self.event("ERROR", f"{device} {action} 실패 ({e.kind}: {e.detail})")
            await self.pause(f"{device} {action} 실패 — 확인 후 resume")
            return None

    # ---------- 일시정지·작업자 선택 ----------
    async def pause(self, reason):
        """새 퍽을 올리지 않고 벨트를 그 자리에 멈춤. resume으로 재개"""
        if self.mode not in ("running", "draining"):
            return
        self.mode = "paused"
        self.resume_event.clear()
        await self.set_step("paused", reason)
        await self.event("WARN", f"일시정지: {reason}")
        await self.send_belt(False)

    async def send_belt(self, run: bool, drain: bool = False):
        params = {"run": run} | ({"drain": True} if drain else {})
        try:
            await self.send("mega", "belt", params, timeout_key="belt_drain" if drain else "belt")
            return True
        except CmdError as e:
            await self.event("ERROR", f"벨트 {'켜기' if run else '끄기'} 실패 ({e.kind}: {e.detail})")
            return False

    async def resume(self):
        if self.mode != "paused":
            return
        self.mode = "running"
        await self.set_step("running")
        await self.send_belt(True)
        await self.event("INFO", "재개")
        self.resume_event.set()

    async def wait_operator(self, message):
        """작업자가 resume / skip / stop 중 하나를 고를 때까지 기다림"""
        self.mode = "paused"
        self.resume_event.clear()
        await self.set_step("wait_operator", message)
        self.operator = asyncio.get_running_loop().create_future()
        try:
            choice = await self.operator
        finally:
            self.operator = None
        if choice == "stop":
            raise StopRun()
        self.mode = "running"
        await self.set_step("running")
        return choice

    # ---------- 퍽 올리기 (회차) ----------
    async def pick(self, cell):
        """로봇팔 1이 퍽을 벨트에 올림. 실패하면 작업자 선택 (다시 시도 / 건너뛰기)"""
        self.battery_id += 1
        bid = self.battery_id
        picked_at = now_str()
        while True:
            await self.set_step("pick")
            try:
                await self.send("pi", "pick", {"cell": cell}, battery_id=bid)
                break
            except CmdError as e:
                await self.event("ERROR", f"#{bid} pick 실패 ({e.kind}: {e.detail})")
                choice = await self.wait_operator("pick 실패 — resume: 다시 시도 / skip: 이 건전지 포기")
                if choice == "skip":
                    await self.event("WARN", f"#{bid} 집기를 건너뜀 (칸 {cell}, 결과 기록 안 함)")
                    return
                await self.event("INFO", f"#{bid} pick 다시 시도")
        self.belt.append(Puck(bid, cell, picked_at))
        await self.set_step("running")

    async def run_cycle(self):
        self.run_id = make_run_id()
        self.battery_id = 0
        self.belt.clear()
        self.counts = {g: 0 for g in GRADES}
        self.stop_requested = False
        self.last_seq = {"yolo": None, "mega": None}
        self.mode = "running"
        await self.event("INFO", f"회차 시작 (run_id {self.run_id}, 판정 기준 {RULE_VERSION}, 벨트 위 최대 {MAX_ON_BELT}개)")
        await self.set_step("running")
        if not await self.send_belt(True):
            self.mode = "idle"
            await self.set_step("idle", "벨트를 켜지 못해 시작하지 않음")
            return
        cells: list = []
        scanned_empty = False
        last_feed = 0.0
        try:
            while not self.stop_requested:
                if self.mode == "paused":
                    await self.resume_event.wait()
                    continue
                if len(self.belt) >= MAX_ON_BELT or time.monotonic() - last_feed < FEED_INTERVAL_SEC:
                    await asyncio.sleep(0.2)
                    continue
                if not cells:
                    await self.set_step("scan")
                    try:
                        cells = [list(c) for c in (await self.send("yolo", "scan_pallet")).get("cells") or []]
                    except CmdError as e:
                        await self.event("ERROR", f"팔레트 인식 실패 ({e.kind})")
                        await self.wait_operator("팔레트 인식 실패 — resume: 다시 시도 / stop: 회차 종료")
                        continue
                    await self.set_step("running")
                    if not cells:
                        scanned_empty = True
                        break
                await self.pick(cells.pop(0))
                last_feed = time.monotonic()
        except StopRun:
            self.stop_requested = True
        await self.event("INFO", "팔레트가 비었음 → 벨트 위 퍽을 모두 분류한 뒤 종료" if scanned_empty
                         else "회차 종료 요청 → 벨트 위 퍽을 모두 분류한 뒤 종료")
        await self.finish_run()

    async def finish_run(self):
        """새 퍽을 올리지 않고, 벨트 위 퍽을 모두 분류한 뒤 회차를 끝냄"""
        self.mode = "draining"
        await self.set_step("draining", "벨트 위 퍽 분류 중")
        await self.send_belt(False, drain=True)
        deadline = time.monotonic() + PUCK_TIMEOUT_SEC
        while self.belt and time.monotonic() < deadline:
            await asyncio.sleep(0.2)
        if self.belt:
            await self.event("ERROR", f"배출 완료 알림을 받지 못한 퍽 {[p.bid for p in self.belt]} — 벨트를 확인하세요")
            self.belt.clear()
        if self.agv_loaded > 0:
            await self.dispatch_agv("회차 종료")
        c = self.counts
        await self.event("INFO", f"회차 종료 — {sum(c.values())}개 ("
                         + ", ".join(f"{GRADE_KR[g]} {c[g]}" for g in GRADES) + ")")
        self.mode = "idle"
        await self.set_step("idle")

    # ---------- YOLO 결과 ----------
    def check_seq(self, device, seq):
        """일련번호가 1씩 늘지 않으면 빠진 알림이 있다는 뜻 → 경고 문장 반환"""
        prev = self.last_seq.get(device)
        self.last_seq[device] = seq if isinstance(seq, int) else prev
        if isinstance(seq, int) and isinstance(prev, int) and seq != prev + 1 and seq != 1:
            return f"{device} 알림 번호가 건너뜀 ({prev} → {seq})"
        return None

    async def on_vision(self, msg):
        if self.mode in ("idle", "estop"):
            return
        if warn := self.check_seq("yolo", msg.get("seq")):
            await self.event("WARN", warn)
        puck = next((p for p in self.belt if p.vision is None), None)
        if puck is None:
            await self.event("WARN", f"YOLO 결과 짝이 없음 (seq {msg.get('seq')}) — 중복 결과로 보고 버림")
            return
        puck.vision = normalize_vision(msg)
        await self.send_plan(puck)

    async def send_plan(self, puck: Puck):
        pre = judge_before_measure(puck.vision)
        if pre:
            puck.grade, puck.reason = pre
            puck.plan = {"act": "PASS", "to": DEST[puck.grade]}
        else:
            puck.plan = {"act": "MEASURE", "to": None}
        await self.send_quiet("mega", "plan", {"no": puck.bid, **puck.plan}, battery_id=puck.bid)

    # ---------- Mega 알림 ----------
    def find(self, no) -> Puck | None:
        return next((p for p in self.belt if p.bid == no), None)

    async def on_mega(self, msg):
        ev, no, data = msg.get("ev"), msg.get("no"), msg.get("data") or {}
        if warn := self.check_seq("mega", msg.get("seq")):
            await self.event("WARN", warn)
        if ev == "ENV_ALARM":
            await self.event("WARN", f"환경 경고: {data.get('sensor')} = {data.get('value')}")
            return
        if self.mode in ("idle", "estop"):
            return

        if ev == "WAIT_PLAN":
            # 계획 없이 측정 자리에 도착 = 줄에서 계획이 없는 첫 퍽
            puck = next((p for p in self.belt if p.plan is None), None)
            if puck is None:
                await self.event("ERROR", "퍽 줄에 없는 퍽이 측정 자리에 도착 (WAIT_PLAN)")
                await self.pause("알 수 없는 퍽 도착 — 벨트 확인 후 resume")
                return
            if puck.vision is not None:
                return            # YOLO 결과는 왔고 계획을 보내는 중 → 곧 도착
            puck.vision = dict(FAILED_VISION)
            await self.event("WARN", f"#{puck.bid} YOLO 결과 없이 측정 자리 도착 → 재분류")
            await self.send_plan(puck)
            return

        puck = self.find(no)
        if ev in ("MEASURED", "DIVERTED", "AT_PICK", "SORTED") and puck is None:
            await self.event("ERROR", f"{ev} 알림의 번호 #{no}가 퍽 줄에 없음")
            await self.pause("퍽 순서 불일치 — 벨트 확인 후 resume")
            return

        if ev == "MEASURED":
            earlier = [p.bid for p in self.belt if p.bid < puck.bid and p.plan is None]
            if puck.plan is None or puck.plan["act"] != "MEASURE" or earlier:
                await self.event("ERROR", f"#{puck.bid} 측정 알림이 계획과 맞지 않음 (계획 {puck.plan}, 앞 퍽 {earlier})")
                await self.pause("퍽 순서 불일치 — 벨트 확인 후 resume")
                return
            puck.measure = data
            puck.electrical = "measured" if data.get("polarity") else "failed"
            puck.grade, puck.reason = judge_measure(data)
            await self.send_quiet("mega", "route", {"no": puck.bid, "to": DEST[puck.grade]}, battery_id=puck.bid)
        elif ev == "DIVERTED":
            await self.finish_puck(puck, "RESORT_BOX")
        elif ev == "SORTED":
            await self.finish_puck(puck, data.get("to"))
        elif ev == "AT_PICK":
            pass
        elif ev == "NO_RESPONSE":
            await self.event("WARN", f"Mega가 서버 응답({data.get('waiting')})을 10초 넘게 기다리는 중"
                             + (f" (#{no})" if no else ""))
            await self.pause("Mega 응답 대기 경고 — 원인 확인 후 resume")
        elif ev == "FAULT":
            err = data.get("error") or "?"
            await self.event("ERROR", f"Mega 이상: {err}" + (f" (#{no})" if no else ""))
            if puck is not None and err in ("ARM2_FAILED", "SERVO_FAILED"):
                if puck.grade is None:
                    puck.grade, puck.reason = "RESORT", None
                puck.reason = (puck.reason + ", " if puck.reason else "") + f"배출 실패({err})"
                await self.finish_puck(puck, None)
            await self.pause(f"장치 이상 {err} — 확인 후 resume")

    async def finish_puck(self, puck: Puck, dest_actual):
        """배출 완료(또는 실패) → server/results 발행, 줄에서 뺌"""
        if puck not in self.belt:
            return
        self.belt.remove(puck)
        if puck.grade is None:              # 판정 전에 끝난 경우 (있으면 안 되지만 대비)
            puck.grade, puck.reason = "RESORT", "판정 정보 없음"
        m = puck.measure if puck.electrical == "measured" else None
        normal = bool(m) and m.get("polarity") == "normal"
        result = {
            "run_id": self.run_id, "battery_id": puck.bid, "pallet_cell": puck.cell, "picked_at": puck.picked_at,
            "vision": puck.vision or dict(FAILED_VISION),
            "electrical_status": puck.electrical,
            "polarity": m.get("polarity") if m else None,
            "v_open": m.get("v_open") if m and m.get("polarity") != "reversed" else None,
            "v_load": m.get("v_load") if normal else None,
            "r_int": m.get("r_int") if normal else None,
            "r_load": m.get("r_load") if normal else None,
            "grade": puck.grade, "reason": puck.reason, "dest": dest_actual, "finished_at": now_str(),
            "rule_version": RULE_VERSION,
        }
        await self.publish("server/results", json.dumps(result, ensure_ascii=False))
        self.counts[puck.grade] += 1
        if dest_actual == "AGV":
            self.agv_loaded += 1
        await self.publish_line()
        if dest_actual == "AGV" and self.agv_loaded >= AGV_BATCH:
            asyncio.create_task(self.dispatch_agv("적재 개수 도달"))

    async def dispatch_agv(self, why):
        async with self.agv_lock:
            n = self.agv_loaded
            if n == 0:
                return
            if not USE_AGV:
                await self.event("INFO", f"AGV 출발 조건 ({why}, A {n}개) — USE_AGV=False라 명령은 보내지 않음")
                self.agv_loaded = 0
                await self.publish_line()
                return
            await self.event("INFO", f"AGV 출발 ({why}, A {n}개)")
            self.agv_state = "going"
            await self.publish_line()
            if await self.send_quiet("agv", "go", {"count": n}) is not None:
                self.agv_loaded = 0
                self.agv_state = "returning"
                await self.publish_line()
                await self.send_quiet("agv", "return")
            self.agv_state = "home"
            await self.publish_line()

    # ---------- 감시 ----------
    async def watchdog(self):
        """퍽을 올린 뒤 PUCK_TIMEOUT_SEC 안에 배출 완료가 안 오면 ERROR + 일시정지"""
        while True:
            await asyncio.sleep(1.0)
            if self.mode not in ("running", "draining"):
                continue
            for p in list(self.belt):
                if not p.timed_out and time.monotonic() - p.placed > PUCK_TIMEOUT_SEC:
                    p.timed_out = True
                    await self.event("ERROR", f"#{p.bid} 올린 지 {PUCK_TIMEOUT_SEC:g}초가 지나도 배출 완료 알림이 없음")
                    await self.pause(f"#{p.bid} 배출 지연 — 벨트 확인 후 resume")

    # ---------- 작업자 명령 ----------
    async def on_ui(self, cmd):
        a = cmd.get("action")
        if a == "start":
            if self.mode != "idle":
                await self.event("WARN", f"start 무시 (지금 {self.mode})")
                return
            down = [d for d in WATCH_DEVICES if self.devices.get(d) not in (None, "ok")]
            if down:
                await self.event("WARN", f"start 거부: 장치 상태 이상 {down}")
                return
            self.cycle = asyncio.create_task(self.run_cycle())
        elif a == "pause":
            if self.mode in ("running", "draining"):
                await self.pause("작업자 일시정지")
        elif a in ("resume", "skip", "stop"):
            if self.operator and not self.operator.done():
                self.operator.set_result(a)
            elif a == "resume":
                await self.resume()
            elif a == "stop" and self.mode in ("running", "paused"):
                self.stop_requested = True
                if self.mode == "paused":
                    self.mode = "running"
                    self.resume_event.set()
                await self.event("INFO", "회차 종료 요청")
        elif a == "estop":
            await self.emergency_stop("WPF", None, "비상정지 (작업자 화면)")
        elif a == "reset":
            await self.reset_estop()

    async def emergency_stop(self, source, button_id, message):
        """비상정지: 회차 중단 → pi·mega 정지 → STOP_CONFIRMED"""
        if self.cycle and not self.cycle.done():
            self.cycle.cancel()
        already = self.mode == "estop"
        self.mode = "estop"
        self.estop_source = self.estop_source or source
        self.estop_button = self.estop_button or button_id
        await self.set_step("estop", "비상정지 — 버튼 복귀 후 reset으로 해제")
        await self.event("ERROR", message)
        if already:
            return
        if self.belt:
            await self.event("WARN", f"벨트 위에 있던 퍽 {[p.bid for p in self.belt]}은 결과 없이 정리됨 (작업자가 회수)")
            self.belt.clear()
        details = []
        for dev in ("pi", "mega"):
            try:
                await self.send(dev, "estop")
                details.append(f"{dev} 정지 확인")
            except CmdError as e:
                details.append(f"{dev} 정지 응답 없음 ({e.kind})")
                await self.event("ERROR", f"{dev} 정지 응답 없음 ({e.kind})")
        await self.publish("event/estop", estop_body(self.run_id, NAME, button_id, source, "STOP_CONFIRMED",
                                                     ", ".join(details)))

    async def reset_estop(self):
        if self.mode != "estop":
            return
        if self.estop_pressed:
            await self.event("WARN", f"해제 거부: 비상정지 버튼이 아직 눌려 있음 {sorted(self.estop_pressed)}")
            return
        await self.set_step("reset", "비상정지 해제 중")
        for dev in ("pi", "mega"):
            try:
                await self.send(dev, "reset")
            except CmdError as e:
                await self.event("ERROR", f"비상정지 해제 실패: {dev} ({e.kind})")
                await self.set_step("estop", "해제 실패 — 장치 확인 후 다시 reset")
                return
        await self.event("INFO", "비상정지 해제, 원점 복귀 완료 (회차 종료)")
        await self.publish("event/estop", estop_body(self.run_id, NAME, self.estop_button, self.estop_source or "OTHER",
                                                     "RESET_CONFIRMED", "원점 복귀 완료"))
        self.estop_source = self.estop_button = None
        self.mode = "idle"
        await self.set_step("idle")

    async def on_estop(self, ev):
        """event/estop: 실제 버튼 (Mega 중계가 발행)"""
        if ev.get("device") == NAME:          # 내가 보낸 확인 메시지
            return
        kind, button = ev.get("kind"), ev.get("button_id") or "?"
        if kind == "BUTTON_PRESSED":
            self.estop_pressed.add(button)
            await self.emergency_stop("PHYSICAL_BUTTON", button, f"비상정지 버튼 눌림 ({button})")
        elif kind == "BUTTON_RELEASED":
            self.estop_pressed.discard(button)
            await self.event("INFO", f"비상정지 버튼 복귀 ({button}) — reset으로 해제 가능")

    async def on_devices(self, snap):
        new = {d["name"]: d["status"] for d in snap.get("devices", [])}
        for d in WATCH_DEVICES:
            if self.mode in ("running", "draining") and self.devices.get(d) == "ok" and new.get(d) in ("warn", "offline"):
                await self.pause(f"{d} 상태 이상({new.get(d)}) — 확인 후 resume")
        self.devices = new

    async def receive(self):
        async for m in self.client.messages:
            topic = m.topic.value
            try:
                data = json.loads(to_text(m.payload))
            except ValueError:
                continue
            if not isinstance(data, dict):
                continue
            if topic.startswith("res/"):
                q = self.waiters.get(data.get("cmd_id"))
                if q:
                    q.put_nowait(data)
            elif topic == "vision/result":
                asyncio.create_task(self.on_vision(data))
            elif topic == "event/mega":
                asyncio.create_task(self.on_mega(data))
            elif topic == "ui/cmd":
                asyncio.create_task(self.on_ui(data))
            elif topic == "server/devices":
                await self.on_devices(data)
            elif topic == "event/estop":
                asyncio.create_task(self.on_estop(data))

    async def run_forever(self):
        print(f"[{now_str()}] 라인 컨트롤러 시작 (규약 v1.2, 판정 기준 {RULE_VERSION}, "
              f"벨트 위 최대 {MAX_ON_BELT}개, AGV {'사용' if USE_AGV else '미사용'})", flush=True)
        status_topic = f"status/{NAME}"
        while True:
            will = aiomqtt.Will(status_topic, payload="offline", qos=1, retain=True)
            try:
                async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier=NAME, keepalive=5, will=will) as c:
                    self.client = c
                    await c.publish(status_topic, "online", qos=1, retain=True)
                    for t in ("res/+", "ui/cmd", "server/devices", "event/estop", "vision/result", "event/mega"):
                        await c.subscribe(t, qos=1)
                    self.mode, self.step = "idle", "idle"
                    await self.publish_line()
                    print(f"[{now_str()}] 브로커 연결됨 ({BROKER_HOST}:{BROKER_PORT}) — 관리자 프로그램 시작 버튼 또는 line_cmd.py start", flush=True)
                    try:
                        async with asyncio.TaskGroup() as tg:
                            tg.create_task(self.receive())
                            tg.create_task(heartbeat_loop(c, NAME))
                            tg.create_task(self.watchdog())
                    finally:
                        if self.cycle and not self.cycle.done():
                            self.cycle.cancel()
                        with contextlib.suppress(Exception):
                            await asyncio.wait_for(c.publish(status_topic, "offline", qos=1, retain=True), 1.0)
            except* aiomqtt.MqttError as eg:
                print(f"[{now_str()}] 브로커 연결 실패/끊김: {eg.exceptions[0]} → 3초 뒤 재시도", flush=True)
            self.client = None
            self.mode = "idle"
            self.belt.clear()
            await asyncio.sleep(3)


if __name__ == "__main__":
    run_main(lambda: LineController().run_forever())
