r"""
run_all.py - 서버 쪽 프로그램을 한 번에 켜고 끄는 실행기

  창 1개로 켜기 : start_all.bat 더블클릭   (모니터 화면 1개, 끌 때는 Ctrl+C)
  창 없이 켜기  : start_all.vbs 더블클릭   (상태는 관리자 프로그램에서 확인)
  끄기          : stop_all.bat 더블클릭    (어느 쪽으로 켰든 끔)

켜는 순서: 브로커 → 중앙 서버 → 라인 컨트롤러 → DB 저장 → 가짜 라파·가짜 YOLO
각 프로그램의 출력은 창 대신 logs\<이름>.log 에 저장된다 (지난번 것은 <이름>.prev.log).

옵션
  --no-fake     가짜 라파·가짜 YOLO를 켜지 않음 (실제 장치를 붙일 때)
  --no-db       DB 저장 프로그램을 켜지 않음 (MySQL 없이 라인만 볼 때)
  --estop-test  가짜 라파가 40초 뒤 비상정지 버튼을 누름
  --hidden      창 없이 실행 (start_all.vbs가 붙임)
  --stop        실행 중인 실행기를 끔 (stop_all.bat가 붙임)

MySQL 비밀번호
  환경변수 AA_DB_PASSWORD가 있으면 그 값을 쓴다.
  없으면 창 1개 모드에서는 시작할 때 한 번 묻고, 창 없는 모드에서는 시작하지 않는다.
  창 없는 모드를 쓰려면 cmd에서 한 번만:  setx AA_DB_PASSWORD 비밀번호
"""
import argparse
import getpass
import json
import os
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

# ===== 수정할 값 =====
MOSQUITTO_EXE = r"C:\Program Files\mosquitto\mosquitto.exe"
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
BROKER_WAIT_SEC = 10        # 브로커 포트가 열릴 때까지 기다리는 최대 시간
ESTOP_TEST_AT_SEC = 40      # --estop-test 때 비상정지 버튼을 누르는 시각
STOP_WAIT_SEC = 15          # stop_all이 실행기가 꺼지기를 기다리는 최대 시간
# ====================

HERE = Path(__file__).resolve().parent
LOG_DIR = HERE / "logs"
PID_FILE = LOG_DIR / "run_all.pid"      # 실행 중 표시 (실행기와 자식 PID)
STOP_FILE = LOG_DIR / "run_all.stop"    # stop_all이 만들면 실행기가 알아서 끔
IS_WIN = os.name == "nt"
NO_WINDOW = 0x08000000 if IS_WIN else 0  # CREATE_NO_WINDOW: 창 없이 실행


# ---------- 출력 ----------
_main_log = None
_hidden = False


def say(msg):
    """실행기 자신의 메시지: logs\\run_all.log에 남기고, 창이 있으면 화면에도 출력"""
    line = time.strftime("%H:%M:%S ") + msg
    if _main_log:
        _main_log.write(line + "\n")
        _main_log.flush()
    if not _hidden:
        try:
            print("[실행기] " + msg, flush=True)
        except UnicodeEncodeError:
            print(("[실행기] " + msg).encode("ascii", "replace").decode(), flush=True)


def popup(msg, warn=False):
    """창 없는 모드에서 사용자에게 알리는 Windows 알림 창 (기다리지 않음)"""
    if not (_hidden and IS_WIN):
        return

    def show():
        import ctypes
        flags = (0x30 if warn else 0x40) | 0x40000  # 경고/정보 아이콘 + 맨 위
        ctypes.windll.user32.MessageBoxW(0, msg, "디팔 서버", flags)

    threading.Thread(target=show, daemon=True).start()


# ---------- 실행 중인지 확인 ----------
def pid_alive(pid):
    if pid <= 0:
        return False
    if IS_WIN:
        import ctypes
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED_INFORMATION
        if not h:
            return False
        code = ctypes.c_ulong()
        ctypes.windll.kernel32.GetExitCodeProcess(h, ctypes.byref(code))
        ctypes.windll.kernel32.CloseHandle(h)
        return code.value == 259  # STILL_ACTIVE
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def read_pid_file():
    try:
        return json.loads(PID_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_pid_file(procs):
    data = {"pid": os.getpid(), "started": time.strftime("%Y-%m-%d %H:%M:%S"),
            "children": {name: p.pid for name, p in procs}}
    PID_FILE.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")


def port_open(host, port):
    try:
        with socket.create_connection((host, port), timeout=0.5):
            return True
    except OSError:
        return False


# ---------- 끄기 (stop_all.bat) ----------
def kill_pid(pid):
    if IS_WIN:
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        try:
            os.kill(pid, 15)
        except OSError:
            pass


def do_stop():
    info = read_pid_file()
    if not info or not pid_alive(info.get("pid", 0)):
        # 실행기는 없는데 자식만 남은 경우(실행기가 강제로 꺼짐)를 정리
        left = [(n, pid) for n, pid in (info or {}).get("children", {}).items() if pid_alive(pid)]
        if not left:
            print("실행 중인 서버가 없습니다.")
            PID_FILE.unlink(missing_ok=True)
            return 0
        for name, pid in left:
            print(f"남아 있던 {name} (PID {pid}) 종료")
            kill_pid(pid)
        PID_FILE.unlink(missing_ok=True)
        return 0

    STOP_FILE.write_text("stop", encoding="utf-8")
    print("서버를 끄는 중...", flush=True)
    deadline = time.monotonic() + STOP_WAIT_SEC
    while time.monotonic() < deadline:
        if not pid_alive(info["pid"]):
            print("모두 꺼졌습니다.")
            return 0
        time.sleep(0.3)
    print(f"{STOP_WAIT_SEC}초 안에 꺼지지 않아 강제로 끕니다.")
    for pid in list(info.get("children", {}).values()) + [info["pid"]]:
        if pid_alive(pid):
            kill_pid(pid)
    STOP_FILE.unlink(missing_ok=True)
    PID_FILE.unlink(missing_ok=True)
    return 0


# ---------- 켜기 ----------
def python_exe():
    """pythonw.exe로 실행됐어도 자식은 python.exe로 (출력이 로그로 가야 하므로)"""
    exe = Path(sys.executable)
    if exe.name.lower() == "pythonw.exe" and (exe.parent / "python.exe").exists():
        return str(exe.parent / "python.exe")
    return str(exe)


def open_log(name):
    path = LOG_DIR / f"{name}.log"
    if path.exists():
        prev = LOG_DIR / f"{name}.prev.log"
        try:
            prev.unlink(missing_ok=True)
            path.rename(prev)
        except OSError:
            pass  # 다른 프로그램이 열고 있으면 그냥 덮어씀
    return open(path, "w", encoding="utf-8", errors="replace", buffering=1)


def spawn(name, args, env, cwd=HERE):
    log = open_log(name)
    log.write(time.strftime("=== %Y-%m-%d %H:%M:%S 시작: ") + " ".join(args) + "\n")
    p = subprocess.Popen(args, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                         stdout=log, stderr=subprocess.STDOUT, creationflags=NO_WINDOW)
    p._log = log
    say(f"{name} 켜짐 (PID {p.pid})")
    return p


def stop_children(procs):
    # 켠 순서의 반대로 끈다 (브로커가 마지막)
    for name, p in reversed(procs):
        if p.poll() is None:
            p.terminate()
            try:
                p.wait(timeout=3)
            except subprocess.TimeoutExpired:
                p.kill()
            say(f"{name} 꺼짐")
        try:
            p._log.close()
        except AttributeError:
            pass


def main():
    global _main_log, _hidden
    ap = argparse.ArgumentParser(description="서버 쪽 프로그램을 한 번에 켜고 끄는 실행기")
    ap.add_argument("--no-fake", action="store_true", help="가짜 라파·가짜 YOLO를 켜지 않음")
    ap.add_argument("--no-db", action="store_true", help="DB 저장 프로그램을 켜지 않음")
    ap.add_argument("--estop-test", action="store_true", help=f"가짜 라파가 {ESTOP_TEST_AT_SEC}초 뒤 비상정지")
    ap.add_argument("--hidden", action="store_true", help="창 없이 실행")
    ap.add_argument("--stop", action="store_true", help="실행 중인 실행기를 끔")
    a = ap.parse_args()

    LOG_DIR.mkdir(exist_ok=True)
    if a.stop:
        return do_stop()

    _hidden = a.hidden
    if not _hidden and sys.stdout and not sys.stdout.isatty():
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    # 두 번 켜기 방지
    info = read_pid_file()
    if info and pid_alive(info.get("pid", 0)):
        msg = (f"이미 실행 중입니다 (시작 {info.get('started')}).\n"
               "다시 켜려면 stop_all.bat로 먼저 끄세요.")
        if _hidden:
            popup(msg, warn=True)
            time.sleep(10)  # 알림 창이 보일 시간
        else:
            print(msg)
        return 1
    STOP_FILE.unlink(missing_ok=True)
    _main_log = open_log("run_all")

    # MySQL 비밀번호: 한 번만 받아서 DB 저장 프로그램에 환경변수로 넘김
    env = {**os.environ, "PYTHONIOENCODING": "utf-8", "PYTHONUNBUFFERED": "1"}
    if not a.no_db and not env.get("AA_DB_PASSWORD"):
        if _hidden:
            msg = ("MySQL 비밀번호가 저장되어 있지 않아 시작하지 않았습니다.\n"
                   "cmd에서 한 번만 실행:  setx AA_DB_PASSWORD 비밀번호\n"
                   "(또는 start_all.bat로 켜면 비밀번호를 물어봅니다)")
            say(msg.replace("\n", " / "))
            popup(msg, warn=True)
            time.sleep(30)
            return 1
        env["AA_DB_PASSWORD"] = getpass.getpass("MySQL 비밀번호 (DB 저장용): ")

    py = python_exe()
    procs = []
    monitor = None
    PID_FILE.write_text(json.dumps({"pid": os.getpid()}), encoding="utf-8")
    try:
        # 1) 브로커
        if port_open(BROKER_HOST, BROKER_PORT):
            say(f"{BROKER_PORT} 포트에 이미 브로커가 있어 그것을 씀 (Mosquitto 서비스나 이전 창)")
        else:
            if not Path(MOSQUITTO_EXE).exists():
                raise RuntimeError(f"Mosquitto가 없습니다: {MOSQUITTO_EXE}")
            procs.append(("broker", spawn("broker", [MOSQUITTO_EXE, "-c", "mosquitto.conf"], env)))
            t0 = time.monotonic()
            while not port_open(BROKER_HOST, BROKER_PORT):
                if procs[-1][1].poll() is not None or time.monotonic() - t0 > BROKER_WAIT_SEC:
                    raise RuntimeError("브로커가 켜지지 않았습니다 → logs\\broker.log 확인")
                time.sleep(0.2)

        # 2) 서버 쪽 프로그램
        plan = [("server", ["central_server.py"], HERE),
                ("controller", ["line_controller.py"], HERE)]
        if not a.no_db:
            plan.append(("db_writer", ["mqtt_db_writer.py"], HERE / "db"))
        if not a.no_fake:
            pi_args = ["fake_pi.py"] + (["--estop-at", str(ESTOP_TEST_AT_SEC)] if a.estop_test else [])
            plan += [("fake_pi", pi_args, HERE), ("fake_yolo", ["fake_yolo.py"], HERE)]
        for i, (name, args, cwd) in enumerate(plan):
            procs.append((name, spawn(name, [py, *args], env, cwd)))
            time.sleep(1.5 if i < 2 else 0.5)  # 서버·컨트롤러가 먼저 구독하도록

        # 3) 모니터: 창 1개 모드에서만, 이 창에 그대로 출력
        if not _hidden:
            say("모니터를 이 창에 띄웁니다. 끄려면 Ctrl+C")
            if a.estop_test:
                say(f"라인 시작 {ESTOP_TEST_AT_SEC}초쯤 뒤 비상정지 → 버튼 복귀 후 관리자 프로그램에서 해제(reset)")
            print("-" * 60, flush=True)
            monitor = subprocess.Popen([py, "monitor.py"], cwd=HERE, env=env)

        write_pid_file(procs)
        names = ", ".join(n for n, _ in procs)
        say(f"시작 완료: {names}")
        popup(f"서버를 켰습니다.\n{names}\n\n상태는 관리자 프로그램에서, 끄기는 stop_all.bat")

        # 4) 지켜보기: 하나가 꺼지면 알림, stop_all이 부르면 전부 끔
        reported = set()
        while True:
            if STOP_FILE.exists():
                say("stop_all 요청을 받아 끕니다")
                break
            if monitor and monitor.poll() is not None:
                say("모니터가 꺼져 전부 끕니다")
                break
            for name, p in procs:
                if name not in reported and p.poll() is not None:
                    reported.add(name)
                    msg = f"{name} 종료됨 (코드 {p.returncode}) → logs\\{name}.log 확인"
                    say(msg)
                    popup(msg, warn=True)
            time.sleep(0.5)
    except KeyboardInterrupt:
        say("Ctrl+C → 전부 끕니다")
    except RuntimeError as e:
        say(str(e))
        popup(str(e), warn=True)
        if _hidden:
            time.sleep(30)
    finally:
        if monitor and monitor.poll() is None:
            monitor.terminate()
        stop_children(procs)
        STOP_FILE.unlink(missing_ok=True)
        PID_FILE.unlink(missing_ok=True)
        say("끝")
    return 0


if __name__ == "__main__":
    sys.exit(main())
