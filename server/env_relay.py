"""
환경 중계 프로그램 (통신 규약 v1.5, 3-4) — 서버 PC에서 실행
================================================================
환경 센서·부저는 라인 Mega와 따로 Arduino Uno 한 대에 붙는다 (11/18 하드웨어 결정).
Uno는 MQTT를 못 쓰므로 USB 시리얼로 JSON 한 줄씩 보내고, 이 프로그램이 MQTT로 바꾼다.

  Uno ──시리얼 9600── [env_relay] ──MQTT──▶ sensor/env   (5초마다 측정값 → DB·관리자 프로그램)
                                       ──▶ event/env    ENV_ALARM (기준 넘은 순간 → 컨트롤러 WARN)
                                       ──▶ hb/env       1초 하트비트 (serial: ok / offline)
                                       ──▶ status/env   online / offline (LWT)

Uno가 보내는 줄 (env_uno.ino)
  {"t":"env","gas":182,"temp":24.1,"hum":41.0}       온습도 읽기 실패면 temp·hum이 null → 가스만 발행
  {"t":"alarm","sensor":"gas_1","metric":"GAS","value":612}
  {"t":"hb"}                                          1초마다. 3초 넘게 아무 줄도 없으면 serial: offline

- 장치 이름(client_id·device)은 env. 시각(ts)과 회차 번호(run_id, server/line에서 가져옴)는 이 프로그램이 붙인다
- 부저는 Uno가 스스로 울린다 (서버 명령 없음). 비상정지 부저는 Mega에 따로 있다
- 시리얼이 끊기면 3초마다 다시 연다 (USB를 뽑았다 꽂아도 됨)
- --fake: Uno 없이 가짜 값을 발행 (실행기 run_all.py가 가짜 장치를 켤 때 씀)

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pyserial, 같은 폴더에 line_common.py
사용법
  python env_relay.py --port COM5            Uno 포트
  python env_relay.py --list                 PC에 보이는 시리얼 포트 목록
  python env_relay.py --port COM5 --show     시리얼로 들어오는 줄을 전부 화면에 표시
  python env_relay.py --fake                 가짜 값 (5초마다)
  python env_relay.py --fake --gas-alarm-at 30   30초 뒤 가짜 가스 경고 한 번
  (아두이노 IDE 시리얼 모니터는 닫을 것 — 포트를 같이 못 씀)
"""

# ===================== 설정값 =====================
BROKER_HOST = "localhost"
BROKER_PORT = 1883
NAME = "env"                     # client_id·hb/·status/·device (규약 v1.5)
BAUD = 9600
SERIAL_SILENT_SEC = 3.0          # 이 시간 동안 Uno에서 아무 줄도 안 오면 serial: offline
SERIAL_RETRY_SEC = 3.0           # 포트를 못 열면 다시 시도하는 간격
GAS = ("gas_1", "GAS", "raw")
TEMP = ("temp_hum_1", "TEMPERATURE", "C")
HUM = ("temp_hum_1", "HUMIDITY", "%")
# 가짜 값 (--fake)
FAKE_INTERVAL_SEC = 5.0
FAKE_GAS = (90.0, 15.0)          # 평균, 흔들림 (깨끗한 공기 약 75~80raw, 11/12 실측)
FAKE_TEMP = (24.0, 0.8)
FAKE_HUM = (42.0, 3.0)
FAKE_GAS_ALARM_RAW = 300         # 가짜 가스 경고 값
# ================================================

import argparse
import asyncio
import json
import random
import time

import aiomqtt

from line_common import env_body, now_str, run_main, to_text

try:
    import serial
    import serial.tools.list_ports
except ImportError:
    serial = None


def log(msg):
    print(f"[{now_str()}] {msg}", flush=True)


def to_readings(line: dict) -> list[dict]:
    """시리얼 env 한 줄 → sensor/env readings. 값이 null이면 그 측정은 뺀다"""
    out = []
    for key, (sensor, metric, unit) in (("gas", GAS), ("temp", TEMP), ("hum", HUM)):
        v = line.get(key)
        if isinstance(v, (int, float)):
            out.append({"sensor": sensor, "metric": metric, "value": v, "unit": unit})
    return out


class EnvRelay:
    def __init__(self, args):
        self.a = args
        self.client: aiomqtt.Client | None = None
        self.run_id = None
        self.seq = 0                       # event/env 알림 번호 (1부터, 재시작 시 1)
        self.serial_ok = False
        self.last_line = 0.0               # 마지막으로 Uno 줄을 받은 시각 (monotonic)

    # ---------- MQTT ----------
    async def pub(self, topic, payload, qos=1, retain=False):
        await self.client.publish(topic, payload, qos=qos, retain=retain)

    async def watch_line(self):
        """server/line(retained)에서 진행 중 회차 번호를 가져옴"""
        async for m in self.client.messages:
            if str(m.topic) != "server/line":
                continue
            try:
                line = json.loads(to_text(m.payload))
            except json.JSONDecodeError:
                continue
            run_id = line.get("run_id") if line.get("mode") not in (None, "idle") else None
            if run_id != self.run_id:
                self.run_id = run_id
                log(f"회차: {run_id}")

    async def heartbeat(self):
        seq = 0
        while True:
            seq += 1
            if not self.a.fake and self.serial_ok and time.monotonic() - self.last_line > SERIAL_SILENT_SEC:
                self.serial_ok = False
                log(f"Uno에서 {SERIAL_SILENT_SEC:.0f}초 넘게 아무 줄도 없음 → serial: offline")
            body = {"device": NAME, "seq": seq, "ts": time.time(), "state": "run",
                    "serial": "ok" if (self.a.fake or self.serial_ok) else "offline"}
            await self.pub(f"hb/{NAME}", json.dumps(body), qos=0)
            await asyncio.sleep(1.0)

    async def send_env(self, readings):
        await self.pub("sensor/env", env_body(self.run_id, NAME, readings))
        log("→ sensor/env " + ", ".join(f"{r['sensor']}.{r['metric']}={r['value']}{r['unit']}" for r in readings))

    async def send_alarm(self, sensor, metric, value):
        self.seq += 1
        body = {"ev": "ENV_ALARM", "no": None, "seq": self.seq,
                "data": {"sensor": sensor, "metric": metric, "value": value}, "ts": now_str()}
        await self.pub("event/env", json.dumps(body, ensure_ascii=False))
        log(f"→ event/env ENV_ALARM {body['data']}")

    # ---------- Uno 줄 처리 ----------
    async def handle(self, line: dict):
        kind = line.get("t")
        if kind == "hb":
            return
        if kind == "env":
            readings = to_readings(line)
            if not readings:
                log(f"값 없음, 건너뜀: {line}")
                return
            if len(readings) < 3:
                log("온습도 읽기 실패 → 가스 값만 발행")
            await self.send_env(readings)
        elif kind == "alarm":
            await self.send_alarm(line.get("sensor"), line.get("metric"), line.get("value"))
        else:
            log(f"(모르는 종류, 무시) {line}")

    async def serial_loop(self):
        """포트를 열고 줄을 읽는다. 끊기면 SERIAL_RETRY_SEC마다 다시 연다"""
        warned = False
        while True:
            try:
                ser = await asyncio.to_thread(serial.Serial, self.a.port, self.a.baud, timeout=1)
            except (serial.SerialException, OSError) as e:
                if not warned:
                    log(f"포트 {self.a.port}를 열 수 없음 ({e}) → {SERIAL_RETRY_SEC:.0f}초마다 다시 시도"
                        " (Uno USB 연결, COM 번호, 시리얼 모니터가 열려 있지 않은지 확인)")
                    warned = True
                await asyncio.sleep(SERIAL_RETRY_SEC)
                continue
            warned = False
            log(f"시리얼 연결 {self.a.port} {self.a.baud} (Uno가 재시작됨, 첫 값까지 몇 초)")
            try:
                while True:
                    raw = await asyncio.to_thread(ser.readline)
                    if not raw:
                        continue
                    text = raw.decode("utf-8", errors="replace").strip()
                    if self.a.show:
                        log(f"(시리얼) {text}")
                    try:
                        line = json.loads(text)
                    except json.JSONDecodeError:
                        log(f"(JSON 아님, 무시) {text}")
                        continue
                    if not self.serial_ok:
                        log("serial: ok")
                    self.serial_ok = True
                    self.last_line = time.monotonic()
                    await self.handle(line)
            except (serial.SerialException, OSError) as e:
                log(f"시리얼 끊김 ({e}) → 다시 연결 시도")
            finally:
                self.serial_ok = False
                try:
                    ser.close()
                except Exception:
                    pass
            await asyncio.sleep(SERIAL_RETRY_SEC)

    async def fake_loop(self):
        """Uno 없이 가짜 값 발행. --gas-alarm-at N이면 N초 뒤 가스 경고 한 번"""
        start = time.monotonic()
        alarm_done = False
        log(f"가짜 모드: {FAKE_INTERVAL_SEC:.0f}초마다 sensor/env"
            + (f", {self.a.gas_alarm_at:.0f}초 뒤 가스 경고" if self.a.gas_alarm_at > 0 else ""))
        while True:
            gas = round(random.gauss(*FAKE_GAS))
            readings = [{"sensor": GAS[0], "metric": GAS[1], "value": gas, "unit": GAS[2]},
                        {"sensor": TEMP[0], "metric": TEMP[1], "value": round(random.gauss(*FAKE_TEMP), 1), "unit": TEMP[2]},
                        {"sensor": HUM[0], "metric": HUM[1], "value": round(random.gauss(*FAKE_HUM), 1), "unit": HUM[2]}]
            if not alarm_done and self.a.gas_alarm_at > 0 and time.monotonic() - start >= self.a.gas_alarm_at:
                alarm_done = True
                readings[0]["value"] = FAKE_GAS_ALARM_RAW
                await self.send_alarm(GAS[0], GAS[1], FAKE_GAS_ALARM_RAW)
            await self.send_env(readings)
            await asyncio.sleep(FAKE_INTERVAL_SEC)

    async def main(self):
        """브로커에 붙어 있는 동안 일한다. 브로커가 끊기면 3초 뒤 다시 붙는다"""
        will = aiomqtt.Will(f"status/{NAME}", payload="offline", qos=1, retain=True)
        while True:
            try:
                async with aiomqtt.Client(self.a.host, BROKER_PORT, identifier=NAME, keepalive=5, will=will) as client:
                    self.client = client
                    await self.pub(f"status/{NAME}", "online", retain=True)
                    await client.subscribe("server/line", qos=1)
                    log(f"브로커 연결 {self.a.host}:{BROKER_PORT} (client_id {NAME})")
                    try:
                        async with asyncio.TaskGroup() as tg:
                            tg.create_task(self.watch_line())
                            tg.create_task(self.heartbeat())
                            tg.create_task(self.fake_loop() if self.a.fake else self.serial_loop())
                    finally:
                        try:
                            await self.pub(f"status/{NAME}", "offline", retain=True)
                        except Exception:
                            pass
            except* aiomqtt.MqttError as eg:
                log(f"브로커 연결 끊김 ({eg.exceptions[0]}) → 3초 뒤 다시 연결")
            await asyncio.sleep(3)


def main():
    p = argparse.ArgumentParser(description="환경 중계 (Uno 시리얼 ↔ MQTT, 규약 v1.5)")
    p.add_argument("--port", help="Uno 포트 (예: COM5)")
    p.add_argument("--baud", type=int, default=BAUD)
    p.add_argument("--host", default=BROKER_HOST, help="브로커 주소 (기본 localhost)")
    p.add_argument("--list", action="store_true", help="시리얼 포트 목록")
    p.add_argument("--show", action="store_true", help="시리얼로 들어오는 줄을 전부 표시")
    p.add_argument("--fake", action="store_true", help="Uno 없이 가짜 값 발행")
    p.add_argument("--gas-alarm-at", type=float, default=0, help="(가짜) N초 뒤 가스 경고 한 번")
    a = p.parse_args()
    if not a.fake:
        if serial is None:
            raise SystemExit("pyserial이 없음 → python -m pip install pyserial")
        if a.list or not a.port:
            ports = list(serial.tools.list_ports.comports())
            print("시리얼 포트:" if ports else "연결된 시리얼 포트 없음")
            for pt in ports:
                print(f"  {pt.device}  {pt.description}")
            if not a.port:
                print("→ --port COM번호 로 실행 (Uno 없이 시험하려면 --fake)")
            return
    run_main(lambda: EnvRelay(a).main())


if __name__ == "__main__":
    main()
