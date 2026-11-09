"""
가짜 라즈베리파이 - 라인 컨트롤러 시험용 (통신 규약 3-1)
=========================================================
실제 라파 대신 cmd/pi 명령(pick, move, measure, sort, estop, reset)에 응답합니다.
하드웨어 담당은 "라파가 이렇게 답하면 된다"는 예제로 보면 됩니다.
  - 받자마자 accepted, 동작 시간이 지나면 done / failed
  - 측정값은 새것·쓰던 것·다 쓴 것을 섞어서 만들고, 가끔 거꾸로(reversed)·접촉 불량(abnormal)을 냄
  - 1초마다 hb/pi 하트비트 (gripper, arm, arduino 상태 포함)
  - 서버 하트비트가 끊기면 "컨베이어 정지" 표시
  - 환경 센서(가스·온도·습도) 값을 sensor/env로 주기 발행
  - 비상정지 버튼(아두이노에 연결)을 누른 것처럼 event/estop 발행 (--estop-at)

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2" pydantic, 같은 폴더에 line_common.py
사용법
  python fake_pi.py                         기본
  python fake_pi.py --speed 1               실제 속도 (기본 0.3 = 3배 빠르게)
  python fake_pi.py --reversed 0.2          측정 시 거꾸로 나올 확률
  python fake_pi.py --pick-fail 0.1         집기 실패 확률 (일시정지 시험)
  python fake_pi.py --seed 1                매번 같은 결과
  python fake_pi.py --estop-at 15           15초 뒤 비상정지 버튼 누름, 5초 뒤 복귀 (--estop-hold로 조절)
  python fake_pi.py --env-interval 0        환경 센서 발행 끄기 (기본 5초마다)
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
R_LOAD = 3.31                 # 부하 저항 실측값(Ω)이라고 가정
ESTOP_BUTTON_ID = "estop_1"   # 비상정지 버튼 이름
# 환경 센서 (가스: MQ-9, ADC 원시값 → 단위 raw. 온습도 모델은 확정 전)
ENV_SENSORS = [("gas_1", "GAS", "raw", 180.0, 20.0),          # (센서, 측정 종류, 단위, 기준값, 흔들림)
               ("temp_hum_1", "TEMPERATURE", "C", 24.0, 0.6),
               ("temp_hum_1", "HUMIDITY", "%", 40.0, 2.0)]
# 동작 시간(초, --speed 1 기준)
TIME_SEC = {"pick": 3.0, "move": 2.0, "measure": 1.0, "sort": 3.0, "reset": 2.0}
# =================================================

import argparse
import asyncio
import random

from line_common import env_body, estop_body, run_device, run_main


def make_handler(args, rnd: random.Random):
    async def wait(action):
        await asyncio.sleep(TIME_SEC.get(action, 0.5) * args.speed)

    def sample_battery():
        """새것 / 쓰던 것 / 다 쓴 것 중 하나의 무부하 전압·내부저항"""
        kind = rnd.choices(["new", "used", "dead"], weights=[5, 3, 2])[0]
        if kind == "new":
            return rnd.uniform(1.55, 1.62), rnd.uniform(0.12, 0.30)
        if kind == "used":
            return rnd.uniform(1.33, 1.48), rnd.uniform(0.40, 0.90)
        return rnd.uniform(1.00, 1.28), rnd.uniform(1.2, 3.0)

    async def handler(action, params):
        if action == "pick":
            await wait(action)
            if rnd.random() < args.pick_fail:
                return "failed", {}, "grip_failed"
            return "done", {"method": "gripper"}, None
        if action == "move":
            await wait(action)
            return "done", {"pos": params.get("to")}, None
        if action == "measure":
            await wait(action)
            if rnd.random() < args.adc_fail:
                return "failed", {}, "adc_error"
            x = rnd.random()
            if x < args.reversed:          # 거꾸로: 부하 안 걸고 바로 응답, 전압 null
                return "done", {"polarity": "reversed", "v_open": None, "v_load": None,
                                "r_load": R_LOAD, "r_int": None}, None
            if x < args.reversed + args.abnormal:
                return "done", {"polarity": "abnormal", "v_open": round(rnd.uniform(-0.05, 0.05), 3),
                                "v_load": None, "r_load": R_LOAD, "r_int": None}, None
            v, r = sample_battery()
            v_load = v * R_LOAD / (R_LOAD + r)
            return "done", {"polarity": "normal", "v_open": round(v, 3), "v_load": round(v_load, 3),
                            "r_load": R_LOAD, "r_int": round(r, 3)}, None
        if action == "sort":
            await wait(action)
            return "done", {"dest": params.get("dest")}, None
        if action == "estop":
            print("[pi] 비상정지: 컨베이어 전원 차단, 로봇팔 정지", flush=True)
            return "done", {}, None
        if action == "reset":
            await wait(action)
            return "done", {}, None
        return "rejected", {}, f"unknown action {action}"

    return handler


async def amain(args):
    rnd = random.Random(args.seed)
    state = {"run_id": None}       # 받은 명령의 회차 번호 (센서·비상정지 메시지에 붙임)

    async def env_loop(client):
        """환경 센서: 주기마다 sensor/env 발행"""
        if args.env_interval <= 0:
            return
        while True:
            readings = [{"sensor": s, "metric": m, "unit": u,
                         "value": round(base + rnd.uniform(-d, d)) if u == "raw" else round(base + rnd.uniform(-d, d), 2)}
                        for s, m, u, base, d in ENV_SENSORS]
            await client.publish("sensor/env", env_body(state["run_id"], "pi", readings), qos=1)
            await asyncio.sleep(args.env_interval)

    async def estop_button(client):
        """비상정지 버튼 흉내: 실제로는 아두이노가 먼저 컨베이어를 끊고, 라파는 알림만 보냄"""
        if args.estop_at <= 0:
            return
        await asyncio.sleep(args.estop_at)
        print("[pi] 비상정지 버튼 눌림 → (실제: 아두이노가 컨베이어 즉시 정지)", flush=True)
        await client.publish("event/estop", estop_body(state["run_id"], "pi", ESTOP_BUTTON_ID,
                                                       "PHYSICAL_BUTTON", "BUTTON_PRESSED"), qos=1)
        await asyncio.sleep(args.estop_hold)
        print("[pi] 비상정지 버튼 복귀", flush=True)
        await client.publish("event/estop", estop_body(state["run_id"], "pi", ESTOP_BUTTON_ID,
                                                       "PHYSICAL_BUTTON", "BUTTON_RELEASED"), qos=1)

    await run_device("pi", make_handler(args, rnd), host=BROKER_HOST, port=BROKER_PORT,
                     info_fn=lambda: {"gripper": "normal", "arm": "ok", "arduino": "ok"},
                     extra_tasks=(env_loop, estop_button), state=state,
                     on_server_lost=lambda: print("[pi] → (실제 라파라면 여기서 컨베이어 정지)", flush=True),
                     log=lambda s: print(s, flush=True))


def parse(argv=None):
    p = argparse.ArgumentParser(description="가짜 라즈베리파이")
    p.add_argument("--speed", type=float, default=0.3, help="동작 시간 배율 (1=실제)")
    p.add_argument("--reversed", type=float, default=0.1, help="측정 시 거꾸로 확률")
    p.add_argument("--abnormal", type=float, default=0.05, help="측정 시 접촉 불량 확률")
    p.add_argument("--adc-fail", type=float, default=0.0, help="측정 실패 확률")
    p.add_argument("--pick-fail", type=float, default=0.0, help="집기 실패 확률")
    p.add_argument("--seed", type=int, default=None, help="난수 시드 (같은 결과 반복)")
    p.add_argument("--env-interval", type=float, default=5.0, help="환경 센서 발행 간격(초), 0이면 끔")
    p.add_argument("--estop-at", type=float, default=0, help="N초 뒤 비상정지 버튼 누름 (0이면 안 함)")
    p.add_argument("--estop-hold", type=float, default=5.0, help="버튼을 누르고 있는 시간(초)")
    return p.parse_args(argv)


if __name__ == "__main__":
    a = parse()
    run_main(lambda: amain(a))
