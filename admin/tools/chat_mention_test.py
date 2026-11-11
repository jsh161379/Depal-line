"""
채팅 @멘션 시험 — 실제 서버 모드에서 "다른 사람이 나를 부른" 메시지를 한 번 보낸다.
관리자 프로그램이 같은 브로커에 붙어 있으면 그 메시지가 호박색으로 강조되고 소리가 나야 한다.

실행 (서버 PC, C:\\depal\\server 에서):
    python chat_mention_test.py
    python chat_mention_test.py 장성찬      ← 다른 사람 이름으로 부르기

필요한 것: paho-mqtt (서버의 aiomqtt가 이미 설치해 둠)
형식: 통신 규약 v1.3 5-1 (chat/messages)
"""
import json
import sys
import uuid
from datetime import datetime

import paho.mqtt.publish as publish

# ── 수정할 값 ──────────────────────────────────────────
BROKER = "localhost"
PORT = 1883
TOPIC = "chat/messages"
TO_ID = "admin"          # 부를 사람 아이디 (관리자 프로그램 로그인 아이디)
TO_NAME = "관리자"        # 부를 사람 이름 (계정 관리 화면의 이름)
FROM_ID = "tester"
FROM_NAME = "시험봇"
STATION = "TEST"
# ──────────────────────────────────────────────────────


def main() -> None:
    to_name = sys.argv[1] if len(sys.argv) > 1 else TO_NAME
    to_id = TO_ID if len(sys.argv) <= 1 else ""   # 이름을 직접 주면 아이디는 모름 → 이름으로 비교됨

    msg = {
        "msg_id": uuid.uuid4().hex,                 # 매번 새 번호 (같은 번호는 화면이 한 번만 보여 줌)
        "from_id": FROM_ID,
        "from_name": FROM_NAME,
        "station": STATION,
        "text": f"@{to_name} 시험 메시지입니다. 이 줄이 호박색으로 보이면 성공.",
        "ref_kind": None,
        "ref_text": None,
        "mentions": [{"id": to_id, "name": to_name}],
        "ts": datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3],
    }
    payload = json.dumps(msg, ensure_ascii=False).encode("utf-8")
    publish.single(TOPIC, payload, qos=1, hostname=BROKER, port=PORT)
    print(f"보냄 → {TOPIC}: @{to_name}")


if __name__ == "__main__":
    main()
