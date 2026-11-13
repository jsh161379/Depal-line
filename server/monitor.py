"""
라인 모니터 (콘솔용) - 캡처·시연·디버깅용
==========================================
mosquitto_sub 대신 쓰는 프로그램입니다. 서버가 보내는 메시지를 사람이 읽기 좋게 색을 넣어 보여 줍니다.
  - 장치 상태가 바뀌면: 장치 상태 한 줄 요약 (정상 초록 / 지연 노랑 / 끊김 빨강)
  - 이벤트: 시각, 등급(색), 장치, 내용
  - 결과: 건전지 번호, 등급(색), 보낸 곳, 이유, 전압·내부저항
  - 비상정지(event/estop) 항상 표시, 환경 센서(sensor/env)는 --env일 때 표시
  - 라인 현황(server/line): 운전 상태, 벨트 위 퍽 번호, 등급별 개수(5종), AGV 적재 수 (라인 컨트롤러가 있을 때)

필요한 것: Python 3.11 이상,  pip install "aiomqtt>=2"
실행:
  python monitor.py            보기 좋게 출력
  python monitor.py --json     결과 메시지 원본 JSON도 들여쓰기해서 같이 출력 (통신 규약 설명용 캡처)
끄기: Ctrl+C
※ chcp 65001 없이도 한글이 보입니다.
"""

# ===================== 설정값 =====================
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
# =================================================

import argparse
import asyncio
import json
import os
import sys

import aiomqtt

# ---- 색 (Windows cmd에서도 동작하도록 켜 줌) ----
os.system("")
RESET, BOLD, DIM = "\033[0m", "\033[1m", "\033[2m"
GREEN, YELLOW, RED, CYAN, MAGENTA, GRAY = "\033[92m", "\033[93m", "\033[91m", "\033[96m", "\033[95m", "\033[90m"

_log_file = None
_builtin_print = print


def print(*args, **kwargs):  # noqa: A001 - 화면 출력과 함께 로그 파일에도 저장 (--log)
    if _log_file is not None:              # 로그를 먼저 저장 (화면 출력이 실패해도 기록은 남게)
        _log_file.write(" ".join(str(x) for x in args) + "\n")
        _log_file.flush()
    try:
        _builtin_print(*args, **kwargs)
    except UnicodeEncodeError:             # 화면이 표시 못 하는 글자(— 등)는 ?로 바꿔 출력
        enc = sys.stdout.encoding or "utf-8"
        _builtin_print(*(str(x).encode(enc, "replace").decode(enc) for x in args), **kwargs)


LEVEL_COLOR = {"INFO": GREEN, "WARN": YELLOW, "ERROR": RED}
STATUS_VIEW = {"ok": (GREEN, "정상"), "warn": (YELLOW, "지연"), "offline": (RED, "끊김"), "unknown": (GRAY, "확인 전")}
GRADE_COLOR = {"A": GREEN, "B": CYAN, "NG_VISUAL": RED, "NG_VOLTAGE": RED, "RESORT": MAGENTA}
DEST_KR = {"AGV": "AGV", "B_BOX": "B함", "NG_VISUAL_BOX": "외관불량함", "NG_VOLTAGE_BOX": "전압불량함",
           "RESORT_BOX": "재분류함", None: "배출 안 됨"}


def short_time(ts: str | None) -> str:
    """'2026-11-06 14:22:20.450' → '14:22:20.450'"""
    return ts.split(" ")[-1] if ts else "--:--:--"


def show_event(e: dict):
    lv = e.get("level", "?")
    c = LEVEL_COLOR.get(lv, "")
    print(f"{GRAY}{short_time(e.get('ts'))}{RESET}  {c}{BOLD}{lv:<5}{RESET}  "
          f"{BOLD}{e.get('device', '?'):<6}{RESET}  {c}{e.get('message', '')}{RESET}", flush=True)


last_devices = None


def show_devices(d: dict):
    global last_devices
    parts = []
    for dev in d.get("devices", []):
        c, kr = STATUS_VIEW.get(dev.get("status"), (GRAY, dev.get("status")))
        name = dev.get('name')
        user = (dev.get('info') or {}).get('user')    # 관리자 화면은 로그인한 사람 이름을 하트비트에 실어 보냄 (규약 v1.4)
        if user:
            name = f"{name}({user})"
        parts.append(f"{BOLD}{name}{RESET} {c}● {kr}{RESET}")
    line = "   ".join(parts)
    if line != last_devices:       # 바뀌었을 때만 출력
        last_devices = line
        print(f"{GRAY}{short_time(d.get('ts'))}{RESET}  {CYAN}{BOLD}[장치]{RESET} {line}", flush=True)


MODE_VIEW = {"idle": (GRAY, "대기"), "running": (GREEN, "운전"), "paused": (YELLOW, "일시정지"),
             "draining": (CYAN, "마무리"), "estop": (RED, "비상정지")}
STEP_KR = {"idle": "-", "running": "운전 중", "scan": "팔레트 인식", "pick": "퍽 올리기", "paused": "일시정지",
           "draining": "벨트 비우는 중", "wait_operator": "작업자 확인 대기", "estop": "비상정지", "reset": "해제 중"}
last_line = None


STATION_KR = {"inspect": "검사", "measure": "측정", "sort": "분류"}


def show_line(d: dict):
    """server/line: 라인 현황 (바뀔 때만 출력)"""
    global last_line
    c = d.get("counts") or {}
    agv = d.get("agv") or {}
    on_belt = d.get("on_belt") or []
    st = d.get("stations") or {}
    key = (d.get("mode"), d.get("step"), tuple(on_belt), json.dumps(st, sort_keys=True),
           tuple(sorted(c.items())), d.get("message"))
    if key == last_line:
        return
    last_line = key
    mc, mk = MODE_VIEW.get(d.get("mode"), (GRAY, d.get("mode")))
    # 공정별 퍽 번호 (v1.4.1 stations). 없으면 예전처럼 번호만
    if st:
        belt = " ".join(f"{STATION_KR.get(k, k)} " + ",".join(f"#{n}" for n in st[k])
                        for k in ("inspect", "measure", "sort") if st.get(k))
    else:
        belt = ("벨트 " + ",".join(f"#{n}" for n in on_belt)) if on_belt else "벨트 비었음"
    print(f"{GRAY}{short_time(d.get('ts'))}{RESET}  {MAGENTA}{BOLD}[라인]{RESET} {mc}{BOLD}{mk:<4}{RESET} "
          f"{STEP_KR.get(d.get('step'), d.get('step')):<8} {belt:<12}  "
          f"{GREEN}A {c.get('A', 0)}{RESET} {CYAN}B {c.get('B', 0)}{RESET} "
          f"{RED}외관 {c.get('NG_VISUAL', 0)} 전압 {c.get('NG_VOLTAGE', 0)}{RESET} "
          f"{MAGENTA}재분류 {c.get('RESORT', 0)}{RESET}  AGV {agv.get('loaded', 0)}/{agv.get('batch', '-')}"
          + (f"  {YELLOW}{d['message']}{RESET}" if d.get("message") else ""), flush=True)


ESTOP_KR = {"BUTTON_PRESSED": "버튼 눌림", "BUTTON_RELEASED": "버튼 복귀",
            "STOP_CONFIRMED": "정지 확인", "RESET_CONFIRMED": "해제 확인"}


def show_estop(e: dict):
    """event/estop: 비상정지"""
    kind = e.get("kind")
    c = GREEN if kind in ("BUTTON_RELEASED", "RESET_CONFIRMED") else RED
    print(f"{GRAY}{short_time(e.get('ts'))}{RESET}  {c}{BOLD}[비상정지]{RESET} {c}{ESTOP_KR.get(kind, kind)}{RESET}  "
          f"{e.get('device')}  원인 {e.get('source')}  버튼 {e.get('button_id') or '-'}"
          + (f"  {DIM}({e['details']}){RESET}" if e.get("details") else ""), flush=True)


def show_env(d: dict):
    """sensor/env: 환경 센서 한 줄"""
    parts = [f"{r.get('metric')} {r.get('value')}{r.get('unit')}" for r in d.get("readings", [])]
    print(f"{GRAY}{short_time(d.get('ts'))}  [센서] {d.get('device')}  " + "   ".join(parts) + RESET, flush=True)


def fmt(v, unit):
    return "  -  " if v is None else f"{v:.3f}{unit}"


def defects(vis: dict) -> str:
    d = vis.get("defects") or []
    return ("(" + ",".join(x.get("type", "?") for x in d) + ") ") if d else ""


def show_result(r: dict, raw: str, with_json: bool):
    g = r.get("grade", "?")
    c = GRADE_COLOR.get(g, "")
    vis = r.get("vision") or {}
    print(f"{GRAY}{short_time(r.get('finished_at'))}{RESET}  {BOLD}[결과]{RESET} #{r.get('battery_id'):<3} "
          f"{c}{BOLD}{g:<10}{RESET} → {c}{DEST_KR.get(r.get('dest'), r.get('dest')):<6}{RESET} "
          f"외관 {vis.get('result') or vis.get('status') or '-':<7}{defects(vis):<14} 측정 {r.get('electrical_status'):<8} "
          f"V {fmt(r.get('v_open'), 'V')} / {fmt(r.get('v_load'), 'V')}  R {fmt(r.get('r_int'), 'Ω')}"
          + (f"  {DIM}({r.get('reason')}){RESET}" if r.get("reason") else ""), flush=True)
    if with_json:
        print(DIM + json.dumps(json.loads(raw), ensure_ascii=False, indent=2) + RESET, flush=True)


async def main(args):
    print(f"{BOLD}라인 모니터{RESET} {GRAY}({BROKER_HOST}:{BROKER_PORT}){RESET}  "
          f"{GREEN}INFO{RESET} {YELLOW}WARN{RESET} {RED}ERROR{RESET}  |  "
          f"등급 {GREEN}A{RESET} {CYAN}B{RESET} {RED}NG_VISUAL NG_VOLTAGE{RESET} {MAGENTA}RESORT{RESET}", flush=True)
    print(GRAY + "─" * 100 + RESET, flush=True)
    async with aiomqtt.Client(BROKER_HOST, BROKER_PORT) as client:
        await client.subscribe("server/events", qos=1)
        await client.subscribe("server/results", qos=1)
        await client.subscribe("server/devices", qos=1)
        await client.subscribe("server/line", qos=1)
        await client.subscribe("event/estop", qos=1)
        await client.subscribe("sensor/env", qos=1)
        async for m in client.messages:
            raw = m.payload.decode("utf-8", errors="replace") if isinstance(m.payload, (bytes, bytearray)) else str(m.payload)
            try:
                data = json.loads(raw)
            except json.JSONDecodeError:
                print(f"{RED}형식 오류{RESET} {m.topic.value}: {raw[:100]}", flush=True)
                continue
            topic = m.topic.value
            if topic == "server/events":
                show_event(data)
            elif topic == "server/devices":
                show_devices(data)
            elif topic == "server/results":
                show_result(data, raw, args.json)
            elif topic == "server/line" and not args.no_line:
                show_line(data)
            elif topic == "event/estop":
                show_estop(data)
            elif topic == "sensor/env" and args.env:
                show_env(data)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="라인 모니터 (콘솔)")
    p.add_argument("--json", action="store_true", help="결과 원본 JSON도 출력")
    p.add_argument("--no-line", action="store_true", help="라인 현황 줄 숨기기 (결과·이벤트만)")
    p.add_argument("--env", action="store_true", help="환경 센서 값도 출력 (5초마다 한 줄)")
    p.add_argument("--log", default="", help="화면 출력을 색 정보와 함께 파일로도 저장 (캡처 이미지 만들 때 사용)")
    args = p.parse_args()
    if not sys.stdout.isatty():            # 다른 프로그램이 켰을 때(출력이 창이 아닐 때)도 한글·기호가 깨지지 않게
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if args.log:
        _log_file = open(args.log, "a", encoding="utf-8")
    try:
        if sys.platform.startswith("win"):
            with asyncio.Runner(loop_factory=asyncio.SelectorEventLoop) as runner:
                runner.run(main(args))
        else:
            asyncio.run(main(args))
    except KeyboardInterrupt:
        print("모니터 종료")
    except Exception as e:
        print(f"브로커에 연결할 수 없음: {e!r}")
