"""
# agv_cmd_test_v2 (agv_cmd_test 대신): agv_relay_v3의 하트비트(agv_state)에 맞춤
AGV 명령 시험 콘솔 — 서버 대신 cmd/agv 를 보내고 res/agv, hb/agv 를 봅니다.
필요: pip install paho-mqtt
실행: python agv_cmd_test.py            (브로커가 이 PC면)
      python agv_cmd_test.py 192.168.0.10
명령: go [개수] | return | estop | reset | quit
"""
import json
import sys
import time
import uuid

import paho.mqtt.client as mqtt

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
last_hb = None


def on_connect(client, userdata, flags, reason_code, properties):
    print(f"[브로커 접속] {HOST} ({reason_code})")
    client.subscribe([("res/agv", 1), ("status/agv", 1), ("hb/agv", 0)])


def on_message(client, userdata, msg):
    global last_hb
    text = msg.payload.decode("utf-8", "replace")
    if msg.topic == "hb/agv":
        hb = json.loads(text)
        key = (hb.get("agv_state"), hb.get("carrying"))
        if key != last_hb:
            last_hb = key
            print(f"[AGV 상태] {hb.get('agv_state')} (carrying={hb.get('carrying')})")
    else:
        print(f"[{msg.topic}] {text}")


def send(client, action, params=None):
    t = time.time()
    cmd = {"cmd_id": uuid.uuid4().hex[:16], "run_id": "test", "action": action,
           "params": params or {},
           "ts": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(t)) + f".{int(t*1000)%1000:03d}"}
    client.publish("cmd/agv", json.dumps(cmd), qos=1)
    print(f"[보냄] {action} {params or ''} ({cmd['cmd_id']})")


client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="agv_tester")
client.on_connect = on_connect
client.on_message = on_message
client.connect(HOST, 1883)
client.loop_start()

print("명령: go [개수] | return | estop | reset | quit")
while True:
    try:
        p = input("> ").strip().split()
    except (EOFError, KeyboardInterrupt):
        break
    if not p:
        continue
    if p[0] == "quit":
        break
    if p[0] == "go":
        send(client, "go", {"count": int(p[1]) if len(p) > 1 else 8})
    elif p[0] in ("return", "estop", "reset"):
        send(client, p[0])
    else:
        print("모르는 명령")
client.loop_stop()
