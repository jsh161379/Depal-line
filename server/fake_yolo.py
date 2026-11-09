"""
가짜 YOLO - 라인 컨트롤러 시험용 (통신 규약 3-2)
================================================
실제 YOLO 프로그램 대신 cmd/yolo 명령(scan_pallet, inspect)에 응답합니다.
YOLO 담당은 "이 형식으로 답하면 된다"는 예제로 보면 됩니다.
  - scan_pallet: 건전지가 있는 칸 목록. 라파가 집어 간 칸(cmd/pi의 pick)은 목록에서 빠짐
  - 회차가 끝나 팔레트가 빈 뒤 start가 오면 새 팔레트를 올린 것처럼 다시 채움
  - inspect: 측면 카메라 외관(normal / scratch / sticker) + 상단 카메라 극 방향(minus_up / plus_up / unknown)
  - 1초마다 hb/yolo 하트비트

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
사용법
  python fake_yolo.py                       팔레트 10칸 가득
  python fake_yolo.py --cells 4             건전지 4개만 (빨리 시험)
  python fake_yolo.py --defect 0.2          외관 불량 확률
  python fake_yolo.py --plus-up 0.1         +극이 위로 보일 확률
  python fake_yolo.py --fail 0.1            검사 실패(신뢰도 낮음) 확률
  python fake_yolo.py --seed 1              매번 같은 결과
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
PALLET_ROWS, PALLET_COLS = 2, 5
TIME_SEC = {"scan_pallet": 0.5, "inspect": 0.8}     # --speed 1 기준
# =================================================

import argparse
import asyncio
import json
import random

from line_common import run_device, run_main


async def amain(args):
    rnd = random.Random(args.seed)
    all_cells = [(r, c) for r in range(PALLET_ROWS) for c in range(PALLET_COLS)]
    pallet = set()

    def refill():
        """새 팔레트를 올림 (회차를 시작할 때마다)"""
        pallet.clear()
        pallet.update(rnd.sample(all_cells, min(args.cells, len(all_cells))))
        print(f"[yolo] 새 팔레트: 건전지 {len(pallet)}개 {sorted(pallet)}", flush=True)

    refill()

    def on_extra(topic, text):
        """라파가 집어 간 칸은 팔레트에서 빼고, start 명령이 오면 새 팔레트를 올림"""
        try:
            cmd = json.loads(text)
        except ValueError:
            return
        if topic == "cmd/pi" and cmd.get("action") == "pick":
            pallet.discard(tuple(cmd.get("params", {}).get("cell", ())))
        elif topic == "ui/cmd" and cmd.get("action") == "start" and not pallet:
            refill()

    async def handler(action, params):
        await asyncio.sleep(TIME_SEC.get(action, 0.5) * args.speed)
        if action == "scan_pallet":
            cells = sorted(pallet)
            return "done", {"cells": [list(x) for x in cells],
                            "conf": [round(rnd.uniform(0.9, 0.99), 2) for _ in cells]}, None
        if action == "inspect":
            if rnd.random() < args.fail:
                return "failed", {}, "low_confidence"
            result = "normal"
            if rnd.random() < args.defect:
                result = rnd.choice(["scratch", "sticker"])
            x = rnd.random()
            polarity = "plus_up" if x < args.plus_up else ("unknown" if x < args.plus_up + 0.1 else "minus_up")
            return "done", {"result": result, "conf": round(rnd.uniform(0.85, 0.98), 2),
                            "polarity": polarity}, None
        return "rejected", {}, f"unknown action {action}"

    await run_device("yolo", handler, host=BROKER_HOST, port=BROKER_PORT,
                     extra_topics=("cmd/pi", "ui/cmd"), on_extra=on_extra,
                     log=lambda s: print(s, flush=True))


def parse(argv=None):
    p = argparse.ArgumentParser(description="가짜 YOLO")
    p.add_argument("--cells", type=int, default=10, help="팔레트 위 건전지 개수 (최대 10)")
    p.add_argument("--speed", type=float, default=0.3, help="동작 시간 배율 (1=실제)")
    p.add_argument("--defect", type=float, default=0.15, help="외관 불량 확률")
    p.add_argument("--plus-up", type=float, default=0.1, help="+극이 위로 보일 확률")
    p.add_argument("--fail", type=float, default=0.0, help="검사 실패 확률")
    p.add_argument("--seed", type=int, default=None, help="난수 시드")
    return p.parse_args(argv)


if __name__ == "__main__":
    a = parse()
    run_main(lambda: amain(a))
