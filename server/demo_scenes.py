"""
포트폴리오 캡처 장면 자동 실행기
================================
가짜 장치를 알아서 켜고 끄면서 캡처할 장면 4개를 차례로 만듭니다.
할 일: 장면이 완성되면 'monitor.py' 창을 캡처(Win+Shift+S)하고, 이 창에서 Enter.

준비: start_all.bat --no-fake --no-db 로 브로커·서버·모니터가 켜져 있어야 함
실행: python demo_scenes.py
필요한 파일(같은 폴더): fake_device.py, sample_results.py
"""

# ===================== 설정값 =====================
FREEZE_AFTER_SEC = 8      # 장면 1: 몇 초 뒤 하트비트를 멈출지
CRASH_AFTER_SEC = 6       # 장면 2: 몇 초 뒤 강제 종료할지
SKIP_SEQ = 6              # 장면 3: 몇 번째 하트비트를 빼먹을지
# =================================================

import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
procs: list[subprocess.Popen] = []

os.system("")   # Windows cmd에서 색 켜기
B, Y, G, R, X = "\033[1m", "\033[93m", "\033[92m", "\033[91m", "\033[0m"


def run(*args, quiet=True) -> subprocess.Popen:
    """가짜 장치 등을 백그라운드로 실행 (출력은 숨김)"""
    p = subprocess.Popen([PY, os.path.join(HERE, args[0]), *args[1:]], cwd=HERE,
                         stdout=subprocess.DEVNULL if quiet else None,
                         stderr=subprocess.DEVNULL if quiet else None)
    procs.append(p)
    return p


def wait(sec: float, msg: str):
    for left in range(int(sec), 0, -1):
        print(f"\r  {msg} ... {left:2d}초 ", end="", flush=True)
        time.sleep(1)
    print(f"\r  {msg} ... 완료      ", flush=True)


def capture(filename: str, hint: str):
    print(f"\n  {G}{B}▶ 지금 캡처하세요{X}  →  {B}{filename}{X}")
    print(f"    {hint}")
    input(f"    캡처했으면 {B}Enter{X} ")


def scene(no: int, title: str):
    print(f"\n{Y}{B}━━━ 장면 {no}. {title} ━━━{X}")


def stop(p: subprocess.Popen):
    if p.poll() is None:
        p.terminate()      # 강제 종료 (브로커가 LWT를 보냄)
        p.wait(timeout=5)


def main():
    print(f"{B}포트폴리오 캡처 장면 자동 실행{X}")
    print("  start_all.bat --no-fake --no-db 로 브로커·서버·모니터가 켜져 있어야 합니다.")
    print("  장면마다 모니터 창을 캡처해서 captures 폴더에 아래 이름으로 저장하세요.")
    input(f"  준비됐으면 {B}Enter{X} ")

    # 장면 1: 정상 → 지연 → 끊김 → 다시 켜서 복구(번호 재시작 감지)
    scene(1, "하트비트: 정상 → 지연 → 끊김 → 복구")
    p = run("fake_device.py", "yolo", "--freeze", str(FREEZE_AFTER_SEC))
    wait(FREEZE_AFTER_SEC + 6.5, "yolo 켜고, 하트비트 멈춘 뒤 지연·끊김 기다리는 중")
    stop(p)
    time.sleep(1)
    p = run("fake_device.py", "yolo")
    wait(3, "yolo 다시 켜는 중 (복구 + 번호 재시작 감지)")
    capture("final_01_hb_timeout.png",
            "모니터에서 '수신 시작 → 지연(노랑) → 끊김(빨강) → 번호 다시 시작 → 복구됨' 줄이 다 보이게")
    stop(p)

    # 장면 2: 강제 종료 → LWT로 즉시 끊김
    scene(2, "강제 종료 → LWT로 즉시 끊김")
    print(f"  agv를 켜고 {CRASH_AFTER_SEC}초 뒤 강제 종료합니다. 모니터에 기다림 없이 바로 끊김이 떠야 합니다.")
    p = run("fake_device.py", "agv", "--crash", str(CRASH_AFTER_SEC))
    wait(CRASH_AFTER_SEC + 2, "agv 동작 중")
    capture("final_02_lwt_crash.png",
            "모니터에서 agv '수신 시작' 시각과 '연결 끊김 (LWT …)' 시각이 같이 보이게 (기다림 없이 바로 끊김)")

    # 장면 3: 하트비트 1개 빠짐 감지 (seq)
    scene(3, "하트비트 1개 빠짐 감지")
    p = run("fake_device.py", "arm1", "--skip", str(SKIP_SEQ))
    wait(SKIP_SEQ + 2, "arm1 동작 중")
    capture("final_03_hb_missing.png", "모니터에서 'arm1 하트비트 1개 빠짐 (seq … → …)' 노란 줄이 보이게")
    stop(p)

    # 장면 4: 결과 7건
    scene(4, "검사 결과 7건 (등급별 색)")
    subprocess.run([PY, os.path.join(HERE, "sample_results.py")], cwd=HERE, stdout=subprocess.DEVNULL)
    time.sleep(1)
    capture("final_04_results.png", "모니터에서 [결과] 7줄 (A 초록, B 하늘, NG 빨강, RESORT 보라)")

    print(f"\n{G}{B}끝났습니다.{X} captures 폴더에 4장이 있는지 확인하세요.")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n중단했습니다.")
    finally:
        for p in procs:
            if p.poll() is None:
                p.terminate()
