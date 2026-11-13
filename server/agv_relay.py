"""
# agv_relay_v4 (agv_relay_v3 대신): AGV를 껐다 켜면(EN 리셋) 블루투스 포트가 죽은 채 남아 "쓰기 실패"만 반복되던 문제 →
#   하트비트가 5초 넘게 안 오거나 쓰기가 2번 연속 실패하면 포트를 닫고 다시 엶 (자동 재연결)
# v3: 하트비트의 "state"를 덮어쓰지 않고 "agv_state"로 보냄
AGV 중계 프로그램 - 서버 PC(또는 노트북)에서 실행 (통신 규약 v1.4, 3-3)
=======================================================================
AGV(ESP32)는 MQTT를 쓰지 않고 블루투스(SPP)로만 이야기한다. 이 프로그램이 블루투스 ↔ MQTT를 바꿔 준다.
mega_relay.py와 같은 구조 (JSON 한 줄씩, ts는 중계가 붙임).

  서버 ──cmd/agv──▶ [중계] ──블루투스 {"c":"go","id":5,"count":8}──▶ AGV
  서버 ◀─res/agv──  [중계] ◀─블루투스 {"t":"ack","id":5,"ok":1}──── AGV   → accepted
                           ◀─블루투스 {"t":"done","id":5,"ok":1}─── AGV   → done (도착·내림 / 복귀)
       ◀─hb/agv             ◀─블루투스 {"t":"hb","st":"home","load":0}  (1초마다)

규약 3-3
  go     {"count": 8}  → done = 목적지 도착, 적재물 내림   (서버 제한 120초)
  return {}            → done = 분류 위치(A 적재 자리)로 복귀 (서버 제한 120초)
  + 공통 규칙: estop은 언제나 즉시, reset은 비상정지 해제

중계가 하는 일
  - 명령을 AGV에 전달. AGV가 ack(ok:1)를 보내면 accepted, done을 보내면 done / failed
  - ack가 2초 안에 없으면 rejected(agv_no_ack), done이 110초 안에 없으면 failed(agv_timeout)
  - 같은 cmd_id가 다시 오면 다시 보내지 않고 마지막 응답을 다시 보냄
  - 1초마다 AGV에 {"c":"hb"}를 보냄 (AGV는 3초간 아무것도 못 받으면 움직이다가도 멈춤)
  - AGV 하트비트가 3초 넘게 없으면 hb/agv의 link를 "offline"으로 (명령은 rejected, estop은 그래도 보내 봄)
  - hb/agv: {..., "link": "ok"/"offline", "agv_state": "home"/... (v3: state → agv_state), "carrying": true/false}

블루투스 형식은 "AGV_블루투스_형식_초안.md". 키 이름이 바뀌면 이 파일만 고치면 된다.

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pyserial, 같은 폴더에 line_common.py
블루투스 연결: Windows 설정 → 블루투스 → 장치 추가 → AGV-01 페어링
              → 제어판 "장치 및 프린터" → AGV-01 속성 → 하드웨어 탭에서 "나가는(Outgoing)" COM 번호 확인
사용법
  python agv_relay.py --port COM7          AGV 블루투스 포트 (나가는 쪽 COM)
  python agv_relay.py --list               PC에 보이는 시리얼 포트 목록
  python agv_relay.py --port COM7 --show   블루투스로 오가는 줄을 전부 화면에 표시
  python agv_relay.py --port COM7 --host 192.168.0.158   브로커가 다른 PC일 때
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
NAME = "agv"
SERIAL_PORT = "COM7"          # --port로 바꿀 수 있음
BAUD = 115200                 # 블루투스 SPP는 속도와 상관없지만 형식상 넣음
ACK_TIMEOUT_SEC = 2.0         # AGV ack 대기 (서버의 accepted 제한 2초)
DONE_TIMEOUT_SEC = 110.0      # go·return 끝날 때까지 대기 (서버 제한 120초보다 짧게)
LINK_HB_TIMEOUT_SEC = 3.0     # AGV 하트비트가 이만큼 없으면 link offline
HB_TO_AGV_SEC = 1.0           # AGV에 {"c":"hb"} 보내는 간격
RECONNECT_SEC = 3.0           # 블루투스 포트가 안 열리면 다시 시도하는 간격
REOPEN_AFTER_SEC = 5.0        # v4: 포트는 열려 있는데 AGV 하트비트가 이만큼 없으면 포트를 닫고 다시 엶
WRITE_FAIL_REOPEN = 2         # v4: 쓰기가 이만큼 연속 실패하면 포트를 닫고 다시 엶
# =================================================

import argparse
import asyncio
import contextlib
import json
import threading
import time

import aiomqtt

from line_common import heartbeat_loop, now_str, run_main, to_text

ACTIONS = ("go", "return", "estop", "reset")


def log(s):
    print(f"[{now_str()[11:23]}] [agv-relay] {s}", flush=True)


class SerialLink:
    """블루투스 COM 포트를 스레드로 읽음 (mega_relay.py와 같은 모양)"""

    def __init__(self, port, baud):
        self.port, self.baud = port, baud
        self.ser = None

    def open(self):
        import serial                          # pip install pyserial
        self.ser = serial.Serial(self.port, self.baud, timeout=0.2, write_timeout=1.0)
        self.ser.reset_input_buffer()          # ESP32는 블루투스로 연결해도 리셋되지 않음

    def readline(self) -> str | None:
        raw = self.ser.readline()
        return raw.decode("utf-8", errors="replace").strip() if raw else None

    def write(self, text: str):
        self.ser.write((text + "\n").encode("utf-8"))

    def close(self):
        with contextlib.suppress(Exception):
            self.ser.close()


class AgvRelay:
    def __init__(self, link, host, port, show=False):
        self.link = link
        self.host, self.port = host, port
        self.show = show
        self.client: aiomqtt.Client | None = None
        self.loop: asyncio.AbstractEventLoop | None = None
        self.lines: asyncio.Queue = asyncio.Queue()
        self.serial_open = False
        self.write_fails = 0             # v4: 연속 쓰기 실패 횟수
        self.opened_at = 0.0             # v4: 포트를 연 시각
        self.write_lock = threading.Lock()
        self.agv_seen = 0.0              # 마지막으로 AGV에서 JSON 줄을 받은 시각
        self.agv_state = None            # home / going / away / returning / stopped / estop
        self.agv_load = False
        self.next_id = 0
        self.acks: dict[int, asyncio.Future] = {}    # 블루투스 id → ack 기다림
        self.dones: dict[int, asyncio.Future] = {}   # 블루투스 id → done 기다림
        self.last_res: dict[str, str] = {}           # cmd_id → 마지막 응답 (같은 명령이 또 오면 다시 보냄)
        self.running: set[str] = set()               # 처리 중인 cmd_id
        self.moving_cmd = None                       # 실행 중인 go/return의 cmd_id (한 번에 하나)

    # ---------- 블루투스 ----------
    def link_ok(self):
        return self.serial_open and time.monotonic() - self.agv_seen < LINK_HB_TIMEOUT_SEC

    def reader_thread(self):
        while True:
            if not self.serial_open:
                try:
                    self.link.open()
                    self.serial_open = True
                    self.write_fails = 0
                    self.opened_at = time.monotonic()
                    self.loop.call_soon_threadsafe(log, f"블루투스 연결됨 ({self.link.port})")
                except Exception as e:
                    self.loop.call_soon_threadsafe(
                        log, f"블루투스 연결 실패 ({self.link.port}): {e} → {RECONNECT_SEC:g}초 뒤 다시")
                    time.sleep(RECONNECT_SEC)
                    continue
            try:
                line = self.link.readline()
            except Exception as e:
                if self.serial_open:                 # v4: reopen()이 닫은 거면 로그 생략
                    self.serial_open = False
                    self.link.close()
                    self.loop.call_soon_threadsafe(log, f"블루투스 끊김: {e}")
                time.sleep(0.2)
                continue
            if line:
                self.loop.call_soon_threadsafe(self.lines.put_nowait, line)

    def send(self, obj: dict) -> bool:
        text = json.dumps(obj, ensure_ascii=False, separators=(",", ":"))
        if not self.serial_open:
            return False
        try:
            with self.write_lock:
                self.link.write(text)
        except Exception as e:
            self.write_fails += 1
            log(f"블루투스 쓰기 실패: {e}")
            if self.write_fails >= WRITE_FAIL_REOPEN:
                self.reopen("쓰기가 계속 실패")
            return False
        self.write_fails = 0
        if self.show and obj.get("c") != "hb":
            log(f"PC → AGV  {text}")
        return True

    # ---------- MQTT ----------
    def reopen(self, why: str):
        """v4: 블루투스 포트를 닫음 → reader_thread가 RECONNECT_SEC 뒤 다시 엶"""
        if not self.serial_open:
            return
        self.serial_open = False
        self.link.close()
        log(f"블루투스 다시 연결 ({why})")

    async def pub(self, topic, body, qos=1):
        if self.client is not None:
            with contextlib.suppress(aiomqtt.MqttError):
                await self.client.publish(topic, body, qos=qos)

    async def reply(self, cmd_id, status, data=None, error=None):
        body = json.dumps({"cmd_id": cmd_id, "status": status, "data": data or {}, "error": error,
                           "ts": now_str()}, ensure_ascii=False)
        if status != "accepted":
            self.last_res[cmd_id] = body
            if len(self.last_res) > 200:
                self.last_res.pop(next(iter(self.last_res)))
        await self.pub(f"res/{NAME}", body)
        log(f"res {status}" + (f" ({error})" if error else "") + f"  [{cmd_id[:8]}]")

    def info(self):
        ok = self.link_ok()
        return {"link": "ok" if ok else "offline",
                "agv_state": self.agv_state if ok else "unknown",   # v3: "state"는 규약상 "run" 고정이라 이름 바꿈
                "carrying": bool(self.agv_load) if ok else False}

    # ---------- 서버 명령 → AGV ----------
    async def on_cmd(self, cmd):
        cmd_id, action, prm = cmd.get("cmd_id"), cmd.get("action"), cmd.get("params") or {}
        if not cmd_id:
            return
        if cmd_id in self.last_res:                 # 같은 명령이 또 옴 → 마지막 응답만 다시
            await self.pub(f"res/{NAME}", self.last_res[cmd_id])
            return
        if cmd_id in self.running:
            return
        log(f"cmd {action} {prm or ''}  [{cmd_id[:8]}]")
        if action not in ACTIONS:
            await self.reply(cmd_id, "rejected", error=f"unknown action {action}")
            return
        if action in ("go", "return") and self.moving_cmd:
            await self.reply(cmd_id, "rejected", error="busy")
            return
        # estop은 link가 끊겨 보여도 일단 보내 본다
        if not self.link_ok() and action != "estop":
            await self.reply(cmd_id, "rejected", error="agv_offline")
            return

        self.running.add(cmd_id)
        sid = None
        if action in ("go", "return"):
            self.moving_cmd = cmd_id
        try:
            self.next_id = self.next_id % 9999 + 1      # 1~9999 (0은 중계가 스스로 보내는 것)
            sid = self.next_id
            line = {"c": action, "id": sid}
            if action == "go":
                line["count"] = int(prm.get("count") or 0)
            ack_f, done_f = self.loop.create_future(), self.loop.create_future()
            self.acks[sid], self.dones[sid] = ack_f, done_f
            if not self.send(line):
                await self.reply(cmd_id, "rejected", error="bt_write")
                return

            # 1) ack → accepted (또는 rejected)
            try:
                ack = await asyncio.wait_for(ack_f, ACK_TIMEOUT_SEC)
            except asyncio.TimeoutError:
                await self.reply(cmd_id, "rejected", error="agv_no_ack")
                return
            if not ack.get("ok"):
                await self.reply(cmd_id, "rejected", error=ack.get("err") or "agv_refused")
                return
            await self.reply(cmd_id, "accepted")

            # 2) done → done / failed  (estop·reset은 AGV가 ack와 done을 같이 보내도 됨)
            try:
                done = await asyncio.wait_for(done_f, DONE_TIMEOUT_SEC)
            except asyncio.TimeoutError:
                await self.reply(cmd_id, "failed", error="agv_timeout")
                return
            if done.get("ok"):
                await self.reply(cmd_id, "done")
            else:
                await self.reply(cmd_id, "failed", error=done.get("err") or "agv_failed")
        finally:
            self.acks.pop(sid, None)
            self.dones.pop(sid, None)
            self.running.discard(cmd_id)
            if self.moving_cmd == cmd_id:
                self.moving_cmd = None

    # ---------- AGV 줄 → 서버 ----------
    async def on_line(self, line: str):
        if not line.startswith("{"):
            log(f"(AGV 출력) {line}")              # 펌웨어의 디버그 출력은 그대로 보여 줌
            return
        try:
            m = json.loads(line)
        except ValueError:
            log(f"JSON이 아닌 줄 무시: {line[:80]}")
            return
        if not isinstance(m, dict):
            return
        self.agv_seen = time.monotonic()
        t = m.get("t")
        if self.show and t != "hb":
            log(f"AGV → PC  {line}")
        if t == "hb":
            self.agv_state = m.get("st")
            self.agv_load = bool(m.get("load"))
        elif t in ("ack", "done"):
            fut = (self.acks if t == "ack" else self.dones).get(m.get("id"))
            if fut and not fut.done():
                fut.set_result(m)
        else:
            log(f"알 수 없는 종류 t={t!r}: {line[:80]}")

    # ---------- 주기 작업 ----------
    async def watch(self):
        last = None
        while True:
            self.send({"c": "hb"})                  # AGV 쪽 연결 감시용
            ok = self.link_ok()
            # v4: 포트는 열려 있는데 AGV가 말이 없음 (AGV를 껐다 켜면 이렇게 됨) → 다시 열기
            if self.serial_open and not ok:
                since = max(self.agv_seen, self.opened_at)
                if time.monotonic() - since >= REOPEN_AFTER_SEC:
                    self.reopen(f"AGV 하트비트 {REOPEN_AFTER_SEC:g}초 넘게 없음")
            if ok != last:
                last = ok
                log("AGV 하트비트 수신 중 (link ok)" if ok else "AGV 하트비트 없음 (link offline)")
            await asyncio.sleep(HB_TO_AGV_SEC)

    async def receive(self):
        async for m in self.client.messages:
            if m.topic.value != f"cmd/{NAME}":
                continue
            try:
                cmd = json.loads(to_text(m.payload))
            except ValueError:
                continue
            if isinstance(cmd, dict):
                asyncio.create_task(self.on_cmd(cmd))

    async def handle_lines(self):
        while True:
            line = await self.lines.get()
            try:
                await self.on_line(line)
            except Exception as e:                  # 한 줄 때문에 중계가 죽지 않게
                log(f"줄 처리 오류: {e!r} ({line[:80]})")

    async def run(self):
        self.loop = asyncio.get_running_loop()
        threading.Thread(target=self.reader_thread, daemon=True).start()
        status_topic = f"status/{NAME}"
        while True:
            will = aiomqtt.Will(status_topic, payload="offline", qos=1, retain=True)
            try:
                async with aiomqtt.Client(self.host, self.port, identifier=NAME, keepalive=5, will=will) as c:
                    self.client = c
                    await c.publish(status_topic, "online", qos=1, retain=True)
                    await c.subscribe(f"cmd/{NAME}", qos=1)
                    log(f"브로커 연결됨 ({self.host}:{self.port})")
                    try:
                        async with asyncio.TaskGroup() as tg:
                            tg.create_task(self.receive())
                            tg.create_task(self.handle_lines())
                            tg.create_task(self.watch())
                            tg.create_task(heartbeat_loop(c, NAME, 1.0, self.info))
                    finally:
                        with contextlib.suppress(Exception):
                            await asyncio.wait_for(c.publish(status_topic, "offline", qos=1, retain=True), 1.0)
            except* aiomqtt.MqttError as eg:
                log(f"브로커 연결 실패/끊김: {eg.exceptions[0]} → 3초 뒤 다시")
            self.client = None
            await asyncio.sleep(3)


def list_ports():
    try:
        from serial.tools import list_ports as lp
    except ImportError:
        print("pyserial이 없습니다 → python -m pip install pyserial")
        return
    ports = list(lp.comports())
    if not ports:
        print("보이는 시리얼 포트가 없습니다. AGV-01 블루투스 페어링을 확인하세요.")
    for p in ports:
        print(f"  {p.device:<8} {p.description}")


def parse(argv=None):
    p = argparse.ArgumentParser(description="AGV 중계 (블루투스 ↔ MQTT)")
    p.add_argument("--port", default=SERIAL_PORT, help=f"블루투스 COM 포트 (기본 {SERIAL_PORT})")
    p.add_argument("--baud", type=int, default=BAUD)
    p.add_argument("--host", default=BROKER_HOST, help=f"브로커 IP (기본 {BROKER_HOST})")
    p.add_argument("--show", action="store_true", help="블루투스로 오가는 줄을 모두 표시 (하트비트 제외)")
    p.add_argument("--list", action="store_true", help="시리얼 포트 목록만 보고 끝냄")
    return p.parse_args(argv)


if __name__ == "__main__":
    a = parse()
    if a.list:
        list_ports()
    else:
        try:
            import serial  # noqa: F401
        except ImportError:
            raise SystemExit("pyserial이 없습니다 → python -m pip install pyserial")
        log(f"AGV 중계 시작 (블루투스 {a.port} ↔ 브로커 {a.host}:{BROKER_PORT})")
        run_main(lambda: AgvRelay(SerialLink(a.port, a.baud), a.host, BROKER_PORT, show=a.show).run())
