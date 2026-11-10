"""
server/results 샘플 발행기 - DB 담당 시험용
============================================
server/results 형식(메시지_형식 v1.4)의 샘플 9건을 발행합니다. 등급 5종이 모두 들어 있습니다.
DB 저장 프로그램이 battery_cycles / 검사 테이블에 제대로 넣는지 확인할 때 씁니다.

필요한 것: Python 3.11 이상,  pip install "aiomqtt>=2" pydantic
사용법
  python sample_results.py                  새 run_id로 샘플 9건 발행
  python sample_results.py --twice          같은 9건을 두 번 발행 → DB에 중복 저장이 안 되는지 확인
  python sample_results.py --run-id 20261112-093015-a3f9   지정한 회차 번호로 발행
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
TOPIC = "server/results"
SAMPLE_RULE_VERSION = "temp-2026-11-10"
# 아래 등급 기준은 임시값 (전압 시험 데이터로 확정 예정)
# ==================================================

import argparse
import asyncio
import secrets
import sys
from datetime import datetime, timedelta
from typing import Literal

import aiomqtt
from pydantic import BaseModel


class Defect(BaseModel):
    type: str                                       # scratch / dent / sticker / leak ... (YOLO 클래스에 따라 추가)
    conf: float | None = None
    cam: Literal["front", "back", "top"] | None = None   # 불량을 찾은 카메라


class VisionResult(BaseModel):
    status: Literal["ok", "failed"]                 # failed = 검사 못 함 또는 결과가 오지 않음
    result: Literal["normal", "defect"] | None
    conf: float | None                              # YOLO 신뢰도 0~1
    polarity: Literal["minus_up", "plus_up", "unknown"] | None   # 카메라로 본 극 방향
    defects: list[Defect] = []                      # result가 defect일 때만


class BatteryResult(BaseModel):
    """server/results 메시지 형식 (메시지_형식 v1.4). 라인 컨트롤러도 같은 형식으로 보냄."""
    run_id: str
    battery_id: int
    pallet_cell: list[int]                          # [행, 열]
    picked_at: str                                  # 로봇팔 1이 집은 시각
    vision: VisionResult
    electrical_status: Literal["measured", "skipped", "failed"]
    polarity: Literal["normal", "reversed", "abnormal"] | None   # 측정으로 본 극성 (측정 안 했으면 null)
    v_open: float | None                            # 무부하 전압 V (측정 안 했거나 reversed면 null)
    v_load: float | None                            # 부하 전압 V   (polarity normal일 때만)
    r_int: float | None                             # 내부저항 Ω    (polarity normal일 때만)
    r_load: float | None = None                     # 부하 저항 Ω   (polarity normal일 때만)
    grade: Literal["A", "B", "NG_VISUAL", "NG_VOLTAGE", "RESORT"]
    reason: str | None                              # 고정 문장 (메시지_형식 v1.4)
    dest: Literal["AGV", "B_BOX", "NG_VISUAL_BOX", "NG_VOLTAGE_BOX", "RESORT_BOX"] | None  # 실제로 넣은 곳 (실패면 null)
    rule_version: str = SAMPLE_RULE_VERSION
    finished_at: str                                # 배출 완료 시각 (battery_id 순서와 다를 수 있음)


def ts(dt: datetime) -> str:
    return dt.isoformat(sep=" ", timespec="milliseconds")


def make_samples(run_id: str) -> list[BatteryResult]:
    t = datetime.now()
    v_ok = VisionResult(status="ok", result="normal", conf=0.94, polarity="minus_up")
    skip = dict(electrical_status="skipped", polarity=None, v_open=None, v_load=None, r_int=None)

    def r(no, cell, sec, **kw):
        return BatteryResult(run_id=run_id, battery_id=no, pallet_cell=cell, picked_at=ts(t + timedelta(seconds=sec)),
                             finished_at=ts(t + timedelta(seconds=sec + 20)), **kw)

    return [
        # 1) A: 측정 완료, AGV
        r(1, [0, 0], 0, vision=v_ok, electrical_status="measured", polarity="normal",
          v_open=1.582, v_load=1.491, r_int=0.203, r_load=3.31, grade="A", reason=None, dest="AGV"),
        # 2) B: 쓰던 건전지
        r(2, [0, 1], 4, vision=v_ok, electrical_status="measured", polarity="normal",
          v_open=1.384, v_load=1.142, r_int=0.712, r_load=3.31, grade="B", reason=None, dest="B_BOX"),
        # 3) 외관 불량 2가지 (카메라 2대): 측정 안 함 → 외관불량함
        r(3, [0, 2], 8, vision=VisionResult(status="ok", result="defect", conf=0.91, polarity="minus_up",
                                           defects=[Defect(type="scratch", conf=0.91, cam="front"),
                                                    Defect(type="dent", conf=0.85, cam="back")]),
          **skip, grade="NG_VISUAL", reason="외관 불량", dest="NG_VISUAL_BOX"),
        # 4) 전압 낮음: 다 쓴 건전지 → 전압불량함
        r(4, [0, 3], 12, vision=v_ok, electrical_status="measured", polarity="normal",
          v_open=1.214, v_load=0.873, r_int=1.302, r_load=3.31, grade="NG_VOLTAGE", reason="전압 낮음",
          dest="NG_VOLTAGE_BOX"),
        # 5) 카메라가 +극 위로 판단: 측정 안 함 → 서보가 재분류함으로
        r(5, [0, 4], 16, vision=VisionResult(status="ok", result="normal", conf=0.93, polarity="plus_up"),
          **skip, grade="RESORT", reason="+극이 위", dest="RESORT_BOX"),
        # 6) 카메라는 몰랐는데 측정해 보니 거꾸로 → 재분류
        r(6, [1, 0], 20, vision=VisionResult(status="ok", result="normal", conf=0.88, polarity="unknown"),
          electrical_status="measured", polarity="reversed", v_open=None, v_load=None, r_int=None,
          grade="RESORT", reason="측정 극성 반대", dest="RESORT_BOX"),
        # 7) 측정 실패(1번 다시 측정해도 실패) → 재분류
        r(7, [1, 1], 24, vision=v_ok, electrical_status="failed", polarity=None, v_open=None, v_load=None,
          r_int=None, grade="RESORT", reason="측정 실패", dest="RESORT_BOX"),
        # 8) YOLO 결과 없이 측정 자리 도착 → 재분류
        r(8, [1, 2], 28, vision=VisionResult(status="failed", result=None, conf=None, polarity=None),
          **skip, grade="RESORT", reason="외관 검사 실패", dest="RESORT_BOX"),
        # 9) 배출 실패: 판정은 A인데 로봇팔 2가 넣지 못함 → dest null
        r(9, [1, 3], 32, vision=v_ok, electrical_status="measured", polarity="normal",
          v_open=1.571, v_load=1.477, r_int=0.215, r_load=3.31, grade="A", reason="배출 실패(ARM2_FAILED)", dest=None),
    ]


async def main(args):
    run_id = args.run_id or (datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + secrets.token_hex(2))
    samples = make_samples(run_id)
    async with aiomqtt.Client(args.broker, args.mqtt_port, identifier=f"sample_results_{secrets.token_hex(2)}") as client:
        for round_no in range(2 if args.twice else 1):
            for r in samples:
                await client.publish(TOPIC, r.model_dump_json(), qos=1)
                print(f"[{round_no + 1}회차 발행] run_id={r.run_id} battery_id={r.battery_id} "
                      f"grade={r.grade} dest={r.dest}", flush=True)
    print("완료", flush=True)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="server/results 샘플 발행")
    p.add_argument("--twice", action="store_true", help="같은 샘플을 두 번 발행 (중복 저장 방지 확인용)")
    p.add_argument("--run-id", default="", help="회차 번호 지정")
    p.add_argument("--broker", default=BROKER_HOST, help="MQTT 브로커 호스트")
    p.add_argument("--mqtt-port", type=int, default=BROKER_PORT, help="MQTT 브로커 포트")
    args = p.parse_args()
    if sys.platform.startswith("win"):
        with asyncio.Runner(loop_factory=asyncio.SelectorEventLoop) as runner:
            runner.run(main(args))
    else:
        asyncio.run(main(args))
