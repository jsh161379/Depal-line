"""
라인 컨트롤러 - 건전지 1개씩 순서대로 지휘 (통신 규약 v0.9.3, 4장)
=================================================================
하는 일
  ui/cmd 로 start를 받으면 회차(run_id)를 새로 만들고, 팔레트가 빌 때까지 아래를 반복한다.
    scan_pallet → pick → move(inspect) → inspect → [판정 1~2] → move(measure) → measure
    → move(home) → [판정 3~6] → sort(배출 완료 알림) → server/results 발행
  - 물리 동작(pick, move, sort) 실패·시간 초과 → 일시정지, 작업자가 resume(다시 시도) / skip(이 건전지 포기)
  - 외관 검사 실패 → 1번 자동 재시도, 또 실패면 재분류
  - 전압 측정 실패 → 재분류
  - 감시 중인 장치(pi, yolo)가 끊기면 지금 건전지를 끝낸 뒤 일시정지
  - 현재 상태를 server/line (retained)으로, 주요 사건을 server/events로 발행
  - 실제 비상정지 버튼(event/estop BUTTON_PRESSED)이나 작업자 화면 estop → 즉시 비상정지, STOP_CONFIRMED 발행
    버튼이 눌린 채면 reset 거부, 해제 완료 시 RESET_CONFIRMED 발행

어디서 실행하나 (팀 결정 전, 두 가지 모두 가능)
  A. 서버 PC에서 실행 → "서버가 지휘"
  B. 라즈베리파이에서 실행 → "서버는 기록만" (BROKER_HOST만 서버 PC IP로 바꿈)

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
실행: python line_controller.py      (명령은 python line_cmd.py start / pause / resume / skip / stop / estop / reset)
"""

# ===================== 설정값 (여기만 바꾸면 됨) =====================
BROKER_HOST = "127.0.0.1"            # B안(라파에서 실행)이면 서버 PC의 IP
BROKER_PORT = 1883
NAME = "controller"

PALLET_ROWS, PALLET_COLS = 2, 5       # 팔레트 2×5

ACCEPT_TIMEOUT_SEC = 2.0              # accepted 응답까지
TIMEOUT_SEC = {                       # done/failed 응답까지 (임시값)
    "pi":   {"pick": 20, "move": 15, "measure": 5, "sort": 20, "estop": 3, "reset": 30},
    "yolo": {"scan_pallet": 5, "inspect": 5},
    "agv":  {"go": 120, "return": 120},
}
VISION_RETRY = 1                      # 외관 검사 자동 재시도 횟수

# 등급 판정 기준 (임시값, 전압 시험 데이터로 확정 예정) — 바꾸면 RULE_VERSION도 바꿀 것
RULE_VERSION = "temp-2026-11-06"
GRADE_A = {"v_open_min": 1.50, "r_int_max": 0.35}
GRADE_B = {"v_open_min": 1.30, "r_int_max": 1.0}

USE_AGV = False                       # AGV가 준비되면 True (False면 출발 조건만 이벤트로 알림)
AGV_BATCH = 8                         # A 몇 개마다 출발 (잠정, 지게 구현 후 확정)

WATCH_DEVICES = ["pi", "yolo"]        # 끊기면 일시정지할 장치
# ====================================================================

import asyncio
import contextlib
import json
import time

import aiomqtt

from line_common import event_body, estop_body, make_run_id, new_id, now_str, run_main, heartbeat_loop, to_text

DEST = {"A": "AGV", "B": "B_BOX", "NG": "NG_BOX", "RESORT": "RESORT_BOX"}
DEST_KR = {"AGV": "AGV", "B_BOX": "B함", "NG_BOX": "불량함", "RESORT_BOX": "재분류함"}
DEFECT_KR = {"scratch": "스크래치", "sticker": "스티커", "torn": "찢김", "rust": "녹", "dent": "찌그러짐"}


class CmdError(Exception):
    def __init__(self, kind: str, detail: str):
        super().__init__(f"{kind}: {detail}")
        self.kind, self.detail = kind, detail


class SkipBattery(Exception):
    """작업자가 이 건전지를 건너뛰기로 함"""


class StopRun(Exception):
    """회차를 끝냄 (stop 또는 팔레트가 빔)"""


# ---------- 판정 (규약 4장 표, 위에서부터) ----------

def judge_before_measure(vision: dict):
    """측정 전에 정해지는 경우. 정해지면 (등급, 이유), 아니면 None"""
    if vision["status"] == "failed":
        return "RESORT", "외관 검사 실패"
    if vision["result"] != "normal":
        return "NG", f"외관 불량({DEFECT_KR.get(vision['result'], vision['result'])})"
    if vision.get("polarity") == "plus_up":
        return "RESORT", "+극이 위 (상단 카메라)"
    return None


def judge_measure(m: dict | None):
    """측정 결과로 정함. m=None이면 측정 실패"""
    if m is None:
        return "RESORT", "전압 측정 실패"
    pol = m.get("polarity")
    if pol == "reversed":
        return "RESORT", "측정 극성 반대"
    if pol != "normal":
        return "RESORT", "측정값 이상(접촉 불량 등)"
    v, r = m.get("v_open"), m.get("r_int")
    if v is None or r is None:
        return "RESORT", "측정값 누락"
    if v >= GRADE_A["v_open_min"] and r <= GRADE_A["r_int_max"]:
        return "A", None
    if v >= GRADE_B["v_open_min"] and r <= GRADE_B["r_int_max"]:
        return "B", None
    return "NG", f"전압 {v:.3f}V / 내부저항 {r:.3f}Ω 기준 미달"


class LineController:
    def __init__(self):
        self.client: aiomqtt.Client | None = None
        self.waiters: dict[str, asyncio.Queue] = {}
        self.cycle: asyncio.Task | None = None
        self.mode = "idle"            # idle / running / paused / estop
        self.step = "idle"
        self.message = None
        self.run_id = None
        self.boot_id = make_run_id()      # 회차 전 이벤트용 번호 (DB run_id 형식을 맞추기 위함)
        self.battery_id = 0
        self.counts = {"A": 0, "B": 0, "NG": 0, "RESORT": 0}
        self.agv_loaded = 0
        self.agv_state = "home"
        self.pause_requested = False
        self.stop_requested = False
        self.operator: asyncio.Future | None = None   # 일시정지 중 작업자 선택을 기다림
        self.devices: dict[str, str] = {}             # server/devices 로 받은 장치 상태
        self.estop_pressed: set[str] = set()          # 눌린 채로 있는 실제 비상정지 버튼
        self.estop_source = None                      # 지금 비상정지의 원인 (PHYSICAL_BUTTON / WPF)
        self.estop_button = None                      # 지금 비상정지를 건 버튼 이름

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
                "agv": {"enabled": USE_AGV, "loaded": self.agv_loaded, "batch": AGV_BATCH, "state": self.agv_state},
                "message": self.message, "ts": now_str()}
        await self.publish("server/line", json.dumps(body, ensure_ascii=False), retain=True)

    async def set_step(self, step, message=None):
        self.step, self.message = step, message
        await self.publish_line()

    # ---------- 명령 보내고 응답 기다리기 (규약 2장) ----------
    async def send(self, device, action, params=None):
        cmd_id = new_id()
        q: asyncio.Queue = asyncio.Queue()
        self.waiters[cmd_id] = q
        body = {"cmd_id": cmd_id, "run_id": self.run_id, "battery_id": self.battery_id or None,
                "action": action, "params": params or {}, "ts": now_str()}
        try:
            await self.client.publish(f"cmd/{device}", json.dumps(body, ensure_ascii=False), qos=1)
            try:
                res = await asyncio.wait_for(q.get(), ACCEPT_TIMEOUT_SEC)
                if res["status"] == "accepted":
                    res = await asyncio.wait_for(q.get(), TIMEOUT_SEC[device][action])
            except asyncio.TimeoutError:
                raise CmdError("timeout", f"{device} {action} 응답 없음") from None
            if res["status"] == "done":
                return res.get("data") or {}
            raise CmdError(res["status"], f"{device} {action}: {res.get('error')}")
        finally:
            self.waiters.pop(cmd_id, None)

    async def physical(self, device, action, params, step):
        """물리 동작: 실패하면 일시정지하고 작업자 선택을 기다림 (자동 재시도 안 함)"""
        while True:
            await self.set_step(step)
            try:
                return await self.send(device, action, params)
            except CmdError as e:
                await self.event("ERROR", f"#{self.battery_id} {action} 실패 ({e.kind}: {e.detail})")
                choice = await self.wait_operator(f"{action} 실패 — resume: 다시 시도 / skip: 이 건전지 포기")
                if choice == "skip":
                    raise SkipBattery(action) from None
                await self.event("INFO", f"#{self.battery_id} {action} 다시 시도")

    async def wait_operator(self, message):
        self.mode = "paused"
        await self.set_step("wait_operator", message)
        self.operator = asyncio.get_running_loop().create_future()
        try:
            choice = await self.operator
        finally:
            self.operator = None
        if choice == "stop":
            raise StopRun()
        self.mode = "running"
        return choice

    # ---------- 건전지 1개 ----------
    async def inspect(self):
        for attempt in range(VISION_RETRY + 1):
            try:
                d = await self.send("yolo", "inspect")
                return {"status": "ok", "result": d.get("result"), "conf": d.get("conf"),
                        "polarity": d.get("polarity")}
            except CmdError as e:
                await self.event("WARN", f"#{self.battery_id} 외관 검사 실패 ({e.kind})"
                                 + (", 다시 시도" if attempt < VISION_RETRY else ""))
        return {"status": "failed", "result": None, "conf": None, "polarity": None}

    async def process(self, cell):
        self.battery_id += 1
        bid = self.battery_id
        picked_at = now_str()
        vision = {"status": "failed", "result": None, "conf": None, "polarity": None}
        m, electrical = None, "skipped"
        grade = reason = None
        dest_actual = None
        try:
            await self.physical("pi", "pick", {"cell": cell}, "pick")
        except SkipBattery:
            await self.event("WARN", f"#{bid} 집기를 건너뜀 (칸 {cell}, 결과 기록 안 함)")
            return
        try:
            await self.physical("pi", "move", {"to": "inspect"}, "inspect")
            await self.set_step("inspect")
            vision = await self.inspect()
            pre = judge_before_measure(vision)
            if pre:
                grade, reason = pre
            else:
                await self.physical("pi", "move", {"to": "measure"}, "measure")
                await self.set_step("measure")
                try:
                    m = await self.send("pi", "measure")
                    electrical = "measured"
                except CmdError as e:
                    electrical = "failed"
                    await self.event("WARN", f"#{bid} 전압 측정 실패 ({e.kind})")
                grade, reason = judge_measure(m)
            await self.physical("pi", "move", {"to": "home"}, "home")
            dest = DEST[grade]
            if dest == "AGV" and USE_AGV and self.agv_state != "home":
                await self.set_step("wait_agv", "AGV가 돌아올 때까지 대기")
                while self.agv_state != "home":
                    await asyncio.sleep(0.5)
            r = await self.physical("pi", "sort", {"dest": dest}, "sort")
            dest_actual = r.get("dest", dest)
        except SkipBattery as s:
            if grade is None:
                grade = "RESORT"
            reason = (reason + ", " if reason else "") + f"작업자가 {s} 단계에서 건너뜀"
            dest_actual = None

        measured_normal = electrical == "measured" and m and m.get("polarity") == "normal"
        result = {
            "run_id": self.run_id, "battery_id": bid, "pallet_cell": cell, "picked_at": picked_at,
            "vision": vision, "electrical_status": electrical,
            "polarity": m.get("polarity") if electrical == "measured" and m else None,
            "v_open": m.get("v_open") if electrical == "measured" and m and m.get("polarity") != "reversed" else None,
            "v_load": m.get("v_load") if measured_normal else None,
            "r_int": m.get("r_int") if measured_normal else None,
            "grade": grade, "reason": reason, "dest": dest_actual, "finished_at": now_str(),
            # v1.2 후보 (DB 저장 프로그램은 모르는 칸을 무시함)
            "rule_version": RULE_VERSION,
            "r_load": m.get("r_load") if measured_normal else None,
        }
        await self.publish("server/results", json.dumps(result, ensure_ascii=False))
        self.counts[grade] += 1
        if dest_actual == "AGV":
            self.agv_loaded += 1
        await self.publish_line()                 # 등급 개수와 AGV 적재 수를 올린 뒤에 화면에 알림
        if dest_actual == "AGV":
            if self.agv_loaded >= AGV_BATCH:
                try:
                    await self.dispatch_agv("적재 개수 도달")
                except SkipBattery:
                    await self.event("WARN", "AGV 운송을 작업자가 건너뜀")

    async def dispatch_agv(self, why):
        n = self.agv_loaded
        if not USE_AGV:
            await self.event("INFO", f"AGV 출발 조건 ({why}, A {n}개) — USE_AGV=False라 명령은 보내지 않음")
            self.agv_loaded = 0
            await self.publish_line()
            return
        await self.event("INFO", f"AGV 출발 ({why}, A {n}개)")
        self.agv_state = "going"
        await self.physical("agv", "go", {"count": n}, "wait_agv")
        self.agv_loaded = 0
        self.agv_state = "returning"
        await self.physical("agv", "return", {}, "wait_agv")
        self.agv_state = "home"
        await self.publish_line()

    # ---------- 회차 ----------
    async def run_cycle(self):
        self.run_id = make_run_id()
        self.battery_id = 0
        self.counts = {k: 0 for k in self.counts}
        self.pause_requested = self.stop_requested = False
        self.mode = "running"
        await self.event("INFO", f"회차 시작 (run_id {self.run_id}, 판정 기준 {RULE_VERSION})")
        try:
            while True:
                await self.set_step("scan")
                try:
                    cells = (await self.send("yolo", "scan_pallet")).get("cells") or []
                except CmdError as e:
                    await self.event("ERROR", f"팔레트 인식 실패 ({e.kind})")
                    await self.wait_operator("팔레트 인식 실패 — resume: 다시 시도 / stop: 회차 종료")
                    continue
                if not cells:
                    await self.event("INFO", "팔레트가 비었음")
                    break
                for cell in cells:
                    await self.check_pause()
                    await self.process(list(cell))
                await self.check_pause()
        except StopRun:
            await self.event("INFO", "작업자 요청으로 회차를 끝냄")
        if self.agv_loaded > 0:
            with contextlib.suppress(SkipBattery, StopRun):
                await self.dispatch_agv("회차 종료")
        c = self.counts
        await self.event("INFO", f"회차 종료 — {sum(c.values())}개 (A {c['A']}, B {c['B']}, NG {c['NG']}, 재분류 {c['RESORT']})")
        self.mode = "idle"
        await self.set_step("idle")

    async def check_pause(self):
        """건전지 사이에서 일시정지·종료 요청 처리"""
        if self.stop_requested:
            raise StopRun()
        if self.pause_requested:
            self.pause_requested = False
            await self.wait_operator(self.message or "일시정지 — resume: 재개 / stop: 회차 종료")

    # ---------- 받은 메시지 ----------
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
            if self.mode == "running":
                self.pause_requested = True
                self.message = "일시정지 요청 — 지금 건전지를 끝낸 뒤 멈춤"
                await self.event("INFO", self.message)
        elif a in ("resume", "skip", "stop"):
            if self.operator and not self.operator.done():
                self.operator.set_result(a)
            elif a == "stop" and self.mode == "running":
                self.stop_requested = True
                await self.event("INFO", "회차 종료 요청 — 지금 건전지를 끝낸 뒤 종료")
        elif a == "estop":
            await self.emergency_stop("WPF", None, "비상정지 (작업자 화면)")
        elif a == "reset":
            if self.mode != "estop":
                return
            if self.estop_pressed:
                await self.event("WARN", f"해제 거부: 비상정지 버튼이 아직 눌려 있음 {sorted(self.estop_pressed)}")
                return
            await self.set_step("reset", "비상정지 해제 중")
            try:
                await self.send("pi", "reset")
                await self.event("INFO", "비상정지 해제, 원점 복귀 완료 (회차 종료)")
            except CmdError as e:
                await self.event("ERROR", f"비상정지 해제 실패 ({e.kind})")
                return
            await self.publish("event/estop", estop_body(self.run_id, NAME, self.estop_button, self.estop_source or "OTHER",
                                                         "RESET_CONFIRMED", "원점 복귀 완료"))
            self.estop_source = self.estop_button = None
            self.mode = "idle"
            await self.set_step("idle")

    async def emergency_stop(self, source, button_id, message):
        """비상정지: 회차 중단 → 라파 정지 명령 → STOP_CONFIRMED 발행"""
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
        try:
            await self.send("pi", "estop")
            detail = "라파 정지 확인"
        except CmdError as e:
            detail = f"라파 정지 응답 없음 ({e.kind})"
            await self.event("ERROR", detail)
        await self.publish("event/estop", estop_body(self.run_id, NAME, button_id, source, "STOP_CONFIRMED", detail))

    async def on_estop(self, ev):
        """event/estop: 실제 버튼(라파가 발행)"""
        kind, button = ev.get("kind"), ev.get("button_id") or "?"
        if ev.get("device") == NAME:          # 내가 보낸 확인 메시지
            return
        if kind == "BUTTON_PRESSED":
            self.estop_pressed.add(button)
            await self.emergency_stop("PHYSICAL_BUTTON", button, f"비상정지 버튼 눌림 ({button})")
        elif kind == "BUTTON_RELEASED":
            self.estop_pressed.discard(button)
            await self.event("INFO", f"비상정지 버튼 복귀 ({button}) — reset으로 해제 가능")

    async def on_devices(self, snap):
        new = {d["name"]: d["status"] for d in snap.get("devices", [])}
        for d in WATCH_DEVICES:
            if self.mode == "running" and self.devices.get(d) == "ok" and new.get(d) in ("warn", "offline"):
                self.pause_requested = True
                self.message = f"{d} 상태 이상({new.get(d)}) — 확인 후 resume"
                await self.event("WARN", f"{d} 상태 이상 → 지금 건전지 뒤 일시정지")
        self.devices = new

    async def receive(self):
        async for m in self.client.messages:
            topic = m.topic.value
            try:
                data = json.loads(to_text(m.payload))
            except ValueError:
                continue
            if topic.startswith("res/"):
                q = self.waiters.get(data.get("cmd_id"))
                if q:
                    q.put_nowait(data)
            elif topic == "ui/cmd":
                asyncio.create_task(self.on_ui(data))
            elif topic == "server/devices":
                await self.on_devices(data)
            elif topic == "event/estop":
                asyncio.create_task(self.on_estop(data))

    async def run_forever(self):
        print(f"[{now_str()}] 라인 컨트롤러 시작 (판정 기준 {RULE_VERSION}, AGV {'사용' if USE_AGV else '미사용'})", flush=True)
        status_topic = f"status/{NAME}"
        while True:
            will = aiomqtt.Will(status_topic, payload="offline", qos=1, retain=True)
            try:
                async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier=NAME, keepalive=5, will=will) as c:
                    self.client = c
                    await c.publish(status_topic, "online", qos=1, retain=True)
                    for t in ("res/+", "ui/cmd", "server/devices", "event/estop"):
                        await c.subscribe(t, qos=1)
                    self.mode, self.step = "idle", "idle"
                    await self.publish_line()
                    print(f"[{now_str()}] 브로커 연결됨 ({BROKER_HOST}:{BROKER_PORT}) — python line_cmd.py start 로 시작", flush=True)
                    try:
                        async with asyncio.TaskGroup() as tg:
                            tg.create_task(self.receive())
                            tg.create_task(heartbeat_loop(c, NAME))
                    finally:
                        if self.cycle and not self.cycle.done():
                            self.cycle.cancel()
                        with contextlib.suppress(Exception):
                            await asyncio.wait_for(c.publish(status_topic, "offline", qos=1, retain=True), 1.0)
            except* aiomqtt.MqttError as eg:
                print(f"[{now_str()}] 브로커 연결 실패/끊김: {eg.exceptions[0]} → 3초 뒤 재시도", flush=True)
            self.client = None
            self.mode = "idle"
            await asyncio.sleep(3)


if __name__ == "__main__":
    run_main(lambda: LineController().run_forever())
