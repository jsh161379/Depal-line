"""
환경 센서 시리얼 → MQTT 시험 프로그램 (시험용, 실제 Mega 중계 프로그램 아님)
================================================================================
아두이노(env_serial_test.ino)가 시리얼로 보내는 JSON 한 줄을 받아서
통신 규약 v1.3 형식으로 발행한다.

  {"t":"env", "gas":182, "temp":24.1, "hum":41.0}  →  sensor/env   (DB environment_readings)
  {"t":"alarm", "sensor":"gas_1", "value":612}      →  event/mega  ENV_ALARM (컨트롤러 → WARN 이벤트)

- 시각(ts)은 이 프로그램이 붙인다 (규약: Mega 메시지 시각은 PC 중계가 붙임)
- run_id는 server/line(retained)에서 진행 중인 회차를 가져온다. 회차가 없으면 null
- client_id는 env_test (진짜 중계의 mega와 겹치지 않게)
- 실제 Mega 중계 프로그램은 하드웨어 담당이 만든다. 이 파일은 서버·DB 경로 시험용

이 파일은 line_common.py 와 같은 폴더(C:\\depal\\server)에 둘 것.

필요한 것: Python 3.11 이상
  python -m pip install -r requirements.txt
  python -m pip install pyserial

실행 (아두이노 IDE 시리얼 모니터는 닫을 것 — 포트를 같이 못 씀):
  python env_bridge_test.py --port COM3
  python env_bridge_test.py --list            연결된 시리얼 포트 목록
"""

# ---------- 수정할 값 ----------
BROKER_HOST = "localhost"
BROKER_PORT = 1883
CLIENT_ID = "env_test"
DEVICE = "mega"                 # 메시지의 device (규약: 환경 센서는 mega)
BAUD = 9600
GAS = ("gas_1", "GAS", "raw")
TEMP = ("temp_hum_1", "TEMPERATURE", "C")
HUM = ("temp_hum_1", "HUMIDITY", "%")
# -------------------------------

import argparse
import asyncio
import json

import aiomqtt

from line_common import env_body, now_str, run_main, to_text

try:
    import serial
    import serial.tools.list_ports
except ImportError:
    raise SystemExit("pyserial이 없음 → python -m pip install pyserial")


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


class Bridge:
    def __init__(self, args, ser=None):
        self.a = args
        self.ser = ser          # 미리 열어 둔 시리얼 포트
        self.run_id = None
        self.seq = 0            # event/mega 알림 번호 (시험 프로그램 자체, 1부터)
        self.client = None

    async def watch_line(self):
        """server/line에서 진행 중 회차 번호를 가져옴"""
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

    async def read_serial(self):
        ser = self.ser
        log(f"시리얼 연결 {self.a.port} {self.a.baud} (아두이노가 재시작됨, 첫 값까지 몇 초)")
        try:
            while True:
                raw = await asyncio.to_thread(ser.readline)
                if not raw:
                    continue
                text = raw.decode("utf-8", errors="replace").strip()
                try:
                    line = json.loads(text)
                except json.JSONDecodeError:
                    log(f"(JSON 아님, 무시) {text}")
                    continue
                await self.handle(line)
        finally:
            ser.close()

    async def handle(self, line: dict):
        kind = line.get("t")
        if kind == "env":
            readings = to_readings(line)
            if not readings:
                log(f"값 없음, 건너뜀: {line}")
                return
            if len(readings) < 3:
                log("DHT11 읽기 실패 → 가스 값만 발행")
            await self.client.publish("sensor/env", env_body(self.run_id, DEVICE, readings), qos=1)
            log("→ sensor/env " + ", ".join(f"{r['sensor']}.{r['metric']}={r['value']}{r['unit']}" for r in readings))
        elif kind == "alarm":
            self.seq += 1
            data = {"sensor": line.get("sensor"), "value": line.get("value")}
            if line.get("metric"):   # 규약 v1.4 후보: 같은 센서(temp_hum_1)의 온도·습도 경고 구분
                data["metric"] = line["metric"]
            body = {"ev": "ENV_ALARM", "no": None, "seq": self.seq, "data": data, "ts": now_str()}
            await self.client.publish("event/mega", json.dumps(body, ensure_ascii=False), qos=1)
            log(f"→ event/mega ENV_ALARM {body['data']}")
        else:
            log(f"(모르는 종류, 무시) {line}")

    async def main(self):
        async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier=CLIENT_ID) as client:
            self.client = client
            await client.subscribe("server/line", qos=1)
            log(f"브로커 연결 {BROKER_HOST}:{BROKER_PORT} (client_id {CLIENT_ID})")
            async with asyncio.TaskGroup() as tg:
                tg.create_task(self.watch_line())
                tg.create_task(self.read_serial())


def main():
    p = argparse.ArgumentParser(description="환경 센서 시리얼 → MQTT 시험")
    p.add_argument("--port", help="아두이노 포트 (예: COM3)")
    p.add_argument("--baud", type=int, default=BAUD)
    p.add_argument("--list", action="store_true", help="시리얼 포트 목록")
    a = p.parse_args()
    if a.list or not a.port:
        ports = list(serial.tools.list_ports.comports())
        print("시리얼 포트:" if ports else "연결된 시리얼 포트 없음")
        for pt in ports:
            print(f"  {pt.device}  {pt.description}")
        if not a.port:
            print("→ --port COM번호 로 실행")
        return
    try:
        ser = serial.Serial(a.port, a.baud, timeout=1)
    except serial.SerialException as e:
        if "PermissionError" in str(e) or "Access" in str(e) or "액세스" in str(e):
            print(f"{a.port}을(를) 다른 프로그램이 쓰고 있음 → 아두이노 IDE의 시리얼 모니터·플로터를 닫고 다시 실행")
        else:
            print(f"{a.port}을(를) 열 수 없음: {e}\n→ 포트 번호 확인: python env_bridge_test.py --list")
        return
    run_main(lambda: Bridge(a, ser).main())


if __name__ == "__main__":
    main()
