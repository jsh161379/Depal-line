"""
가짜 Mega (중계 포함) - 라인 컨트롤러 시험용 (통신 규약 v1.2, 3-1-1 · 3-4)
=========================================================================
실제로는 서버 PC의 중계 프로그램이 Arduino Mega와 USB 시리얼로 이야기하고, MQTT로는 아래처럼 보인다.
이 파일은 그 "MQTT에서 보이는 모습"을 흉내 낸다. 하드웨어 담당은 "중계가 이렇게 답하면 된다"는 예제로 보면 됨.

  cmd/mega  plan {no, act, to}     계획 줄 맨 뒤에 넣음 (바로 done)
            route {no, to}         측정을 마친 퍽을 보낼 곳 (바로 done)
            belt {run}             켜기 / 그 자리 정지 (바로 done)
            belt {run:false, drain:true}  벨트 위 퍽을 모두 분류한 뒤 정지 (끝나면 done)
            estop / reset
  event/mega  WAIT_PLAN · MEASURED · DIVERTED · AT_PICK · SORTED · NO_RESPONSE · FAULT · ENV_ALARM
  hb/mega   {serial, belt, queue}  1초마다
  event/estop  비상정지 버튼 (--estop-at)
  sensor/env   MQ-9 가스(원시값), DHT11 온도·습도 (5초마다)

벨트 흉내
  - 로봇팔 1이 퍽을 올리면(cmd/arm1 pick → res/arm1 done) 벨트 시작점에 퍽이 생김. 퍽에는 번호가 없음 (실제와 같음)
  - 측정 자리에 오면 계획 줄 맨 앞 계획을 가져감. 계획이 없으면 벨트를 멈추고 WAIT_PLAN
  - MEASURE: 벨트를 멈추고 측정 → MEASURED → route를 기다림 (접촉 불량·측정 실패면 1번 다시 측정)
  - 재분류함(RESORT_BOX)으로 갈 퍽은 측정 자리 바로 뒤 서보가 쳐냄 → DIVERTED
  - 나머지는 로봇팔 2 자리까지 가서 AT_PICK → 팔 2가 함에 넣음 → SORTED
  - 서버 응답(plan·route)이 10초 넘게 없으면 NO_RESPONSE (계속 기다림)
  - 서버 하트비트가 끊기면 벨트 정지

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
사용법
  python fake_mega.py                       기본 (--speed 0.3 = 3배 빠르게)
  python fake_mega.py --speed 1             실제 속도
  python fake_mega.py --reversed 0.1        측정 시 극성 반대 확률
  python fake_mega.py --abnormal 0.05       측정 시 접촉 불량 확률 (1번 다시 측정)
  python fake_mega.py --arm2-fail 0.1       로봇팔 2 실패(FAULT ARM2_FAILED) 확률
  python fake_mega.py --estop-at 40         40초 뒤 비상정지 버튼 누름, 5초 뒤 복귀 (--estop-hold)
  python fake_mega.py --gas-alarm-at 30     30초 뒤 가스 경고(ENV_ALARM) 한 번
  python fake_mega.py --seed 1              매번 같은 결과
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
NAME = "mega"
R_LOAD = 3.31                 # 부하 저항 실측값(Ω)이라고 가정
ESTOP_BUTTON_ID = "estop_1"
# 벨트 위치(초, --speed 1 기준으로 벨트가 움직인 시간) 🟡 실측 전 임시값
POS_MEASURE = 6.0             # 올린 곳 → 측정 자리
POS_DIVERT = 6.5              # 측정 자리 → 서보 (재분류함으로 쳐냄)
POS_PICK = 14.0               # 측정 자리 → 로봇팔 2 자리 (약 8초)
TIME_SEC = {"measure": 4.5, "arm2": 5.0, "reset": 2.0}   # --speed 1 기준
NO_RESPONSE_SEC = 10.0        # 서버 응답을 이만큼 기다리면 NO_RESPONSE (속도 배율 적용 안 함)
SERVER_TIMEOUT_SEC = 3.0      # 서버 하트비트가 이만큼 없으면 벨트 정지
ENV_INTERVAL_SEC = 0.0          # 규약 v1.5부터 환경 센서는 Mega가 아니라 환경 Uno(env_relay.py --fake) → 기본은 끔. 옛 방식 시험만 --env-interval 5
GAS_ALARM_RAW = 400           # MQ-9 원시값 경고 기준 🟡
# 환경 센서 (센서, 측정 종류, 단위, 기준값, 흔들림). DHT11은 1도·1% 단위
ENV_SENSORS = [("gas_1", "GAS", "raw", 180.0, 20.0),
               ("temp_hum_1", "TEMPERATURE", "C", 24.0, 1.0),
               ("temp_hum_1", "HUMIDITY", "%", 40.0, 2.0)]
TICK = 0.05
# =================================================

import argparse
import asyncio
import contextlib
import json
import random
import time

import aiomqtt

from line_common import env_body, estop_body, heartbeat_loop, now_str, run_main, to_text


def log(s):
    print(f"[{now_str()[11:19]}] [mega] {s}", flush=True)


class FakeMega:
    def __init__(self, args):
        self.a = args
        self.rnd = random.Random(args.seed)
        self.client: aiomqtt.Client | None = None
        self.run_id = None
        self.seq = 0
        self.plans: list[dict] = []          # 계획 줄 [{"no","act","to"}]
        self.pucks: list[dict] = []          # 벨트 위 퍽 [{"pos","stage","no","to"}] (올린 순서)
        self.belt_cmd = False                # 서버가 켜라고 했는지
        self.hold = False                    # 측정 자리에서 벨트를 멈춤 (계획·측정·route 대기)
        self.estop = False
        self.server_ok = True
        self.drain: asyncio.Future | None = None
        self.route_wait: dict | None = None  # {"no", "fut"}
        self.plan_arrived = asyncio.Event()
        self.pick_cmds: dict[str, int] = {}  # cmd/arm1 pick의 cmd_id → battery_id (로봇팔 1이 올리는 순간을 알기 위함)
        self.arm2_busy = False

    # ---------- 발행 ----------
    async def pub(self, topic, body, qos=1):
        if self.client is not None:
            with contextlib.suppress(aiomqtt.MqttError):
                await self.client.publish(topic, body, qos=qos)

    async def notify(self, ev, no=None, data=None):
        """event/mega 알림. seq는 Mega 자체 번호, ts는 중계가 붙임"""
        self.seq += 1
        await self.pub("event/mega", json.dumps({"ev": ev, "no": no, "seq": self.seq, "data": data or {},
                                                 "ts": now_str()}, ensure_ascii=False))
        log(f"→ {ev}" + (f" #{no}" if no is not None else "") + (f" {data}" if data else ""))

    async def reply(self, cmd_id, status, data=None, error=None):
        await self.pub(f"res/{NAME}", json.dumps({"cmd_id": cmd_id, "status": status, "data": data or {},
                                                  "error": error, "ts": now_str()}, ensure_ascii=False))

    def moving(self):
        return self.belt_cmd and not self.hold and not self.estop and self.server_ok

    def info(self):
        return {"serial": "ok", "belt": "run" if self.moving() else "stop", "queue": len(self.plans)}

    # ---------- 측정값 ----------
    def sample_battery(self):
        # 판정 기준 temp-2026-11-20 (A ≤ 1.2Ω, B ≤ 2.0Ω, 개방전압 먼저)에서 new→A, used→B, worn→내부저항 큼, dead→전압 낮음
        kind = self.rnd.choices(["new", "used", "worn", "dead"], weights=[5, 3, 1, 1])[0]
        if kind == "new":
            return self.rnd.uniform(1.55, 1.62), self.rnd.uniform(0.12, 0.30)
        if kind == "used":
            return self.rnd.uniform(1.33, 1.48), self.rnd.uniform(0.40, 1.80)
        if kind == "worn":
            return self.rnd.uniform(1.33, 1.48), self.rnd.uniform(2.1, 3.0)
        return self.rnd.uniform(1.00, 1.28), self.rnd.uniform(1.2, 3.0)

    def measure_once(self):
        x = self.rnd.random()
        if x < self.a.adc_fail:
            return {"polarity": None, "error": "ADC_FAILED"}
        x -= self.a.adc_fail
        if x < self.a.reversed:        # 극성 반대: 보호 다이오드 때문에 전압 못 읽음, 부하 안 걺
            return {"polarity": "reversed", "v_open": None, "v_load": None, "r_load": R_LOAD, "r_int": None}
        if x < self.a.reversed + self.a.abnormal:
            return {"polarity": "abnormal", "v_open": round(self.rnd.uniform(-0.05, 0.05), 4),
                    "v_load": None, "r_load": R_LOAD, "r_int": None}
        v, r = self.sample_battery()
        return {"polarity": "normal", "v_open": round(v, 4), "v_load": round(v * R_LOAD / (R_LOAD + r), 4),
                "r_load": R_LOAD, "r_int": round(r, 3)}

    async def sleep_scaled(self, key):
        await asyncio.sleep(TIME_SEC[key] * self.a.speed * self.rnd.uniform(0.9, 1.1))

    # ---------- 측정 자리 ----------
    async def wait_server(self, what, cond, no=None):
        """서버 응답을 기다림. 10초가 넘으면 NO_RESPONSE를 한 번 보내고 계속 기다림"""
        t0, warned = time.monotonic(), False
        while not cond():
            if self.estop:
                raise asyncio.CancelledError
            if not warned and time.monotonic() - t0 > NO_RESPONSE_SEC:
                warned = True
                await self.notify("NO_RESPONSE", no, {"waiting": what})
            self.plan_arrived.clear()
            with contextlib.suppress(asyncio.TimeoutError):
                await asyncio.wait_for(self.plan_arrived.wait(), 0.2)

    async def at_station(self, p):
        """퍽이 측정 자리(TCRT5000 센서)에 도착"""
        self.hold = True
        try:
            if not self.plans:
                await self.notify("WAIT_PLAN")
                await self.wait_server("plan", lambda: bool(self.plans))
            plan = self.plans.pop(0)
            p["no"] = plan.get("no")
            if plan.get("act") == "PASS":
                p["to"] = plan.get("to") or "RESORT_BOX"
                log(f"#{p['no']} 통과 (→ {p['to']})")
            else:
                await self.sleep_scaled("measure")          # 헤드 내림 → 측정
                m = self.measure_once()
                if m.get("polarity") in (None, "abnormal"):
                    log(f"#{p['no']} {m.get('polarity') or m.get('error')} → 1번 다시 측정")
                    await self.sleep_scaled("measure")
                    m = self.measure_once()
                self.route_wait = {"no": p["no"], "to": None}
                await self.notify("MEASURED", p["no"], m)
                await self.wait_server("route", lambda: self.route_wait["to"] is not None, p["no"])
                p["to"] = self.route_wait["to"]
                self.route_wait = None
            p["stage"] = "after"
            p["pos"] = POS_MEASURE + 0.01
        finally:
            self.hold = False
            self.route_wait = None

    async def arm2(self, p):
        """로봇팔 2 (MYLOOP + 전자석): 퍽에서 건전지를 집어 함에 넣음"""
        self.arm2_busy = True
        try:
            await self.sleep_scaled("arm2")
            if self.estop or p not in self.pucks:
                return
            self.pucks.remove(p)
            if self.rnd.random() < self.a.arm2_fail:
                await self.notify("FAULT", p["no"], {"error": "ARM2_FAILED"})
            else:
                await self.notify("SORTED", p["no"], {"to": p["to"]})
        finally:
            self.arm2_busy = False

    async def belt_loop(self):
        station_task = None
        while True:
            await asyncio.sleep(TICK)
            if self.estop:
                continue
            if self.moving():
                step = TICK / self.a.speed
                ahead_limit = None
                for p in self.pucks:                   # 앞에서부터 (앞 퍽을 넘어가지 않음)
                    limit = POS_MEASURE if p["stage"] == "before" else POS_PICK
                    if ahead_limit is not None:
                        limit = min(limit, ahead_limit - 0.5)
                    p["pos"] = min(p["pos"] + step, max(p["pos"], limit))
                    ahead_limit = p["pos"]
            for p in list(self.pucks):
                if p["stage"] == "before" and p["pos"] >= POS_MEASURE and (station_task is None or station_task.done()):
                    p["stage"] = "station"
                    station_task = asyncio.create_task(self.at_station(p))
                elif p["stage"] == "after" and p["to"] == "RESORT_BOX" and p["pos"] >= POS_DIVERT:
                    self.pucks.remove(p)
                    await self.notify("DIVERTED", p["no"])
                elif p["stage"] == "after" and p["pos"] >= POS_PICK:
                    p["stage"] = "pick"
                    await self.notify("AT_PICK", p["no"])
                if p["stage"] == "pick" and not self.arm2_busy and p in self.pucks:
                    asyncio.create_task(self.arm2(p))
            if self.drain and not self.pucks and not self.drain.done():
                self.belt_cmd = False
                self.drain.set_result(True)

    # ---------- 명령 ----------
    async def on_cmd(self, cmd):
        cmd_id, action, prm = cmd.get("cmd_id"), cmd.get("action"), cmd.get("params") or {}
        if cmd.get("run_id"):
            self.run_id = cmd["run_id"]
        if self.estop and action not in ("estop", "reset"):
            await self.reply(cmd_id, "rejected", error="estop")
            return
        await self.reply(cmd_id, "accepted")
        if action == "plan":
            self.plans.append({"no": prm.get("no"), "act": prm.get("act"), "to": prm.get("to")})
            self.plan_arrived.set()
            log(f"plan #{prm.get('no')} {prm.get('act')} {prm.get('to') or ''} (줄 {len(self.plans)}개)")
            await self.reply(cmd_id, "done")
        elif action == "route":
            rw = self.route_wait
            if rw is None or rw["no"] != prm.get("no"):
                await self.reply(cmd_id, "failed", error="no_puck_waiting_route")
                return
            rw["to"] = prm.get("to")
            self.plan_arrived.set()
            log(f"route #{prm.get('no')} → {prm.get('to')}")
            await self.reply(cmd_id, "done")
        elif action == "belt":
            if prm.get("run"):
                self.belt_cmd = True
                log("벨트 켜기")
                await self.reply(cmd_id, "done")
            elif prm.get("drain"):
                log(f"벨트 종료: 위의 퍽 {len(self.pucks)}개를 모두 분류한 뒤 정지")
                self.belt_cmd = True
                self.drain = asyncio.get_running_loop().create_future()
                try:
                    await self.drain
                finally:
                    self.drain = None
                log("벨트 정지 (분류 끝)")
                await self.reply(cmd_id, "done")
            else:
                self.belt_cmd = False
                log("벨트 일시정지 (그 자리)")
                await self.reply(cmd_id, "done")
        elif action == "estop":
            self.do_estop()
            await self.reply(cmd_id, "done")
        elif action == "reset":
            await asyncio.sleep(TIME_SEC["reset"] * self.a.speed)
            self.plans.clear()
            self.pucks.clear()
            self.estop = False
            self.belt_cmd = False
            log("원점 복귀, 계획 줄 비움")
            await self.reply(cmd_id, "done")
        else:
            await self.reply(cmd_id, "rejected", error=f"unknown action {action}")

    def do_estop(self):
        if not self.estop:
            log("비상정지: 벨트·팔 2·헤드 정지")
        self.estop = True
        self.belt_cmd = False
        if self.drain and not self.drain.done():
            self.drain.set_result(False)

    def on_pi(self, topic, msg):
        """로봇팔 1이 퍽을 올린 순간 → 벨트 시작점에 퍽 생김"""
        if topic == "cmd/arm1" and msg.get("action") == "pick":
            self.pick_cmds[msg.get("cmd_id")] = msg.get("battery_id")
        elif topic == "res/arm1" and msg.get("status") == "done" and msg.get("cmd_id") in self.pick_cmds:
            self.pick_cmds.pop(msg["cmd_id"])
            if not self.estop:
                self.pucks.append({"pos": 0.0, "stage": "before", "no": None, "to": None})

    # ---------- 버튼·센서 ----------
    async def estop_button(self):
        if self.a.estop_at <= 0:
            return
        await asyncio.sleep(self.a.estop_at)
        log("비상정지 버튼 눌림 → 벨트 즉시 정지")
        self.do_estop()
        await self.pub("event/estop", estop_body(self.run_id, NAME, ESTOP_BUTTON_ID, "PHYSICAL_BUTTON", "BUTTON_PRESSED"))
        await asyncio.sleep(self.a.estop_hold)
        log("비상정지 버튼 복귀")
        await self.pub("event/estop", estop_body(self.run_id, NAME, ESTOP_BUTTON_ID, "PHYSICAL_BUTTON", "BUTTON_RELEASED"))

    async def env_loop(self):
        if self.a.env_interval <= 0:
            return
        start = time.monotonic()
        alarm_done = False
        while True:
            readings = []
            for s, m, u, base, d in ENV_SENSORS:
                v = base + self.rnd.uniform(-d, d)
                readings.append({"sensor": s, "metric": m, "unit": u, "value": round(v)})
            await self.pub("sensor/env", env_body(self.run_id, NAME, readings))
            if not alarm_done and self.a.gas_alarm_at > 0 and time.monotonic() - start >= self.a.gas_alarm_at:
                alarm_done = True
                await self.notify("ENV_ALARM", None, {"sensor": "gas_1", "value": GAS_ALARM_RAW + 50})
            await asyncio.sleep(self.a.env_interval)

    async def watch_server(self, seen):
        while True:
            ok = time.monotonic() - seen["t"] < SERVER_TIMEOUT_SEC
            if ok != self.server_ok:
                self.server_ok = ok
                log("서버 하트비트 복구 → 벨트 다시 움직일 수 있음" if ok else "서버 하트비트 끊김 → 벨트 정지")
            await asyncio.sleep(0.5)

    async def receive(self, seen):
        async for m in self.client.messages:
            topic = m.topic.value
            if topic == "hb/server":
                seen["t"] = time.monotonic()
                continue
            try:
                msg = json.loads(to_text(m.payload))
            except ValueError:
                continue
            if not isinstance(msg, dict):
                continue
            if topic == f"cmd/{NAME}":
                asyncio.create_task(self.on_cmd(msg))
            elif topic in ("cmd/arm1", "res/arm1"):
                self.on_pi(topic, msg)

    async def run(self):
        status_topic = f"status/{NAME}"
        will = aiomqtt.Will(status_topic, payload="offline", qos=1, retain=True)
        async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier=NAME, keepalive=5, will=will) as c:
            self.client = c
            await c.publish(status_topic, "online", qos=1, retain=True)
            for t, q in ((f"cmd/{NAME}", 1), ("cmd/arm1", 1), ("res/arm1", 1), ("hb/server", 0)):
                await c.subscribe(t, qos=q)
            log(f"브로커 연결됨 ({BROKER_HOST}:{BROKER_PORT}), 명령 기다리는 중")
            seen = {"t": time.monotonic()}
            try:
                async with asyncio.TaskGroup() as tg:
                    tg.create_task(self.receive(seen))
                    tg.create_task(heartbeat_loop(c, NAME, 1.0, self.info))
                    tg.create_task(self.watch_server(seen))
                    tg.create_task(self.belt_loop())
                    tg.create_task(self.estop_button())
                    tg.create_task(self.env_loop())
            finally:
                with contextlib.suppress(Exception):
                    await asyncio.wait_for(c.publish(status_topic, "offline", qos=1, retain=True), 1.0)


def parse(argv=None):
    p = argparse.ArgumentParser(description="가짜 Mega (중계 포함)")
    p.add_argument("--speed", type=float, default=0.3, help="동작 시간 배율 (1=실제)")
    p.add_argument("--reversed", type=float, default=0.1, help="측정 시 극성 반대 확률")
    p.add_argument("--abnormal", type=float, default=0.05, help="측정 시 접촉 불량 확률")
    p.add_argument("--adc-fail", type=float, default=0.0, help="측정 실패(ADC_FAILED) 확률")
    p.add_argument("--arm2-fail", type=float, default=0.0, help="로봇팔 2 실패 확률")
    p.add_argument("--seed", type=int, default=None, help="난수 시드")
    p.add_argument("--env-interval", type=float, default=ENV_INTERVAL_SEC, help="환경 센서 발행 간격(초), 0이면 끔")
    p.add_argument("--gas-alarm-at", type=float, default=0, help="N초 뒤 가스 경고 한 번 (0이면 안 함)")
    p.add_argument("--estop-at", type=float, default=0, help="N초 뒤 비상정지 버튼 누름 (0이면 안 함)")
    p.add_argument("--estop-hold", type=float, default=5.0, help="버튼을 누르고 있는 시간(초)")
    return p.parse_args(argv)


if __name__ == "__main__":
    a = parse()
    run_main(lambda: FakeMega(a).run())
