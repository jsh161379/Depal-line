r"""
카메라 번호 ↔ USB 꽂힌 위치 확인 (SC-200 여러 대 구분용, 시험 코드)
=====================================================================

[왜 필요한가]
  SC-200 카메라는 세 대 모두 이름이 같아서, 프로그램은 0번·1번·3번 같은 "번호"로만 카메라를 연다.
  이 번호는 PC를 껐다 켜거나 USB를 다시 꽂으면 순서가 바뀔 수 있다.
  번호가 바뀌면 상단 카메라에 측면 설정이 들어가는 식으로 엉뚱하게 동작한다.

[확인된 사실 (11/26 노트북 시험)]
  - SC-200에는 카메라마다 다른 고유 번호(시리얼)가 없다.
  - Windows는 "어느 USB 자리에 꽂혔는지"로 카메라를 구분한다 (USB 위치).
  - 카메라를 같은 자리에 꽂아 두면 USB 위치는 재부팅·재연결해도 그대로다.
  - 카메라를 다른 자리로 옮기면 USB 위치가 바뀐다 → 역할(상단/왼쪽/오른쪽)도 다시 확인해야 한다.

[사용 규칙]
  1. 허브는 PC의 정해진 USB 포트에, 카메라는 허브의 정해진 포트에 꽂는다 (라벨 붙여 두기).
  2. 자리를 바꿨거나 처음 설치한 PC라면 이 스크립트를 실행해서
     "어느 USB 위치 = 상단/왼쪽/오른쪽"을 다시 확인하고 기록한다.
  3. 같은 자리에 꽂혀 있으면 이후로는 USB 위치만 보고 번호를 찾을 수 있다.

[USB 자리를 바꿨을 때]
  1. 이 스크립트를 실행한다.
  2. 표에서 SC-200 세 대의 번호와 USB 위치를 확인한다.
  3. 번호별로 화면을 띄워서(sc200_fixed_settings.py --cam 번호 --read-only)
     어느 카메라가 상단/왼쪽/오른쪽인지 확인한다.
  4. "USB 위치 → 역할"을 새로 기록한다 (yolo_vision.py 설정 등).

[준비 (한 번만)]
  python -m pip install cv2_enumerate_cameras

[실행]
  python cam_identify.py          OpenCV 번호별(DSHOW·MSMF) 장치 ID·USB 위치 표
  python cam_identify.py --raw    장치 경로 원문도 같이 출력 (문제 생겼을 때 확인용)

[결과 보는 법]
  - DSHOW 표: 카메라 설정(포커스 등)을 넣을 때 쓰는 번호
  - MSMF 표 : yolo_vision.py가 화면을 읽을 때 쓰는 번호
  - 같은 카메라는 두 표에서 번호가 달라도 "장치 ID"와 "USB 위치"가 같다 → 이걸로 짝을 맞춘다
  - "자리" 열: 허브에 꽂혔으면 "허브 N번", PC에 바로 꽂혔으면 "PC 직접" (참고용 요약)
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys

import cv2

TARGET_VID = "32E6"   # SC-200 (VID_32E6&PID_9251)


def enumerate_backend(api: int):
    try:
        from cv2_enumerate_cameras import enumerate_cameras
    except ImportError:
        sys.exit("cv2_enumerate_cameras가 없습니다:  python -m pip install cv2_enumerate_cameras")
    return list(enumerate_cameras(api))


def instance_id_from_path(path: str) -> str | None:
    r"""\\?\usb#vid_32e6&pid_9251&mi_00#8&12816cc&0&0000#{...}  →  USB\VID_32E6&PID_9251&MI_00\8&12816CC&0&0000"""
    if not path or "#" not in path:
        return None
    parts = path.split("#")
    if len(parts) < 3:
        return None
    bus = parts[0].split("\\")[-1]           # usb
    return f"{bus}\\{parts[1]}\\{parts[2]}".upper()


def location_map() -> dict[str, str]:
    """Windows PnP: InstanceId → LocationInfo (카메라·이미지 장치만)"""
    ps = r"""
Get-PnpDevice -PresentOnly | Where-Object { $_.Class -in 'Camera','Image' } | ForEach-Object {
  $loc = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction SilentlyContinue).Data
  [PSCustomObject]@{ id = $_.InstanceId; loc = $loc; name = $_.FriendlyName }
} | ConvertTo-Json -Compress
"""
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                             capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30).stdout.strip()
        data = json.loads(out) if out else []
        if isinstance(data, dict):
            data = [data]
        return {d["id"].upper(): (d.get("loc") or "") for d in data}
    except Exception as exc:  # noqa: BLE001
        print(f"[경고] PowerShell로 USB 위치를 읽지 못했습니다: {exc}")
        return {}


def hub_port(location: str) -> str:
    """USB 위치 요약 (참고용)
    0007.0000.0003.001.002.000… → 허브 2번   /   0005.0000.0003.001.000… → PC 직접"""
    nums = [p for p in location.split(".") if p.strip("0")]
    if len(nums) >= 4:
        return f"허브 {int(nums[-1])}번"
    return "PC 직접" if nums else "-"


def main() -> None:
    ap = argparse.ArgumentParser(description="OpenCV 카메라 번호 ↔ USB 위치")
    ap.add_argument("--raw", action="store_true", help="장치 경로 원문 출력")
    a = ap.parse_args()

    locs = location_map()
    for label, api in (("DSHOW", cv2.CAP_DSHOW), ("MSMF", cv2.CAP_MSMF)):
        cams = enumerate_backend(api)
        print(f"\n=== {label} ({len(cams)}대) ===")
        print(f"{'번호':>6}  {'이름':<16} {'SC-200':<6} {'자리':<8} {'장치 ID':<44} USB 위치")
        for c in cams:
            inst = instance_id_from_path(getattr(c, "path", "") or "")
            loc = locs.get(inst or "", "")
            is_sc = "예" if (getattr(c, "vid", None) and f"{c.vid:04X}" == TARGET_VID) or (inst and TARGET_VID in inst) else ""
            print(f"{c.index:>6}  {str(c.name)[:16]:<16} {is_sc:<6} {hub_port(loc) if loc else '-':<8} "
                  f"{(inst or '(경로 없음)')[:44]:<44} {loc or '-'}")
            if a.raw:
                print(f"        경로: {getattr(c, 'path', '')}")
    print("\n※ 같은 카메라는 DSHOW·MSMF 표에서 번호가 달라도 장치 ID·USB 위치가 같다.")
    print("※ MSMF 번호가 1400 이상으로 나오면 OpenCV가 붙인 값이다 (1400 + 실제 번호).")
    print("※ USB 자리를 바꾸면 USB 위치가 바뀐다 → 역할(상단/왼쪽/오른쪽)을 다시 확인할 것.")


if __name__ == "__main__":
    if sys.stdout and hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    main()
