"""
가짜 AGV (장치 이름 agv) - 라인 컨트롤러 시험용 (통신 규약 v1.4, 3-3)
=======================================================================================
실제 AGV(블루투스 ↔ MQTT 중계) 대신 cmd/agv 명령(go, return, estop, reset)에 응답합니다.
AGV 중계 프로그램을 만드는 사람은 "중계가 이렇게 답하면 된다"는 예제로 보면 됩니다.
  - go     {"count": 8}  : A 적재 자리 → 목적지로 가서 내려놓음. 받자마자 accepted, 도착·하역하면 done
  - return {}            : 분류 위치(A 적재 자리)로 복귀. 도착하면 done
  - 1초마다 hb/agv 하트비트 (agv 위치 상태 포함: home / going / away / returning)
  - 서버 하트비트(hb/server)가 3초 끊기면 (실제 AGV라면) 그 자리에 정지

컨트롤러가 AGV에 명령을 보내려면 line_controller.py를 --agv 로 켜야 합니다
(run_all.py는 가짜 장치를 켤 때 자동으로 --agv를 붙임).

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
사용법
  python fake_agv.py                    기본 (--speed 0.3 = 3배 빠르게)
  python fake_agv.py --speed 1          실제 속도
  python fake_agv.py --go-fail 0.2      출발 실패 확률 (컨트롤러 실패 처리 시험)
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
TIME_SEC = {"go": 10.0, "return": 10.0, "reset": 2.0}   # 동작 시간(초, --speed 1 기준, 🟡 실제 AGV 실측 후)
# =================================================

import argparse
import asyncio
import random

from line_common import run_device, run_main


def make_handler(args, rnd: random.Random, state: dict):
    async def handler(action, params):
        if action == "go":
            if state["pos"] != "home":
                return "rejected", {}, f"not_at_home ({state['pos']})"
            state["pos"] = "going"
            print(f"[agv] 출발: A {params.get('count', '?')}개 싣고 목적지로", flush=True)
            await asyncio.sleep(TIME_SEC["go"] * args.speed)
            if rnd.random() < args.go_fail:
                state["pos"] = "away"            # 가다가 멈춘 것으로 봄 → 사람이 확인
                return "failed", {}, "blocked"
            state["pos"] = "away"
            print("[agv] 목적지 도착, 하역 완료", flush=True)
            return "done", {}, None
        if action == "return":
            state["pos"] = "returning"
            await asyncio.sleep(TIME_SEC["return"] * args.speed)
            state["pos"] = "home"
            print("[agv] 분류 위치로 복귀", flush=True)
            return "done", {}, None
        if action == "estop":
            print("[agv] 비상정지: AGV 정지", flush=True)
            return "done", {}, None
        if action == "reset":
            await asyncio.sleep(TIME_SEC["reset"] * args.speed)
            return "done", {}, None
        return "rejected", {}, f"unknown action {action}"

    return handler


async def amain(args):
    rnd = random.Random(args.seed)
    state = {"pos": "home"}
    await run_device("agv", make_handler(args, rnd, state), host=BROKER_HOST, port=BROKER_PORT,
                     info_fn=lambda: {"agv": state["pos"]},
                     on_server_lost=lambda: print("[agv] → (실제 AGV라면 여기서 그 자리에 정지)", flush=True),
                     log=lambda s: print(s, flush=True))


def parse(argv=None):
    p = argparse.ArgumentParser(description="가짜 AGV (agv)")
    p.add_argument("--speed", type=float, default=0.3, help="동작 시간 배율 (1=실제)")
    p.add_argument("--go-fail", type=float, default=0.0, help="출발 실패 확률")
    p.add_argument("--seed", type=int, default=None, help="난수 시드 (같은 결과 반복)")
    return p.parse_args(argv)


if __name__ == "__main__":
    a = parse()
    run_main(lambda: amain(a))
