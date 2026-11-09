"""
라인 명령 보내기 - 메인 프로그램이 생기기 전까지 버튼 대신 사용
==============================================================
사용법
  python line_cmd.py start     회차 시작
  python line_cmd.py pause     지금 건전지를 끝낸 뒤 일시정지
  python line_cmd.py resume    재개 (실패로 멈췄을 때는 그 단계를 다시 시도)
  python line_cmd.py skip      실패로 멈췄을 때 그 건전지를 건너뜀
  python line_cmd.py stop      회차 종료 (지금 건전지를 끝낸 뒤)
  python line_cmd.py estop     비상정지 (즉시)
  python line_cmd.py reset     비상정지 해제

필요한 것: pip install "aiomqtt>=2", 같은 폴더에 line_common.py
"""

BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883

import argparse
import json

import aiomqtt

from line_common import now_str, run_main

ACTIONS = ["start", "pause", "resume", "skip", "stop", "estop", "reset"]


async def send(action):
    async with aiomqtt.Client(BROKER_HOST, BROKER_PORT) as c:
        await c.publish("ui/cmd", json.dumps({"action": action, "ts": now_str()}), qos=1)
    print(f"보냄: {action}", flush=True)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="라인 명령 보내기")
    p.add_argument("action", choices=ACTIONS)
    a = p.parse_args()
    run_main(lambda: send(a.action))
