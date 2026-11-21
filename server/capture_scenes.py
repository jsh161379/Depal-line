"""
포트폴리오 캡처 장면 자동 기록기
================================
장면 3개를 자동으로 만들고, 모니터 화면 출력을 색 정보와 함께 로그 파일로 저장합니다.
이 로그로 선명한 캡처 이미지를 만듭니다 (화면 캡처보다 글자가 깨끗함).

  05 정상 회차      : 건전지 6개(팔레트 한 판) 처리 → 회차 종료
  06 집기 실패      : 일시정지 → resume(다시 시도) → skip(건너뛰기) → 회차 종료
  07 비상정지 버튼  : 운전 중 버튼 눌림 → 정지 확인 → 눌린 채 reset 거부 → 버튼 복귀 → 해제 확인

준비
  1. start_all.bat --no-fake --no-db 로 브로커·서버·컨트롤러를 켜 둔다
  2. 가짜 로봇팔·가짜 YOLO는 켜지 않는다 (이 프로그램이 직접 켜고 끔)
실행: python capture_scenes.py           (약 3분)
결과: captures\\logs\\scene_05.log, scene_06.log, scene_07.log
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
LOG_DIR = "captures/logs"
# =================================================

import asyncio
import json
import os
import subprocess
import sys
import time

import aiomqtt

from line_common import now_str, run_main

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable


class Watch:
    """server/line, server/devices, event/estop 을 계속 받아 두는 감시자"""

    def __init__(self):
        self.mode = None
        self.step = None
        self.devices = {}
        self.estop = []

    async def run(self, client):
        async for m in client.messages:
            try:
                d = json.loads(m.payload)
            except (ValueError, TypeError):
                continue
            t = m.topic.value
            if t == "server/line":
                self.mode, self.step = d.get("mode"), d.get("step")
            elif t == "server/devices":
                self.devices = {x["name"]: x["status"] for x in d.get("devices", [])}
            elif t == "event/estop":
                self.estop.append(d.get("kind"))

    async def until(self, cond, timeout, what):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if cond():
                return True
            await asyncio.sleep(0.2)
        print(f"  ! 시간 초과: {what} (mode={self.mode}, step={self.step})", flush=True)
        return False


def spawn(*args):
    """다른 프로그램을 창 없이 실행. 출력 인코딩을 UTF-8로 고정
    (고정하지 않으면 Windows 기본값 cp949가 '—' 같은 기호를 못 써서 프로그램이 죽음)"""
    env = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    return subprocess.Popen([PY, os.path.join(HERE, args[0]), *args[1:]], cwd=HERE, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def kill(*procs):
    for p in procs:
        if p and p.poll() is None:
            p.terminate()
            try:
                p.wait(timeout=5)
            except subprocess.TimeoutExpired:
                p.kill()


async def ui(client, action):
    await client.publish("ui/cmd", json.dumps({"action": action, "ts": now_str()}), qos=1)
    print(f"  → {action}", flush=True)


async def scene(client, w: Watch, no, title, arm1_args, yolo_args, mega_args, body):
    print(f"\n━━━ 장면 {no}. {title} ━━━", flush=True)
    path = os.path.join(HERE, LOG_DIR, f"scene_{no}.log")
    if os.path.exists(path):
        os.remove(path)
    mon = spawn("monitor.py", "--log", path)
    await asyncio.sleep(1.5)
    arm1 = spawn("fake_arm1.py", *arm1_args)
    yolo = spawn("fake_yolo.py", *yolo_args)
    mega = spawn("fake_mega.py", *mega_args)
    try:
        ok = await w.until(lambda: all(w.devices.get(d) == "ok" for d in ("arm1", "mega", "yolo")), 15, "장치 연결")
        ok = ok and await w.until(lambda: w.mode == "idle", 10, "라인 대기 상태")
        if ok:
            await body()
        await asyncio.sleep(1.5)
    finally:
        kill(arm1, yolo, mega)
        await asyncio.sleep(1.0)
        kill(mon)
    print(f"  저장: {os.path.relpath(path, HERE)}", flush=True)


async def main():
    os.makedirs(os.path.join(HERE, LOG_DIR), exist_ok=True)
    w = Watch()
    async with aiomqtt.Client(BROKER_HOST, BROKER_PORT) as client:
        for t in ("server/line", "server/devices", "event/estop"):
            await client.subscribe(t, qos=1)
        watcher = asyncio.create_task(w.run(client))
        await asyncio.sleep(1)
        if w.mode is None:
            print("라인 컨트롤러 상태를 받지 못했습니다. 3 CONTROLLER 창이 켜져 있는지 확인하세요.")
            return
        if w.mode == "estop":
            await ui(client, "reset")
            await w.until(lambda: w.mode == "idle", 10, "비상정지 해제")

        # 05 정상 회차
        async def s05():
            await ui(client, "start")
            await w.until(lambda: w.mode == "running", 5, "운전 시작")
            await w.until(lambda: w.mode == "idle", 120, "회차 종료")
        await scene(client, w, "05", "정상 회차", ["--seed", "3"], ["--seed", "3"], ["--seed", "3", "--env-interval", "0"], s05)

        # 06 집기 실패 → resume → skip
        async def s06():
            await ui(client, "start")
            await w.until(lambda: w.mode == "paused", 60, "첫 집기 실패")
            await asyncio.sleep(2)
            await ui(client, "resume")
            await asyncio.sleep(1)
            await w.until(lambda: w.mode == "paused", 60, "다시 일시정지")
            await asyncio.sleep(2)
            await ui(client, "skip")
            await asyncio.sleep(4)
            await ui(client, "stop")
            for _ in range(3):
                if await w.until(lambda: w.mode in ("idle", "paused"), 60, "종료"):
                    if w.mode == "paused":
                        await ui(client, "stop")
                        continue
                    break
        await scene(client, w, "06", "집기 실패", ["--seed", "2", "--pick-fail", "0.6"],
                    ["--seed", "2"], ["--seed", "2", "--env-interval", "0"], s06)

        # 07 비상정지 버튼
        async def s07():
            await ui(client, "start")
            await w.until(lambda: "BUTTON_PRESSED" in w.estop, 60, "버튼 눌림")
            await w.until(lambda: "STOP_CONFIRMED" in w.estop, 10, "정지 확인")
            await asyncio.sleep(2)
            await ui(client, "reset")                # 눌린 채 → 거부되어야 함
            await w.until(lambda: "BUTTON_RELEASED" in w.estop, 30, "버튼 복귀")
            await asyncio.sleep(1.5)
            await ui(client, "reset")
            await w.until(lambda: w.mode == "idle", 30, "해제")
        w.estop.clear()
        await scene(client, w, "07", "비상정지 버튼",
                    ["--seed", "4"], ["--seed", "4"],
                    ["--seed", "4", "--estop-at", "14", "--estop-hold", "6", "--env-interval", "0"], s07)

        watcher.cancel()
    print("\n끝났습니다. captures\\logs 폴더에 로그 3개가 있습니다.", flush=True)


if __name__ == "__main__":
    run_main(main)
