"""
시험 주문·문의의 retained 기록 지우기 (주문 통신 규약 v1.0)
============================================================
order/status/<주문번호>, inquiry/answer/<문의번호>는 retained라서 브로커에 계속 남는다.
시험 때 가짜 주문 관리로만 만든 번호는 DB에 없어서, DB 저장 프로그램을 켤 때마다
"Rejected ... unknown order" 오류가 찍힌다. 그 번호의 기록만 골라 지운다.

  - 아래 TEST_NUMBERS(또는 실행할 때 적은 번호)의 기록이 브로커에 있는지 2초 동안 확인해 보여 준다
  - --yes 를 붙여야 실제로 지운다 (빈 내용을 retained로 보내면 브로커가 기록을 지움)
  - 지울 때 DB 저장 프로그램이 빈 메시지를 받아 "Rejected malformed" 한 줄을 남긴다 (한 번뿐, 무시해도 됨)

필요한 것: Python 3.11 이상, pip install "aiomqtt>=2"
실행 (서버 폴더에서):
  python tools\\clear_test_orders.py                       기본 번호 확인만
  python tools\\clear_test_orders.py --yes                 기본 번호 지우기
  python tools\\clear_test_orders.py BR-20261117-XXXX --yes   적은 번호만 지우기
"""

# ---------- 수정할 값 ----------
BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
COLLECT_SEC = 2.0
TEST_NUMBERS = (            # 11/17 오전 가짜 주문 관리 시험에서 생긴 번호 (DB에 없음)
    "BR-20261117-LL56",
    "BR-20261117-4TFA",
    "Q-20261117-9EHG",
)
# -------------------------------

import argparse
import asyncio
import sys

import aiomqtt


def topic_of(no: str) -> str:
    return f"order/status/{no}" if no.startswith("BR-") else f"inquiry/answer/{no}"


async def amain(numbers: list[str], yes: bool):
    targets = {topic_of(n) for n in numbers}
    found: set[str] = set()
    async with aiomqtt.Client(BROKER_HOST, BROKER_PORT, identifier="clear_test_orders") as client:
        for t in sorted(targets):
            await client.subscribe(t, qos=1)

        async def reader():
            async for m in client.messages:
                if m.retain and m.payload:
                    found.add(str(m.topic))

        task = asyncio.create_task(reader())
        await asyncio.sleep(COLLECT_SEC)
        task.cancel()

        for t in sorted(targets):
            print(f"  {'지울 대상' if t in found else '기록 없음'}  {t}")
        if not found:
            print("지울 것이 없습니다.")
            return
        if not yes:
            print("\n실제로 지우려면 끝에 --yes 를 붙여 다시 실행")
            return
        for t in sorted(found):
            await client.publish(t, payload=b"", qos=1, retain=True)
        print(f"\n{len(found)}개 지움. DB 저장 프로그램을 다시 켜도 'unknown order' 오류가 안 나옵니다.")


def main():
    p = argparse.ArgumentParser(description="시험 주문·문의 retained 기록 지우기")
    p.add_argument("numbers", nargs="*", help="지울 주문번호(BR-…)·문의번호(Q-…). 없으면 TEST_NUMBERS")
    p.add_argument("--yes", action="store_true", help="실제로 지움 (없으면 확인만)")
    a = p.parse_args()
    numbers = a.numbers or list(TEST_NUMBERS)
    bad = [n for n in numbers if not n.startswith(("BR-", "Q-"))]
    if bad:
        print(f"번호 형식이 아님: {', '.join(bad)} (BR-… 또는 Q-…)")
        return
    try:
        if sys.platform.startswith("win"):
            with asyncio.Runner(loop_factory=asyncio.SelectorEventLoop) as r:
                r.run(amain(numbers, a.yes))
        else:
            asyncio.run(amain(numbers, a.yes))
    except aiomqtt.MqttError as e:
        print(f"브로커에 연결할 수 없음: {e} → Mosquitto가 켜져 있는지 확인")


if __name__ == "__main__":
    main()
