"""
공통 모듈 - 라인 컨트롤러·가짜 장치·명령 도구가 함께 씀
=========================================================
이 파일은 line_controller.py, fake_arm1.py, fake_yolo.py, line_cmd.py 와 같은 폴더에 둘 것.
직접 실행하는 파일이 아님.

필요한 것: Python 3.11 이상,  pip install "aiomqtt>=2" pydantic
"""

import asyncio
import contextlib
import json
import secrets
import sys
import time
import uuid
from datetime import datetime

import aiomqtt

# ---------- 시간·번호 ----------

def now_str() -> str:
    """한국 시간(PC 시계) 밀리초까지. 예: 2026-11-12 09:31:02.120 (MySQL DATETIME(3)에 그대로 저장 가능)"""
    return datetime.now().isoformat(sep=" ", timespec="milliseconds")


def make_run_id() -> str:
    """회차 번호. 예: 20261112-093015-a3f9 (시작 시각 + 무작위 4글자 → 겹치지 않음)"""
    return datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + secrets.token_hex(2)


def new_id() -> str:
    """명령 번호·이벤트 번호용 32글자"""
    return uuid.uuid4().hex


def to_text(payload) -> str:
    if isinstance(payload, (bytes, bytearray)):
        return payload.decode("utf-8", errors="replace")
    return "" if payload is None else str(payload)


def event_body(run_id: str, device: str, level: str, message: str) -> str:
    """server/events 형식 (메시지_형식 v1.1)"""
    return json.dumps({"event_id": new_id(), "run_id": run_id, "ts": now_str(),
                       "device": device, "level": level, "message": message}, ensure_ascii=False)


def estop_body(run_id, device: str, button_id, source: str, kind: str, details=None) -> str:
    """event/estop 형식 (메시지_형식 v1.3) → DB emergency_stop_events
    kind: BUTTON_PRESSED / BUTTON_RELEASED / STOP_CONFIRMED / RESET_CONFIRMED"""
    return json.dumps({"event_id": new_id(), "run_id": run_id, "ts": now_str(), "device": device,
                       "button_id": button_id, "source": source, "kind": kind, "details": details},
                      ensure_ascii=False)


def env_body(run_id, device: str, readings: list[dict]) -> str:
    """sensor/env 형식 (메시지_형식 v1.3) → DB environment_readings (readings 한 줄 = DB 한 행)"""
    return json.dumps({"reading_id": new_id(), "run_id": run_id, "ts": now_str(), "device": device,
                       "readings": readings}, ensure_ascii=False)


# ---------- 실행 ----------

def run_main(coro_factory):
    """Windows에서도 aiomqtt가 동작하도록 실행 (Ctrl+C로 종료)"""
    try:
        try:
            if sys.platform.startswith("win"):
                with asyncio.Runner(loop_factory=asyncio.SelectorEventLoop) as runner:
                    runner.run(coro_factory())
            else:
                asyncio.run(coro_factory())
        except* aiomqtt.MqttError as eg:
            print(f"브로커에 연결할 수 없음: {eg.exceptions[0]}\n"
                  f"→ Mosquitto(1 BROKER 창)가 켜져 있는지 확인하세요. 켜는 순서: 브로커 → 서버 → 나머지", flush=True)
    except KeyboardInterrupt:
        print("종료", flush=True)


async def heartbeat_loop(client: aiomqtt.Client, name: str, interval: float = 1.0, info_fn=None):
    """1초마다 hb/<name> 발행. info_fn()이 돌려주는 값(gripper 등)을 같이 실음"""
    seq = 0
    while True:
        seq += 1
        body = {"device": name, "seq": seq, "ts": time.time(), "state": "run"}
        if info_fn:
            body.update(info_fn())
        await client.publish(f"hb/{name}", json.dumps(body), qos=0)
        await asyncio.sleep(interval)


# ---------- 가짜 장치 공통 동작 (규약 2장 cmd/res) ----------

async def run_device(name: str, handler, *, host: str, port: int, info_fn=None,
                     extra_topics=(), on_extra=None, server_timeout: float = 3.0,
                     on_server_lost=None, log=print, extra_tasks=(), state: dict | None = None):
    """
    cmd/<name>을 받아 handler(action, params)를 실행하고 res/<name>으로 답한다.
      - 받자마자 accepted, 끝나면 done / failed
      - 실행 중 새 명령은 rejected (estop만 예외: 실행 중인 동작을 취소)
      - 같은 cmd_id가 다시 오면 다시 실행하지 않고 마지막 응답을 다시 보냄
    handler는 (status, data, error)를 돌려주는 async 함수.
    extra_tasks: async fn(client) 목록. 연결된 동안 함께 실행 (센서 발행 등)
    state: 받은 명령의 run_id를 state["run_id"]에 기록 (장치가 보내는 메시지에 회차 번호를 붙일 때 사용)
    """
    status_topic = f"status/{name}"
    will = aiomqtt.Will(status_topic, payload="offline", qos=1, retain=True)
    async with aiomqtt.Client(host, port, identifier=name, keepalive=5, will=will) as client:
        await client.publish(status_topic, "online", qos=1, retain=True)
        await client.subscribe(f"cmd/{name}", qos=1)
        await client.subscribe("hb/server", qos=0)
        for t in extra_topics:
            await client.subscribe(t, qos=1)
        log(f"[{name}] 브로커 연결됨 ({host}:{port}), 명령 기다리는 중")

        last_res: dict[str, str] = {}          # cmd_id → 마지막 응답 (중복 명령 대비)
        cur = {"task": None, "cmd_id": None}
        server_seen = {"t": time.monotonic(), "ok": None}

        async def reply(cmd_id, status, data=None, error=None):
            body = json.dumps({"cmd_id": cmd_id, "status": status, "data": data or {},
                               "error": error, "ts": now_str()}, ensure_ascii=False)
            if status != "accepted":
                last_res[cmd_id] = body
                if len(last_res) > 200:
                    last_res.pop(next(iter(last_res)))
            await client.publish(f"res/{name}", body, qos=1)

        async def execute(cmd):
            cmd_id, action = cmd["cmd_id"], cmd.get("action")
            try:
                status, data, error = await handler(action, cmd.get("params") or {})
            except asyncio.CancelledError:
                status, data, error = "failed", {}, "estop"
            except Exception as e:                       # 가짜 장치 코드 오류
                status, data, error = "failed", {}, repr(e)
            if action != "estop":
                cur["task"], cur["cmd_id"] = None, None
            log(f"[{name}] {action} {cmd.get('params') or ''} → {status}"
                + (f" {data}" if data else "") + (f" ({error})" if error else ""))
            await reply(cmd_id, status, data, error)

        async def receive(tg: asyncio.TaskGroup):
            async for m in client.messages:
                topic = m.topic.value
                if topic == "hb/server":
                    server_seen["t"] = time.monotonic()
                    continue
                if topic != f"cmd/{name}":
                    if on_extra:
                        on_extra(topic, to_text(m.payload))
                    continue
                try:
                    cmd = json.loads(to_text(m.payload))
                    cmd_id = cmd["cmd_id"]
                except (ValueError, KeyError):
                    log(f"[{name}] 형식이 틀린 명령 무시")
                    continue
                if state is not None and cmd.get("run_id"):
                    state["run_id"] = cmd["run_id"]
                if cmd_id in last_res:                    # 같은 명령이 두 번 옴 → 다시 실행하지 않음
                    await client.publish(f"res/{name}", last_res[cmd_id], qos=1)
                    continue
                if cmd_id == cur["cmd_id"]:               # 실행 중인 명령이 또 옴 → 무시
                    continue
                action = cmd.get("action")
                if action == "estop":
                    if cur["task"]:
                        cur["task"].cancel()
                    await reply(cmd_id, "accepted")
                    tg.create_task(execute(cmd))
                    continue
                if cur["task"] is not None:
                    await reply(cmd_id, "rejected", error="busy")
                    continue
                await reply(cmd_id, "accepted")
                cur["cmd_id"] = cmd_id
                cur["task"] = tg.create_task(execute(cmd))

        async def watch_server():
            while True:
                ok = time.monotonic() - server_seen["t"] < server_timeout
                if ok != server_seen["ok"]:
                    server_seen["ok"] = ok
                    if not ok:
                        log(f"[{name}] 서버 하트비트 끊김!")
                        if on_server_lost:
                            on_server_lost()
                await asyncio.sleep(0.5)

        try:
            async with asyncio.TaskGroup() as tg:
                tg.create_task(receive(tg))
                tg.create_task(heartbeat_loop(client, name, 1.0, info_fn))
                tg.create_task(watch_server())
                for fn in extra_tasks:
                    tg.create_task(fn(client))
        finally:
            with contextlib.suppress(Exception):
                await asyncio.wait_for(client.publish(status_topic, "offline", qos=1, retain=True), 1.0)
