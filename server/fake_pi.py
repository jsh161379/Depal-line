"""
가짜 라즈베리파이 (로봇팔 1, myCobot 280-PI) - 라인 컨트롤러 시험용 (통신 규약 v1.2, 3-1)
=======================================================================================
실제 라파 대신 cmd/pi 명령(pick, estop, reset)에 응답합니다.
하드웨어 담당은 "라파가 이렇게 답하면 된다"는 예제로 보면 됩니다.
  - pick: 팔레트 칸의 퍽을 통째로 집어 벨트 시작점에 올림. 받자마자 accepted, 끝나면 done / failed
  - 1초마다 hb/pi 하트비트 (arm 상태 포함)
  - 측정·분류·센서·비상정지 버튼은 v1.2부터 Mega 쪽 (fake_mega.py)

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
사용법
  python fake_pi.py                         기본 (--speed 0.3 = 3배 빠르게)
  python fake_pi.py --speed 1               실제 속도
  python fake_pi.py --pick-fail 0.1         집기 실패 확률 (일시정지 시험)
  python fake_pi.py --seed 1                매번 같은 결과
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
TIME_SEC = {"pick": 3.0, "reset": 2.0}   # 동작 시간(초, --speed 1 기준)
# =================================================

import argparse
import asyncio
import random

from line_common import run_device, run_main


def make_handler(args, rnd: random.Random):
    async def handler(action, params):
        if action == "pick":
            await asyncio.sleep(TIME_SEC["pick"] * args.speed)
            if rnd.random() < args.pick_fail:
                return "failed", {}, "grip_failed"
            return "done", {}, None
        if action == "estop":
            print("[pi] 비상정지: 로봇팔 1 정지", flush=True)
            return "done", {}, None
        if action == "reset":
            await asyncio.sleep(TIME_SEC["reset"] * args.speed)
            return "done", {}, None
        return "rejected", {}, f"unknown action {action}"

    return handler


async def amain(args):
    rnd = random.Random(args.seed)
    await run_device("pi", make_handler(args, rnd), host=BROKER_HOST, port=BROKER_PORT,
                     info_fn=lambda: {"arm": "ok"},
                     on_server_lost=lambda: print("[pi] → (실제 라파라면 여기서 팔 정지)", flush=True),
                     log=lambda s: print(s, flush=True))


def parse(argv=None):
    p = argparse.ArgumentParser(description="가짜 라즈베리파이 (로봇팔 1)")
    p.add_argument("--speed", type=float, default=0.3, help="동작 시간 배율 (1=실제)")
    p.add_argument("--pick-fail", type=float, default=0.0, help="집기 실패 확률")
    p.add_argument("--seed", type=int, default=None, help="난수 시드 (같은 결과 반복)")
    return p.parse_args(argv)


if __name__ == "__main__":
    a = parse()
    run_main(lambda: amain(a))
