"""
중앙 서버 1단계 - 하트비트 감시 (v2: DB 담당과 형식 맞춤)
=========================================================
하는 일
  1) 장치(arm1, mega, yolo, agv)가 1초마다 보내는 하트비트(hb/<장치>)를 받아 정상/지연/끊김 판정
  2) 서버도 1초마다 hb/server 를 보냄 (Mega는 이게 끊기면 벨트를 멈춤 - 하드웨어 팀원과 약속)
  3) MQTT 유언 메시지(LWT, status/<장치>)로 연결 끊김을 바로 알아챔
  4) 상태가 바뀔 때만 server/events 로 이벤트 발행 (DB 저장 프로그램이 구독해 MySQL에 저장)
  5) 화면 프로그램(WPF)용으로 server/devices(현재 상태, retained) 발행

v2에서 바뀐 점
  - SQLite 기록 삭제 (DB는 DB 담당이 MySQL로 저장)
  - run_id: 서버를 켤 때마다 새로 만드는 회차 번호. 재시작해도 이전 회차와 겹치지 않음
  - event_id: 이벤트마다 붙는 고유 번호. 같은 이벤트가 다시 전달돼도 DB에서 중복 저장을 막을 수 있음
  - 브로커가 끊긴 동안 생긴 이벤트는 보관했다가 다시 연결되면 순서대로 발행

필요한 것: Python 3.11 이상
  pip install "aiomqtt>=2" pydantic
실행: python central_server.py   (끄기: Ctrl+C)
메시지 형식은 '메시지_형식_v1.md' 참고
"""

# ===================== 설정값 (여기만 바꾸면 됨) =====================
BROKER_HOST = "127.0.0.1"        # 브로커(Mosquitto)가 서버와 같은 PC면 그대로
BROKER_PORT = 1883
SERVER_NAME = "server"
EXPECTED_DEVICES = ["arm1", "mega", "yolo", "agv", "env"]   # 서버가 기다리는 장치 이름 (env = 환경 센서 Uno, 규약 v1.5)

HB_INTERVAL_SEC = 1.0            # 서버 하트비트 보내는 간격
WARN_AFTER_SEC = 3.0             # 이 시간 동안 하트비트 없으면 '지연'  (임시값)
OFFLINE_AFTER_SEC = 5.0          # 이 시간 동안 하트비트 없으면 '끊김'  (임시값)
CHECK_INTERVAL_SEC = 0.5         # 타임아웃 검사 간격
MQTT_KEEPALIVE_SEC = 5           # 브로커와의 연결 확인 간격
RECONNECT_DELAY_SEC = 3.0        # 브로커 연결 실패 시 다시 시도까지 기다리는 시간
PENDING_MAX = 1000               # 브로커가 끊긴 동안 보관할 이벤트 최대 개수
# ====================================================================

import asyncio
import contextlib
import json
import secrets
import sys
import time
import uuid
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime

import aiomqtt
from pydantic import BaseModel, ConfigDict, ValidationError

STATUS_KR = {"unknown": "확인 전", "ok": "정상", "warn": "지연", "offline": "끊김"}


def now_str() -> str:
    """한국 시간(서버 PC 시계) 밀리초까지. MySQL DATETIME(3)에 그대로 넣을 수 있는 형식."""
    return datetime.now().isoformat(sep=" ", timespec="milliseconds")


def make_run_id() -> str:
    """예: 20261112-093015-a3f9  (시작 시각 + 무작위 4글자 → 재시작해도 겹치지 않음)"""
    return datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + secrets.token_hex(2)


class Heartbeat(BaseModel):
    """하트비트 메시지 형식. 정해진 4개 외의 값(gripper 등)은 자유롭게 추가 가능."""
    model_config = ConfigDict(extra="allow")
    device: str
    seq: int
    ts: float
    state: str = "run"


@dataclass
class DeviceInfo:
    name: str
    status: str = "unknown"            # unknown / ok / warn / offline
    last_seen: float | None = None     # time.monotonic() 기준 마지막 수신 시각
    last_seq: int | None = None
    info: dict = field(default_factory=dict)   # 마지막 하트비트의 추가 정보
    appeared: float = field(default_factory=time.monotonic)   # 목록에 처음 나타난 시각 (기다리는 장치는 서버 시작 시각)


def to_text(payload) -> str:
    if isinstance(payload, (bytes, bytearray)):
        return payload.decode("utf-8", errors="replace")
    return "" if payload is None else str(payload)


class CentralServer:
    def __init__(self):
        self.run_id = make_run_id()
        self.devices = {name: DeviceInfo(name) for name in EXPECTED_DEVICES}
        self.client: aiomqtt.Client | None = None
        self.hb_seq = 0
        self.started = time.monotonic()
        self.pending: deque[str] = deque(maxlen=PENDING_MAX)   # 아직 발행 못 한 이벤트(JSON)

    # ---------- 이벤트: 화면 출력 + server/events 발행 ----------
    async def event(self, device: str, level: str, message: str):
        body = {
            "event_id": uuid.uuid4().hex,
            "run_id": self.run_id,
            "ts": now_str(),
            "device": device,
            "level": level,
            "message": message,
        }
        print(f"[{body['ts']}] {level:<5} {device:<6} {message}", flush=True)
        self.pending.append(json.dumps(body, ensure_ascii=False))
        await self.flush_pending()

    async def flush_pending(self):
        """보관 중인 이벤트를 오래된 것부터 발행. 실패하면 남겨 두고 다음 연결 때 다시 시도."""
        while self.pending and self.client is not None:
            try:
                await self.client.publish("server/events", self.pending[0], qos=1)
            except aiomqtt.MqttError:
                return
            self.pending.popleft()

    async def publish_snapshot(self):
        """화면 프로그램이 언제 켜져도 현재 상태를 바로 받도록 retained로 보냄."""
        if self.client is None:
            return
        now = time.monotonic()
        devices = []
        for d in self.devices.values():
            devices.append({
                "name": d.name,
                "status": d.status,
                "age_sec": None if d.last_seen is None else round(now - d.last_seen, 1),
                "last_seq": d.last_seq,
                "info": d.info,
            })
        body = json.dumps({"run_id": self.run_id, "ts": now_str(), "devices": devices},
                          ensure_ascii=False)
        with contextlib.suppress(aiomqtt.MqttError):
            await self.client.publish("server/devices", body, qos=1, retain=True)

    def get_device(self, name: str) -> tuple[DeviceInfo, bool]:
        if name in self.devices:
            return self.devices[name], False
        self.devices[name] = DeviceInfo(name)
        return self.devices[name], True

    # ---------- 받은 메시지 처리 ----------
    async def on_heartbeat(self, name: str, raw: str):
        try:
            hb = Heartbeat.model_validate_json(raw)
        except ValidationError as e:
            first = e.errors()[0]
            await self.event(name, "WARN", f"하트비트 형식 오류: {first.get('loc')} {first.get('msg')}")
            return

        dev, is_new = self.get_device(name)
        if is_new:
            await self.event(name, "INFO", "목록에 없던 장치의 하트비트 (자동 추가)")

        # 번호 검사: 건너뛰면 메시지 빠짐, 작아지면 장치 재시작
        if dev.last_seq is not None:
            gap = hb.seq - dev.last_seq
            if gap > 1:
                await self.event(name, "WARN", f"하트비트 {gap - 1}개 빠짐 (seq {dev.last_seq} → {hb.seq})")
            elif gap <= 0:
                await self.event(name, "INFO", f"번호가 다시 시작됨 (seq {dev.last_seq} → {hb.seq}), 장치 재시작으로 보임")

        extra = {k: v for k, v in hb.model_dump().items() if k not in ("device", "seq", "ts")}
        info_changed = extra != dev.info
        first_time = dev.last_seen is None     # 이번 서버 실행에서 처음 받는 하트비트인가

        dev.last_seq = hb.seq
        dev.last_seen = time.monotonic()
        dev.info = extra

        if dev.status != "ok":
            old = dev.status
            dev.status = "ok"
            if first_time:
                # 서버 시작 전 기록이나 '한 번도 못 받음'으로 끊김이었어도, 처음 받은 것이면 '수신 시작'
                await self.event(name, "INFO", "하트비트 수신 시작 → 정상")
            else:
                await self.event(name, "INFO", f"복구됨 ({STATUS_KR[old]} → 정상)")
            await self.publish_snapshot()
        elif info_changed:
            await self.publish_snapshot()   # 예: gripper normal → weak

    async def on_status(self, name: str, text: str, retained: bool):
        dev, _ = self.get_device(name)
        text = text.strip().lower()
        if text == "offline":
            if retained:
                # 서버가 켜지기 전에 이미 꺼져 있던 장치 (브로커에 남아 있던 기록)
                if dev.status == "unknown":
                    dev.status = "offline"
                    await self.event(name, "INFO", "서버 시작 전부터 꺼져 있던 장치")
                    await self.publish_snapshot()
            elif dev.status != "offline":
                dev.status = "offline"
                await self.event(name, "ERROR", "연결 끊김 (LWT 또는 종료 알림 수신)")
                await self.publish_snapshot()
        elif text == "online" and not retained:
            await self.event(name, "INFO", "브로커에 접속함, 하트비트 기다리는 중")

    # ---------- 동시에 돌아가는 일 3가지 ----------
    async def receive_messages(self):
        async for message in self.client.messages:
            kind, _, name = message.topic.value.partition("/")
            if not name or name == SERVER_NAME:
                continue
            text = to_text(message.payload)
            if kind == "hb":
                await self.on_heartbeat(name, text)
            elif kind == "status":
                await self.on_status(name, text, message.retain)

    async def send_heartbeat(self):
        while True:
            self.hb_seq += 1
            body = Heartbeat(device=SERVER_NAME, seq=self.hb_seq, ts=time.time(), state="run").model_dump_json()
            await self.client.publish(f"hb/{SERVER_NAME}", body, qos=0)
            await asyncio.sleep(HB_INTERVAL_SEC)

    async def watch_timeouts(self):
        while True:
            now = time.monotonic()
            changed = False
            for dev in self.devices.values():
                if dev.status == "offline":
                    continue
                if dev.last_seen is None:
                    # 목록에 나타난 뒤 OFFLINE_AFTER_SEC 동안 하트비트가 한 번도 안 온 장치.
                    # 서버 시작 시각이 아니라 장치가 나타난 시각부터 센다 → 서버가 켜진 뒤 새로 접속한 장치
                    # (status 직후 첫 하트비트가 오기 전 1초 남짓한 틈)에 잘못 경고하지 않음
                    if dev.status == "unknown" and now - dev.appeared >= OFFLINE_AFTER_SEC:
                        dev.status = "offline"
                        await self.event(dev.name, "WARN", f"{OFFLINE_AFTER_SEC:g}초 동안 하트비트를 한 번도 받지 못함")
                        changed = True
                    continue
                elapsed = now - dev.last_seen
                if elapsed >= OFFLINE_AFTER_SEC:
                    dev.status = "offline"
                    await self.event(dev.name, "ERROR", f"하트비트 {elapsed:.1f}초 없음 → 끊김")
                    changed = True
                elif elapsed >= WARN_AFTER_SEC and dev.status == "ok":
                    dev.status = "warn"
                    await self.event(dev.name, "WARN", f"하트비트 {elapsed:.1f}초 없음 → 지연")
                    changed = True
            if changed:
                await self.publish_snapshot()
            await asyncio.sleep(CHECK_INTERVAL_SEC)

    # ---------- 연결과 재연결 ----------
    async def run_forever(self):
        print(f"[{now_str()}] 서버 시작, run_id = {self.run_id}", flush=True)
        status_topic = f"status/{SERVER_NAME}"
        while True:
            will = aiomqtt.Will(status_topic, payload="offline", qos=1, retain=True)
            try:
                async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier=SERVER_NAME,
                                          keepalive=MQTT_KEEPALIVE_SEC, will=will) as client:
                    self.client = client
                    await client.publish(status_topic, "online", qos=1, retain=True)
                    await client.subscribe("hb/+", qos=0)
                    await client.subscribe("status/+", qos=1)
                    await self.event(SERVER_NAME, "INFO",
                                     f"브로커 연결됨 ({BROKER_HOST}:{BROKER_PORT}), run_id {self.run_id}")
                    await self.publish_snapshot()
                    try:
                        async with asyncio.TaskGroup() as tg:
                            tg.create_task(self.receive_messages())
                            tg.create_task(self.send_heartbeat())
                            tg.create_task(self.watch_timeouts())
                    finally:
                        # Ctrl+C로 정상 종료할 때는 LWT가 안 나가므로 직접 offline 알림
                        with contextlib.suppress(Exception):
                            await asyncio.wait_for(
                                client.publish(status_topic, "offline", qos=1, retain=True), 1.0)
            except* aiomqtt.MqttError as eg:
                self.client = None
                print(f"[{now_str()}] ERROR server 브로커 연결 실패/끊김: {eg.exceptions[0]} "
                      f"→ {RECONNECT_DELAY_SEC}초 뒤 재시도 (보관 중 이벤트 {len(self.pending)}개)", flush=True)
            self.client = None
            await asyncio.sleep(RECONNECT_DELAY_SEC)


def main():
    server = CentralServer()
    try:
        if sys.platform.startswith("win"):
            # Windows에서 aiomqtt는 SelectorEventLoop가 필요함
            with asyncio.Runner(loop_factory=asyncio.SelectorEventLoop) as runner:
                runner.run(server.run_forever())
        else:
            asyncio.run(server.run_forever())
    except KeyboardInterrupt:
        print(f"[{now_str()}] 서버 종료 (Ctrl+C)", flush=True)


if __name__ == "__main__":
    main()
