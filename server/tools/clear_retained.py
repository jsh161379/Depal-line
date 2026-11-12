"""
브로커에 남은 예전 이름의 retained 기록 지우기 (통신 규약 v1.4 이름 변경 후 1번 실행)
=====================================================================================
v1.4에서 장치 이름이 바뀌었다:  pi → arm1,  ui-<PC이름> → admin-<번호>
브로커에는 예전 이름의 status/pi, status/ui-… 기록(retained)이 남아 있어서
서버를 켤 때마다 "pi 끊김", "ui-desktop-… 끊김"처럼 보인다. 이 기록을 지운다.

  - status/ 아래 retained 기록을 2초 동안 모아서 목록을 보여 준다
  - 지울 대상: 아래 OLD_PATTERNS 중 하나로 시작하는 것
  - --yes 를 붙여야 실제로 지운다 (빈 내용을 retained로 보내면 브로커가 기록을 지움)

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2"
실행 (서버 폴더에서):
  python tools\\clear_retained.py           지울 목록만 보기
  python tools\\clear_retained.py --yes     실제로 지우기
"""

# ---------- 수정할 값 ----------
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
COLLECT_SEC = 2.0
OLD_PATTERNS = ("status/pi", "status/ui", "hb/pi", "hb/ui")   # 이 글자로 시작하는 retained 기록을 지움
# -------------------------------

import argparse
import asyncio
import sys

import aiomqtt


async def collect(client) -> list[str]:
    found = []

    async def reader():
        async for m in client.messages:
            if m.retain and m.payload:
                found.append(str(m.topic))

    task = asyncio.create_task(reader())
    await asyncio.sleep(COLLECT_SEC)
    task.cancel()
    return sorted(set(found))


async def amain(yes: bool):
    async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier="clear_retained") as client:
        await client.subscribe("status/#", qos=1)
        await client.subscribe("hb/#", qos=0)
        topics = await collect(client)
        old = [t for t in topics if t.startswith(OLD_PATTERNS)]
        keep = [t for t in topics if t not in old]
        print(f"retained 기록 {len(topics)}개 (status/·hb/)")
        for t in keep:
            print(f"  그대로 둠  {t}")
        for t in old:
            print(f"  지울 대상  {t}")
        if not old:
            print("지울 것이 없습니다.")
            return
        if not yes:
            print("\n실제로 지우려면  python tools\\clear_retained.py --yes")
            return
        for t in old:
            await client.publish(t, payload=b"", qos=1, retain=True)
        print(f"\n{len(old)}개 지움. 중앙 서버를 다시 켜면 목록에서 사라집니다.")


def main():
    p = argparse.ArgumentParser(description="예전 장치 이름의 retained 기록 지우기 (규약 v1.4)")
    p.add_argument("--yes", action="store_true", help="실제로 지움 (없으면 목록만)")
    a = p.parse_args()
    try:
        if sys.platform.startswith("win"):
            with asyncio.Runner(loop_factory=asyncio.SelectorEventLoop) as r:
                r.run(amain(a.yes))
        else:
            asyncio.run(amain(a.yes))
    except aiomqtt.MqttError as e:
        print(f"브로커에 연결할 수 없음: {e} → Mosquitto가 켜져 있는지 확인")


if __name__ == "__main__":
    main()
