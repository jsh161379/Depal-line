"""가짜 주문 관리 프로그램 (홈페이지 연결 시험용)

주문 통신 규약 v1.0 3·4장을 그대로 흉내 낸다. 라인 쪽 fake_mega.py와 같은 역할.
- 홈페이지 주문(order/new)을 검사 → order/accepted, order/status/<no>(received), order/ack
- 고객 취소(order/cancel), 문의(inquiry/new) 처리
- 키보드로 상태 바꾸기(출고 검사 → 배송 중 → 배송 완료), 문의 답변

실행 (서버 PC, server 폴더에서):  python fake_order_admin.py
명령은 키보드로 치거나, data/fake_order_cmd.txt에 한 줄씩 써 넣어도 된다 (읽으면 지움). 출력은 data/fake_order_admin.log에도 남는다.
⚠ 실제 주문 관리 프로그램(C#, UseFakeOrders=false)과 동시에 켜지 말 것 — 둘 다 ack를 보낸다.
공통 담당이 C#에 같은 동작을 넣을 때 참고용 예시이기도 하다.
"""

import json
import random
import re
import sys
import threading
import time
from datetime import datetime
from pathlib import Path

import paho.mqtt.client as mqtt

# ───── 바꿀 값 ─────
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883                       # 서버 PC 안에서는 1883 (계정 없음)
CLIENT_ID = "order_admin_fake"           # 실제 프로그램(order_admin)과 겹치지 않게
OPERATOR = "관리자"                       # 상태·답변의 by
STATE_FILE = Path(__file__).with_name("data") / "fake_order_admin.json"   # 껐다 켜도 주문 기억
CMD_FILE = Path(__file__).with_name("data") / "fake_order_cmd.txt"     # 여기에 명령을 써 넣어도 됨
LOG_FILE = Path(__file__).with_name("data") / "fake_order_admin.log"
DEFAULT_COURIER = "CJ대한통운"

# 가격표 (주문 관리 Core/Models/Products.cs, 홈페이지 PRODUCTS와 같아야 함)
PRODUCTS = {
    "aa4":  ("AA 알카라인 4개입", 3200),
    "aa6":  ("AA 알카라인 6개입", 4700),     # 11/21 팀 결정: 팔레트 한 판(2×3) 크기 (aa8 8개입을 바꿈)
    "aa12": ("AA 알카라인 12개입", 8900),
    "aa24": ("AA 알카라인 24개입", 15900),
    # "aar2" (AA 충전지 2개입)는 11/20 판매 중지 (팀 결정 ⑨) → 새 주문은 BAD_FORMAT. Products.cs에는 OnSale=false로 남음
}

# ───── 규약 값 (주문 통신 규약 v1.0) ─────
T_ORDER_NEW, T_ORDER_ACK, T_ORDER_CANCEL, T_ORDER_ACCEPTED = "order/new", "order/ack", "order/cancel", "order/accepted"
T_INQ_NEW, T_INQ_ACK, T_INQ_ACCEPTED = "inquiry/new", "inquiry/ack", "inquiry/accepted"
def t_status(no): return f"order/status/{no}"
def t_answer(no): return f"inquiry/answer/{no}"

ORDER_NO = re.compile(r"^BR-\d{8}-[A-HJ-NP-Z2-9]{4}$")
INQ_NO = re.compile(r"^Q-\d{8}-[A-HJ-NP-Z2-9]{4}$")
TS = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$")
INQ_TYPES = {"delivery": "주문·배송", "cancel": "취소·교환", "product": "제품", "bulk": "대량 구매", "etc": "기타"}
STATUS_KO = {"received": "주문 접수", "inspect": "출고 검사", "shipping": "배송 중", "delivered": "배송 완료", "canceled": "취소"}
NEXT = {"received": "inspect", "inspect": "shipping", "shipping": "delivered"}
ALLOWED = {"received": {"inspect", "canceled"}, "inspect": {"shipping", "canceled"}, "shipping": {"delivered"},
           "delivered": set(), "canceled": set()}

lock = threading.Lock()
state = {"orders": {}, "inquiries": {}}   # no → {"msg": 원본, "status", "courier", "tracking"} / no → {"msg", "answer"}


def now_ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:23]


def digits(s):
    return re.sub(r"\D", "", str(s or ""))


def say(text):
    line = f"{datetime.now():%H:%M:%S}  {text}"
    print("\r" + line)
    print("> ", end="", flush=True)
    try:
        LOG_FILE.parent.mkdir(exist_ok=True)
        with LOG_FILE.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


def save_state():
    STATE_FILE.parent.mkdir(exist_ok=True)
    tmp = STATE_FILE.with_suffix(".tmp")
    tmp.write_text(json.dumps(state, ensure_ascii=False, indent=1), encoding="utf-8")
    tmp.replace(STATE_FILE)


def load_state():
    if STATE_FILE.exists():
        state.update(json.loads(STATE_FILE.read_text(encoding="utf-8")))


def publish(client, topic, body, retain=False):
    client.publish(topic, json.dumps(body, ensure_ascii=False), qos=1, retain=retain)


def is_str(v, lo, hi):
    return isinstance(v, str) and lo <= len(v.strip()) <= hi


# ───── 주문 ─────
def check_order(m):
    """(오류 코드 또는 None, 설명)"""
    if not isinstance(m, dict):
        return "BAD_FORMAT", "JSON 객체가 아님"
    if not (isinstance(m.get("no"), str) and ORDER_NO.match(m["no"])):
        return "BAD_FORMAT", "주문번호 형식"
    if not (isinstance(m.get("ts"), str) and TS.match(m["ts"])):
        return "BAD_FORMAT", "ts 형식"
    if not is_str(m.get("name"), 1, 50):
        return "BAD_FORMAT", "이름"
    if not 9 <= len(digits(m.get("phone"))) <= 11:
        return "BAD_FORMAT", "연락처"
    if not is_str(m.get("addr"), 1, 255):
        return "BAD_FORMAT", "주소"
    if not isinstance(m.get("memo", ""), str) or len(m.get("memo", "")) > 255:
        return "BAD_FORMAT", "요청 사항"
    items = m.get("items")
    if not isinstance(items, list) or not items:
        return "BAD_FORMAT", "상품 없음"
    total = 0
    for it in items:
        if not isinstance(it, dict) or it.get("id") not in PRODUCTS:
            return "BAD_FORMAT", f"상품 id {it.get('id') if isinstance(it, dict) else it}"
        qty, price = it.get("qty"), it.get("price")
        if type(qty) is not int or not 1 <= qty <= 99 or type(price) is not int:
            return "BAD_FORMAT", "수량·가격"
        if price != PRODUCTS[it["id"]][1]:
            return "PRICE_MISMATCH", f"{it['id']} 단가 {price} (가격표 {PRODUCTS[it['id']][1]})"
        total += qty * price
    if m.get("total") != total:
        return "PRICE_MISMATCH", f"합계 {m.get('total')} (계산 {total})"
    return None, ""


def on_order_new(client, m):
    no = m.get("no") if isinstance(m, dict) else None
    ack = lambda ok, err: publish(client, T_ORDER_ACK, {"no": no, "ok": ok, "error": err, "ts": now_ts()})
    err, why = check_order(m)
    if err == "BAD_FORMAT" and not isinstance(no, str):
        say(f"[주문] 형식 오류, 주문번호도 없어 ack 못 보냄: {why}")
        return
    with lock:
        old = state["orders"].get(no)
        if old is not None:
            if old["msg"] == m:
                say(f"[주문] {no} 같은 주문이 다시 옴 (QoS 1 중복) → ack만 다시")
                ack(True, None)
            else:
                say(f"[주문] {no} 번호는 같은데 내용이 다름 → DUPLICATE_NO")
                ack(False, "DUPLICATE_NO")
            return
        if err:
            say(f"[주문] {no} 거절 {err}: {why}")
            ack(False, err)
            return
        state["orders"][no] = {"msg": m, "status": "received", "courier": None, "tracking": None}
        save_state()
    # ts는 주문 관리(서버 PC) 시계로 바꿔 싣고, 홈페이지 시각은 client_ts로 남긴다 (규약 3장).
    # 고객 PC 시계가 빠르면 뒤에 오는 상태 메시지가 '더 오래된 것'으로 거부되기 때문.
    got = now_ts()
    publish(client, T_ORDER_ACCEPTED, {**m, "ts": got, "client_ts": m["ts"], "accepted_at": got})
    # 첫 상태의 ts = order/accepted의 ts (DB가 주문 저장 때 남긴 첫 기록과 중복 처리되게)
    publish(client, t_status(no), {"no": no, "status": "received", "ts": got, "courier": None,
                                   "tracking": None, "cert_no": None, "by": OPERATOR}, retain=True)
    ack(True, None)
    items = ", ".join(f"{PRODUCTS[i['id']][0]}×{i['qty']}" for i in m["items"])
    say(f"[주문] {no} 접수 — {m['name']} / {items} / {m['total']:,}원")


def set_status(client, no, new, courier=None, tracking=None, why=""):
    """상태 변경 + order/status 발행. 성공하면 True"""
    with lock:
        o = state["orders"].get(no)
        if o is None:
            say(f"없는 주문: {no}")
            return False
        if new not in ALLOWED[o["status"]]:
            say(f"{no}: {STATUS_KO[o['status']]} → {STATUS_KO.get(new, new)} 로 바꿀 수 없음")
            return False
        if new == "shipping":
            courier = courier or DEFAULT_COURIER
            tracking = tracking or "".join(random.choice("0123456789") for _ in range(12))
        if courier: o["courier"] = courier
        if tracking: o["tracking"] = tracking
        o["status"] = new
        save_state()
        body = {"no": no, "status": new, "ts": now_ts(), "courier": o["courier"], "tracking": o["tracking"],
                "cert_no": None, "by": OPERATOR}
    publish(client, t_status(no), body, retain=True)
    extra = f" ({o['courier']} {o['tracking']})" if new == "shipping" else ""
    say(f"[상태] {no} → {STATUS_KO[new]}{extra} {why}")
    return True


def on_order_cancel(client, m):
    no = m.get("no") if isinstance(m, dict) else None
    with lock:
        o = state["orders"].get(no)
        ok = o is not None and digits(m.get("phone")) == digits(o["msg"]["phone"]) and o["status"] == "received"
        reason = ("없는 주문" if o is None else "연락처 다름" if digits(m.get("phone")) != digits(o["msg"]["phone"])
                  else f"상태가 {STATUS_KO[o['status']]}")
    if ok:
        set_status(client, no, "canceled", why="(고객 취소 요청)")
    else:
        say(f"[취소] {no} 취소 안 함: {reason} (응답 없음 — 홈페이지는 5초 뒤 '바로 취소 불가' 안내)")


# ───── 문의 ─────
def check_inquiry(m):
    if not isinstance(m, dict) or not (isinstance(m.get("no"), str) and INQ_NO.match(m["no"])):
        return "BAD_FORMAT"
    if not (isinstance(m.get("ts"), str) and TS.match(m["ts"])):
        return "BAD_FORMAT"
    if not is_str(m.get("name"), 1, 50) or not 9 <= len(digits(m.get("phone"))) <= 11:
        return "BAD_FORMAT"
    if m.get("type") not in INQ_TYPES:
        return "BAD_FORMAT"
    on = m.get("order_no")
    if on is not None and not (isinstance(on, str) and ORDER_NO.match(on)):
        return "BAD_FORMAT"
    if not is_str(m.get("question"), 5, 1000):
        return "BAD_FORMAT"
    return None


def on_inquiry_new(client, m):
    no = m.get("no") if isinstance(m, dict) else None
    ack = lambda ok, err: publish(client, T_INQ_ACK, {"no": no, "ok": ok, "error": err, "ts": now_ts()})
    err = check_inquiry(m)
    with lock:
        old = state["inquiries"].get(no) if isinstance(no, str) else None
        if old is not None:
            ack(old["msg"] == m, None if old["msg"] == m else "DUPLICATE_NO")
            return
        if err:
            say(f"[문의] {no} 거절 {err}")
            if isinstance(no, str): ack(False, err)
            return
        state["inquiries"][no] = {"msg": m, "answer": None}
        save_state()
    got = now_ts()
    publish(client, T_INQ_ACCEPTED, {**m, "ts": got, "client_ts": m["ts"], "accepted_at": got})
    ack(True, None)
    say(f"[문의] {no} {INQ_TYPES[m['type']]} — {m['name']}: {m['question'][:40]}")


def answer(client, no, text):
    with lock:
        q = state["inquiries"].get(no)
        if q is None:
            say(f"없는 문의: {no}")
            return
        q["answer"] = text
        save_state()
    publish(client, t_answer(no), {"no": no, "answer": text, "by": OPERATOR, "ts": now_ts()}, retain=True)
    say(f"[답변] {no} 보냄")


# ───── MQTT ─────
HANDLERS = {T_ORDER_NEW: on_order_new, T_ORDER_CANCEL: on_order_cancel, T_INQ_NEW: on_inquiry_new}


def on_connect(client, userdata, flags, rc, props):
    if rc.is_failure:
        say(f"브로커 접속 거부: {rc}")
        return
    client.subscribe([(t, 1) for t in HANDLERS])
    say(f"브로커 접속 {BROKER_HOST}:{BROKER_PORT} — {', '.join(HANDLERS)} 구독")


def on_message(client, userdata, msg):
    try:
        m = json.loads(msg.payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        say(f"[{msg.topic}] JSON 아님 → 무시")
        return
    HANDLERS[msg.topic](client, m)


# ───── 키보드 ─────
HELP = """명령
  l                     주문·문의 목록 (번호 앞 [1], [q1]으로 고르기)
  n <주문>              다음 단계 (접수 → 출고 검사 → 배송 중(송장 자동) → 배송 완료)
  s <주문> <상태> [택배사 송장]   상태 직접 지정: received inspect shipping delivered canceled
  c <주문>              관리자 취소
  a <문의> <답변 글>     문의 답변 (다시 보내면 답변 바뀜)
  x <주문|문의>          브로커에 남은 retained 기록 지우기 (시험 정리용)
  h                     도움말        q   끝내기
  <주문>은 목록 번호(1) 또는 주문번호, <문의>는 q1 또는 문의번호"""


def pick(arg, kind):
    keys = list(state[kind])
    if kind == "inquiries" and re.fullmatch(r"q\d+", arg or ""):
        i = int(arg[1:]) - 1
        return keys[i] if 0 <= i < len(keys) else None
    if kind == "orders" and (arg or "").isdigit():
        i = int(arg) - 1
        return keys[i] if 0 <= i < len(keys) else None
    return (arg or "").upper() if (arg or "").upper() in state[kind] else None


def show_list():
    with lock:
        rows = ["── 주문 ──"]
        for i, (no, o) in enumerate(state["orders"].items(), 1):
            ship = f" {o['courier']} {o['tracking']}" if o["tracking"] else ""
            rows.append(f" [{i}] {no}  {STATUS_KO[o['status']]:<6} {o['msg']['name']}  {o['msg']['total']:,}원{ship}")
        rows.append("── 문의 ──")
        for i, (no, q) in enumerate(state["inquiries"].items(), 1):
            mark = "답변 완료" if q["answer"] else "답변 대기"
            rows.append(f" [q{i}] {no}  {mark}  {INQ_TYPES[q['msg']['type']]}  {q['msg']['question'][:30]}")
    say("\n".join(rows))


def keyboard(client):
    print(HELP)
    while True:
        try:
            line = input("> ").strip()
        except EOFError:
            break
        if line and handle_line(client, line) == "quit":
            break


def watch_cmd_file(client):
    """data/fake_order_cmd.txt에 써 넣은 명령을 0.5초마다 읽어 실행하고 파일을 지운다"""
    while True:
        time.sleep(0.5)
        try:
            if not CMD_FILE.exists():
                continue
            lines = CMD_FILE.read_text(encoding="utf-8-sig").splitlines()
            CMD_FILE.unlink()
        except OSError:
            continue
        for line in lines:
            if line.strip():
                say(f"(명령 파일) {line.strip()}")
                handle_line(client, line.strip())


def handle_line(client, line):
    """명령 한 줄 실행 (키보드·명령 파일 공통). 끝내기 명령이면 "quit"을 돌려준다."""
    cmd, *args = line.split(maxsplit=2)
    if cmd == "q":
        return "quit"
    elif cmd == "h":
        print(HELP)
    elif cmd == "l":
        show_list()
    elif cmd == "n" and args:
        no = pick(args[0], "orders")
        if no:
            cur = state["orders"][no]["status"]
            if cur in NEXT: set_status(client, no, NEXT[cur])
            else: say(f"{no}: 이미 {STATUS_KO[cur]}")
        else: say("주문을 못 찾음 (l로 목록 확인)")
    elif cmd == "s" and len(args) >= 2:
        no = pick(args[0], "orders")
        rest = args[1].split()
        if no and rest[0] in STATUS_KO:
            set_status(client, no, rest[0], *(rest[1:3]))
        else: say("예: s 1 shipping CJ대한통운 612345678901")
    elif cmd == "c" and args:
        no = pick(args[0], "orders")
        if no: set_status(client, no, "canceled", why="(관리자 취소)")
        else: say("주문을 못 찾음")
    elif cmd == "a" and len(args) == 2:
        no = pick(args[0], "inquiries")
        if no: answer(client, no, args[1])
        else: say("문의를 못 찾음 (l로 목록 확인)")
    elif cmd == "x" and args:
        no = pick(args[0], "orders") or pick(args[0], "inquiries")
        if no:
            client.publish(t_status(no) if no.startswith("BR-") else t_answer(no), b"", qos=1, retain=True)
            say(f"{no} retained 기록 지움 (프로그램 기억은 그대로)")
        else: say("못 찾음")
    else:
        say("명령을 모르겠어요. h = 도움말")


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    load_state()
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=CLIENT_ID, clean_session=False)
    client.on_connect = on_connect
    client.on_message = on_message
    client.reconnect_delay_set(1, 5)
    try:
        client.connect(BROKER_HOST, BROKER_PORT)
    except OSError as e:
        print(f"브로커에 접속할 수 없습니다 ({BROKER_HOST}:{BROKER_PORT}): {e}")
        return
    client.loop_start()
    time.sleep(0.5)
    threading.Thread(target=watch_cmd_file, args=(client,), daemon=True).start()
    try:
        keyboard(client)
    except KeyboardInterrupt:
        pass
    client.loop_stop()
    client.disconnect()


if __name__ == "__main__":
    main()
