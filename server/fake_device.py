"""
가짜 장치 - 로봇팔 1·YOLO·AGV 중계 프로그램 대신 하트비트를 보내는 시험용 프로그램
=================================================================================
실제 장치 없이 중앙 서버의 하트비트 감시를 시험할 때 사용합니다.
로봇팔 담당 팀원에게는 "하트비트를 이렇게 보내고, 서버 하트비트가 끊기면 이렇게 반응하면 된다"는 예제가 됩니다.

필요한 것: Python 3.11 이상,  pip install "aiomqtt>=2"

사용법 (터미널을 장치마다 하나씩 열어서 실행)
  python fake_device.py arm1                 정상 동작
  python fake_device.py yolo --freeze 10    10초 뒤 하트비트만 멈춤 (연결은 유지)  → 서버: 지연 → 끊김
  python fake_device.py agv --crash 10      10초 뒤 강제 종료                       → 서버: LWT로 바로 끊김
  python fake_device.py arm1 --skip 15       15번째 하트비트를 일부러 빼먹음        → 서버: "1개 빠짐" 경고
  Ctrl+C                                    정상 종료 (offline을 직접 알리고 끝냄)
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"     # 브로커가 다른 PC에 있으면 그 PC의 IP (예: "192.168.0.10")
BROKER_PORT = 1883
HB_INTERVAL_SEC = 1.0         # 하트비트 보내는 간격 (서버와 맞출 것)
SERVER_TIMEOUT_SEC = 3.0      # 서버 하트비트가 이 시간 동안 없으면 '서버 끊김'으로 판단
MQTT_KEEPALIVE_SEC = 5
# =================================================

import argparse
import asyncio
import contextlib
import json
import os
import sys
import time

import aiomqtt


async def send_heartbeat(client: aiomqtt.Client, args):
    seq = 0
    start = time.monotonic()
    while True:
        elapsed = time.monotonic() - start
        if args.crash and elapsed >= args.crash:
            print(f"[{args.name}] 강제 종료! (브로커가 LWT로 offline을 대신 알려야 함)", flush=True)
            os._exit(1)          # 정상 종료 절차 없이 바로 끝냄 = 전원이 갑자기 꺼진 상황
        if args.freeze and elapsed >= args.freeze:
            print(f"[{args.name}] 하트비트 멈춤 (연결은 살아 있음 = 프로그램 본체가 멈춘 상황)", flush=True)
            await asyncio.Event().wait()   # 영원히 대기

        seq += 1
        if args.skip and seq == args.skip:
            print(f"[{args.name}] seq {seq} 일부러 건너뜀", flush=True)
        else:
            body = {"device": args.name, "seq": seq, "ts": time.time(), "state": "run"}
            if args.name == "arm1":
                body["gripper"] = "normal"     # 추가 정보 예시 (집게 상태)
            await client.publish(f"hb/{args.name}", json.dumps(body), qos=0)
        await asyncio.sleep(HB_INTERVAL_SEC)


async def watch_server(client: aiomqtt.Client, name: str):
    """서버 하트비트 감시. 실제 장치라면 끊겼을 때 컨베이어를 멈춰야 함."""
    last = time.monotonic()
    server_ok = None

    async def receive():
        nonlocal last
        async for _ in client.messages:     # hb/server 만 구독함
            last = time.monotonic()

    async def check():
        nonlocal server_ok
        while True:
            ok = (time.monotonic() - last) < SERVER_TIMEOUT_SEC
            if ok != server_ok:
                if ok:
                    print(f"[{name}] 서버 하트비트 정상", flush=True)
                else:
                    print(f"[{name}] 서버 하트비트 끊김! → (실제 장치라면 여기서 컨베이어 정지)", flush=True)
                server_ok = ok
            await asyncio.sleep(0.5)

    async with asyncio.TaskGroup() as tg:
        tg.create_task(receive())
        tg.create_task(check())


async def main(args):
    status_topic = f"status/{args.name}"
    will = aiomqtt.Will(status_topic, payload="offline", qos=1, retain=True)
    async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier=args.name,
                              keepalive=MQTT_KEEPALIVE_SEC, will=will) as client:
        await client.publish(status_topic, "online", qos=1, retain=True)
        await client.subscribe("hb/server", qos=0)
        print(f"[{args.name}] 브로커 연결됨 ({BROKER_HOST}:{BROKER_PORT})", flush=True)
        try:
            async with asyncio.TaskGroup() as tg:
                tg.create_task(send_heartbeat(client, args))
                tg.create_task(watch_server(client, args.name))
        finally:
            # 정상 종료 시에는 LWT가 안 나가므로 직접 offline을 알림
            with contextlib.suppress(Exception):
                await asyncio.wait_for(client.publish(status_topic, "offline", qos=1, retain=True), 1.0)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="하트비트 시험용 가짜 장치")
    p.add_argument("name", help="장치 이름 (arm1, yolo, agv 등)")
    p.add_argument("--freeze", type=float, default=0, help="N초 뒤 하트비트만 멈춤")
    p.add_argument("--crash", type=float, default=0, help="N초 뒤 강제 종료")
    p.add_argument("--skip", type=int, default=0, help="N번째 하트비트를 건너뜀")
    args = p.parse_args()
    try:
        if sys.platform.startswith("win"):
            with asyncio.Runner(loop_factory=asyncio.SelectorEventLoop) as runner:
                runner.run(main(args))
        else:
            asyncio.run(main(args))
    except KeyboardInterrupt:
        print(f"[{args.name}] 종료", flush=True)
    except Exception as e:   # 브로커 연결 실패·끊김 (TaskGroup 안에서 나면 묶여서 올라옴)
        print(f"[{args.name}] 브로커 연결 문제로 종료: {e!r}", flush=True)
