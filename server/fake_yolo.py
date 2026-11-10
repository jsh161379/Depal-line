"""
가짜 YOLO - 라인 컨트롤러 시험용 (통신 규약 v1.2, 3-2)
======================================================
실제 YOLO 프로그램 대신 아래 두 가지를 합니다. YOLO 담당은 "이 형식으로 보내면 된다"는 예제로 보면 됩니다.
  - cmd/yolo scan_pallet: 건전지가 있는 칸 목록. 로봇팔 1이 집어 간 칸(cmd/pi pick)은 목록에서 빠짐
    회차가 끝나 팔레트가 빈 뒤 start가 오면 새 팔레트를 올린 것처럼 다시 채움
  - vision/result: 퍽이 촬영 부스를 지나면 결과를 스스로 보냄 (명령 없음)
      외관 normal / defect(+ defects 목록: scratch, dent, sticker, leak), 카메라 front·back·top
      극 방향 minus_up / plus_up / unknown, 가끔 검사 실패(status failed)나 결과 빠짐(--miss)
  - 1초마다 hb/yolo 하트비트

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
사용법
  python fake_yolo.py                       팔레트 10칸 가득
  python fake_yolo.py --cells 4             건전지 4개만 (빨리 시험)
  python fake_yolo.py --defect 0.2          외관 불량 확률
  python fake_yolo.py --plus-up 0.1         +극이 위로 보일 확률
  python fake_yolo.py --fail 0.1            검사 실패 확률
  python fake_yolo.py --miss 0.1            결과를 아예 안 보낼 확률 (WAIT_PLAN 시험)
  python fake_yolo.py --seed 1              매번 같은 결과
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
PALLET_ROWS, PALLET_COLS = 2, 5
BOOTH_SEC = 3.0                  # 벨트에 올린 뒤 촬영 부스를 지나 결과가 나올 때까지 (--speed 1 기준) 🟡
TIME_SEC = {"scan_pallet": 0.5}
DEFECT_TYPES = ["scratch", "dent", "sticker", "leak"]
# =================================================

import argparse
import asyncio
import contextlib
import json
import random

import aiomqtt

from line_common import now_str, run_device, run_main


async def amain(args):
    rnd = random.Random(args.seed)
    all_cells = [(r, c) for r in range(PALLET_ROWS) for c in range(PALLET_COLS)]
    pallet = set()
    pick_cmds: dict[str, int] = {}          # cmd/pi pick의 cmd_id → battery_id
    out = {"client": None, "seq": 0}

    def refill():
        pallet.clear()
        pallet.update(rnd.sample(all_cells, min(args.cells, len(all_cells))))
        print(f"[yolo] 새 팔레트: 건전지 {len(pallet)}개 {sorted(pallet)}", flush=True)

    refill()

    def make_result():
        """퍽 1개의 결과 (vision/result 형식)"""
        out["seq"] += 1
        body = {"seq": out["seq"], "ts": now_str()}
        if rnd.random() < args.fail:
            return body | {"status": "failed", "result": None, "conf": None, "polarity": None, "defects": []}
        x = rnd.random()
        polarity = "plus_up" if x < args.plus_up else ("unknown" if x < args.plus_up + 0.1 else "minus_up")
        if rnd.random() < args.defect:
            defects = [{"type": t, "conf": round(rnd.uniform(0.75, 0.97), 2), "cam": rnd.choice(["front", "back"])}
                       for t in rnd.sample(DEFECT_TYPES, rnd.choice([1, 1, 2]))]
            return body | {"status": "ok", "result": "defect", "conf": max(d["conf"] for d in defects),
                           "polarity": polarity, "defects": defects}
        return body | {"status": "ok", "result": "normal", "conf": round(rnd.uniform(0.85, 0.98), 2),
                       "polarity": polarity, "defects": []}

    async def send_later():
        await asyncio.sleep(BOOTH_SEC * args.speed)
        if rnd.random() < args.miss:
            out["seq"] += 1          # 놓친 결과도 번호는 씀 → 서버가 번호 건너뜀 경고
            print("[yolo] (시험) 이 퍽 결과를 보내지 않음", flush=True)
            return
        r = make_result()
        print(f"[yolo] vision/result seq {r['seq']}: {r['status']} {r.get('result')} {r.get('polarity')}"
              + (f" {[d['type'] for d in r['defects']]}" if r.get("defects") else ""), flush=True)
        if out["client"] is not None:
            with contextlib.suppress(aiomqtt.MqttError):
                await out["client"].publish("vision/result", json.dumps(r, ensure_ascii=False), qos=1)

    def on_extra(topic, text):
        try:
            msg = json.loads(text)
        except ValueError:
            return
        if not isinstance(msg, dict):
            return
        if topic == "cmd/pi" and msg.get("action") == "pick":
            pallet.discard(tuple((msg.get("params") or {}).get("cell", ())))
            pick_cmds[msg.get("cmd_id")] = msg.get("battery_id")
        elif topic == "res/pi" and msg.get("status") == "done" and msg.get("cmd_id") in pick_cmds:
            pick_cmds.pop(msg["cmd_id"])
            asyncio.get_running_loop().create_task(send_later())     # 퍽이 벨트에 올라감 → 곧 부스 통과
        elif topic == "res/pi" and msg.get("status") in ("failed", "rejected"):
            pick_cmds.pop(msg.get("cmd_id"), None)
        elif topic == "ui/cmd" and msg.get("action") == "start" and not pallet:
            refill()

    async def keep_client(client):
        out["client"] = client
        await asyncio.Event().wait()

    async def handler(action, params):
        await asyncio.sleep(TIME_SEC.get(action, 0.5) * args.speed)
        if action == "scan_pallet":
            cells = sorted(pallet)
            return "done", {"cells": [list(x) for x in cells],
                            "conf": [round(rnd.uniform(0.9, 0.99), 2) for _ in cells]}, None
        return "rejected", {}, f"unknown action {action}"

    await run_device("yolo", handler, host=BROKER_HOST, port=BROKER_PORT,
                     extra_topics=("cmd/pi", "res/pi", "ui/cmd"), on_extra=on_extra,
                     extra_tasks=(keep_client,), log=lambda s: print(s, flush=True))


def parse(argv=None):
    p = argparse.ArgumentParser(description="가짜 YOLO")
    p.add_argument("--cells", type=int, default=10, help="팔레트 위 건전지 개수 (최대 10)")
    p.add_argument("--speed", type=float, default=0.3, help="동작 시간 배율 (1=실제)")
    p.add_argument("--defect", type=float, default=0.15, help="외관 불량 확률")
    p.add_argument("--plus-up", type=float, default=0.1, help="+극이 위로 보일 확률")
    p.add_argument("--fail", type=float, default=0.0, help="검사 실패 확률")
    p.add_argument("--miss", type=float, default=0.0, help="결과를 보내지 않을 확률")
    p.add_argument("--seed", type=int, default=None, help="난수 시드")
    return p.parse_args(argv)


if __name__ == "__main__":
    a = parse()
    run_main(lambda: amain(a))
