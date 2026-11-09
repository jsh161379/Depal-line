"""
server/results 샘플 발행기 - DB 담당 시험용
============================================
server/results 형식(v1.2)의 샘플 7건을 발행합니다.
DB 저장 프로그램이 battery_cycles / 검사 테이블에 제대로 넣는지 확인할 때 씁니다.

필요한 것: Python 3.11 이상,  pip install "aiomqtt>=2" pydantic
사용법
  python sample_results.py                  새 run_id로 샘플 7건 발행
  python sample_results.py --twice          같은 7건을 두 번 발행 → DB에 중복 저장이 안 되는지 확인
  python sample_results.py --run-id 20261112-093015-a3f9   지정한 회차 번호로 발행
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
TOPIC = "server/results"
SAMPLE_RULE_VERSION = "temp-2026-11-06"
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


class VisionResult(BaseModel):
    status: Literal["ok", "failed"]                 # failed = 카메라/모델 오류로 검사 못 함
    result: str | None                              # normal / scratch / sticker / ... (YOLO 클래스에 따라 추가)
    conf: float | None                              # YOLO 신뢰도 0~1
    polarity: Literal["minus_up", "plus_up", "unknown"] | None   # 상단 카메라로 본 극 방향


class BatteryResult(BaseModel):
    """server/results 메시지 형식 (v1.2). 서버도 이 클래스로 만들어 보낼 예정."""
    run_id: str
    battery_id: int
    pallet_cell: list[int]                          # [행, 열]
    picked_at: str                                  # 로봇팔이 집은 시각
    vision: VisionResult
    electrical_status: Literal["measured", "skipped", "failed"]
    polarity: Literal["normal", "reversed", "abnormal"] | None   # 측정으로 본 극성 (측정 안 했으면 null)
    v_open: float | None                            # 무부하 전압 V (측정 안 했거나 reversed면 null)
    v_load: float | None                            # 부하 전압 V   (polarity normal일 때만)
    r_int: float | None                             # 내부저항 Ω    (polarity normal일 때만)
    grade: Literal["A", "B", "NG", "RESORT"]        # RESORT = 재분류 (불량 아님)
    reason: str | None                              # NG·RESORT·배출 실패 이유
    dest: Literal["AGV", "B_BOX", "NG_BOX", "RESORT_BOX"] | None  # 실제로 넣은 곳 (미완료면 null)
    rule_version: str = SAMPLE_RULE_VERSION      # 등급 판정 기준 버전
    r_load: float | None = None                # 정상 극성에서만 실측 부하 저항 Ω
    finished_at: str                                # 서버가 결과를 확정한 시각


def ts(dt: datetime) -> str:
    return dt.isoformat(sep=" ", timespec="milliseconds")


def make_samples(run_id: str) -> list[BatteryResult]:
    t = datetime.now()
    v_ok = VisionResult(status="ok", result="normal", conf=0.94, polarity="minus_up")
    return [
        # 1) 정상 A: 측정 완료, AGV로 배출
        BatteryResult(run_id=run_id, battery_id=1, pallet_cell=[0, 0], picked_at=ts(t),
                      vision=v_ok, electrical_status="measured", polarity="normal",
                      r_load=3.31, v_open=1.582, v_load=1.491, r_int=0.203,
                      grade="A", reason=None, dest="AGV", finished_at=ts(t + timedelta(seconds=18))),
        # 2) B 등급: 쓰던 건전지
        BatteryResult(run_id=run_id, battery_id=2, pallet_cell=[0, 1], picked_at=ts(t + timedelta(seconds=20)),
                      vision=v_ok, electrical_status="measured", polarity="normal",
                      r_load=3.31, v_open=1.384, v_load=1.142, r_int=0.712,
                      grade="B", reason=None, dest="B_BOX", finished_at=ts(t + timedelta(seconds=38))),
        # 3) 외관 불량(스크래치): 전압 측정 생략 → NG
        BatteryResult(run_id=run_id, battery_id=3, pallet_cell=[0, 2], picked_at=ts(t + timedelta(seconds=40)),
                      vision=VisionResult(status="ok", result="scratch", conf=0.88, polarity="minus_up"),
                      electrical_status="skipped", polarity=None, v_open=None, v_load=None, r_int=None,
                      grade="NG", reason="외관 불량(스크래치)", dest="NG_BOX", finished_at=ts(t + timedelta(seconds=52))),
        # 4) 상단 카메라가 +극 위로 판단: 측정 생략 → 재분류
        BatteryResult(run_id=run_id, battery_id=4, pallet_cell=[0, 3], picked_at=ts(t + timedelta(seconds=55)),
                      vision=VisionResult(status="ok", result="normal", conf=0.93, polarity="plus_up"),
                      electrical_status="skipped", polarity=None, v_open=None, v_load=None, r_int=None,
                      grade="RESORT", reason="+극이 위 (상단 카메라)", dest="RESORT_BOX", finished_at=ts(t + timedelta(seconds=66))),
        # 5) 상단 카메라는 몰랐는데 측정해 보니 거꾸로: 부하 안 걸고 → 재분류
        BatteryResult(run_id=run_id, battery_id=5, pallet_cell=[0, 4], picked_at=ts(t + timedelta(seconds=68)),
                      vision=VisionResult(status="ok", result="normal", conf=0.91, polarity="unknown"),
                      electrical_status="measured", polarity="reversed", v_open=None, v_load=None, r_int=None,
                      grade="RESORT", reason="측정 극성 반대", dest="RESORT_BOX", finished_at=ts(t + timedelta(seconds=86))),
        # 6) 측정 실패 → 재분류
        BatteryResult(run_id=run_id, battery_id=6, pallet_cell=[1, 0], picked_at=ts(t + timedelta(seconds=88)),
                      vision=v_ok, electrical_status="failed", polarity=None, v_open=None, v_load=None, r_int=None,
                      grade="RESORT", reason="전압 측정 실패", dest="RESORT_BOX", finished_at=ts(t + timedelta(seconds=108))),
        # 7) 배출 미완료: 판정은 됐지만 로봇팔이 넣지 못함 → dest null
        BatteryResult(run_id=run_id, battery_id=7, pallet_cell=[1, 1], picked_at=ts(t + timedelta(seconds=110)),
                      vision=v_ok, electrical_status="measured", polarity="normal",
                      r_load=3.31, v_open=1.571, v_load=1.477, r_int=0.215,
                      grade="A", reason="배출 실패(집게 고장)", dest=None, finished_at=ts(t + timedelta(seconds=131))),
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
