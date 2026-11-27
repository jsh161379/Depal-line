r"""
SC-200 카메라 고정 설정 참고 코드 (Depal-line 상단·왼쪽·오른쪽 카메라)
=====================================================================
데이터 수집 때 쓴 카메라 설정값을 코드로 다시 넣고, 제대로 들어갔는지 읽어서 확인한다.
USB를 다시 꽂거나 PC를 재부팅하면 설정이 초기화될 수 있으므로, 카메라를 열 때마다 apply_settings()를 호출한다.

값 출처: SC-200 설정 창 (2026-11-25 확인)
  - 설정 창 숫자를 그대로 쓴다 (OpenCV DirectShow와 같은 단위. 노출 -7도 그대로)
  - 값을 넣으면 해당 항목은 수동 모드로 바뀐다 (자동 끔)

코드로 설정할 수 없는 항목 (OpenCV에 속성 없음) → 설정 창에서 직접 확인
  - 낮은 빛 보상: 끔
  - PowerLine 주파수(깜박임 방지): 60 Hz

⚠ 같은 SC-200 여러 대를 쓰므로, 재부팅·USB 재연결 후 카메라 번호(0, 1, 2) 순서가 바뀔 수 있다.
   포커스가 카메라마다 다르므로(278 / 450 / 465) 번호가 바뀌면 화면이 흐려진다.
   실행 전 --scan 으로 번호와 화면을 확인한다.

사용법
  python sc200_fixed_settings.py --scan                    연결된 카메라 번호 확인
  python sc200_fixed_settings.py --cam 0 --preset top      0번 카메라에 상단 설정 적용 + 화면 표시
  python sc200_fixed_settings.py --cam 1 --preset left
  python sc200_fixed_settings.py --cam 2 --preset right
  python sc200_fixed_settings.py --cam 1 --preset left --read-only   적용 없이 현재 값만 읽기

화면 단축키
  R : 설정 다시 적용   V : 현재 값 읽어서 비교   D : Windows 설정 창 열기   Q / ESC : 종료

다른 코드에서 쓰기
  from sc200_fixed_settings import open_camera, apply_settings, PRESETS
  cap = open_camera(1)
  apply_settings(cap, PRESETS["left"])
"""

from __future__ import annotations

import argparse
import os
import sys
import time

import cv2

# ===================== 카메라별 고정 설정 =====================
FRAME_WIDTH, FRAME_HEIGHT = 640, 480     # 학습 사진과 같은 해상도
USE_MJPG = True

_COMMON = {
    "brightness": 0,      # 밝기
    "contrast": 32,       # 대비
    "hue": 0,             # 색상
    "saturation": 50,     # 채도
    "sharpness": 10,      # 선명도
    "gamma": 110,         # 감마
    "backlight": 0,       # 후광 보정
    "exposure": -7,       # 노출 (자동 끔)
}

PRESETS: dict[str, dict[str, float]] = {
    "top":   _COMMON | {"wb_temperature": 6500, "gain": 40, "focus": 278},   # 상단
    "left":  _COMMON | {"wb_temperature": 4000, "gain": 50, "focus": 450},   # 왼쪽 측면
    "right": _COMMON | {"wb_temperature": 4000, "gain": 50, "focus": 465},   # 오른쪽 측면
}
# ============================================================

PROP_IDS = {
    "brightness": cv2.CAP_PROP_BRIGHTNESS,
    "contrast": cv2.CAP_PROP_CONTRAST,
    "hue": cv2.CAP_PROP_HUE,
    "saturation": cv2.CAP_PROP_SATURATION,
    "sharpness": cv2.CAP_PROP_SHARPNESS,
    "gamma": cv2.CAP_PROP_GAMMA,
    "wb_temperature": cv2.CAP_PROP_WB_TEMPERATURE,
    "backlight": cv2.CAP_PROP_BACKLIGHT,
    "gain": cv2.CAP_PROP_GAIN,
    "focus": cv2.CAP_PROP_FOCUS,
    "exposure": cv2.CAP_PROP_EXPOSURE,
}

LABELS_KO = {
    "brightness": "밝기", "contrast": "대비", "hue": "색상", "saturation": "채도",
    "sharpness": "선명도", "gamma": "감마", "wb_temperature": "화이트 밸런스",
    "backlight": "후광 보정", "gain": "게인", "focus": "포커스", "exposure": "노출",
}

TOLERANCE = 1.0   # 넣은 값과 읽은 값 차이가 이 이상이면 불일치로 본다


def open_camera(index: int) -> cv2.VideoCapture:
    """DirectShow로 카메라를 열고 해상도·MJPG를 맞춘다."""
    backend = cv2.CAP_DSHOW if os.name == "nt" else cv2.CAP_ANY
    cap = cv2.VideoCapture(index, backend)
    if not cap.isOpened():
        raise RuntimeError(f"카메라 {index}를 열 수 없습니다. --scan 으로 번호를 확인하세요.")
    if USE_MJPG:
        cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, FRAME_WIDTH)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, FRAME_HEIGHT)
    for _ in range(5):            # 카메라 안정화
        cap.read()
    return cap


def read_settings(cap: cv2.VideoCapture, keys=None) -> dict[str, float]:
    """현재 카메라가 보고하는 값 읽기"""
    keys = keys or PROP_IDS.keys()
    return {k: cap.get(PROP_IDS[k]) for k in keys}


def verify_settings(cap: cv2.VideoCapture, preset: dict[str, float]) -> list[str]:
    """넣은 값과 읽은 값 비교 → 불일치 항목 목록 (비어 있으면 정상)"""
    current = read_settings(cap, preset.keys())
    return [f"{LABELS_KO[k]}({k}) 설정 {preset[k]} / 실제 {current[k]}"
            for k in preset if abs(current[k] - float(preset[k])) >= TOLERANCE]


def apply_settings(cap: cv2.VideoCapture, preset: dict[str, float], verbose: bool = True) -> bool:
    """자동 기능 끄기 → 수동 값 넣기 → 읽어서 확인. 모두 맞으면 True"""
    # 1) 자동 기능 먼저 끄기 (드라이버에 따라 무시될 수 있어 값 설정도 같이 함)
    cap.set(cv2.CAP_PROP_AUTOFOCUS, 0)
    cap.set(cv2.CAP_PROP_AUTO_WB, 0)
    # 2) 수동 값 넣기 (DirectShow에서는 값을 넣으면 해당 항목이 수동 모드로 바뀜)
    for key, value in preset.items():
        cap.set(PROP_IDS[key], float(value))
    # 3) 몇 프레임 뒤에 적용되는 카메라 대비
    for _ in range(5):
        cap.read()
    time.sleep(0.2)
    # 4) 확인
    wrong = verify_settings(cap, preset)
    if verbose:
        if wrong:
            print("[설정] ⚠ 일부 값이 적용되지 않았습니다:")
            for w in wrong:
                print("   -", w)
        else:
            print(f"[설정] 적용 확인 완료 (포커스 {preset.get('focus')}, "
                  f"화이트 밸런스 {preset.get('wb_temperature')}, 게인 {preset.get('gain')}, 노출 {preset.get('exposure')})")
        print("[설정] 낮은 빛 보상(끔)·PowerLine(60Hz)은 코드로 설정할 수 없으니 D 키로 설정 창에서 확인하세요.")
    return not wrong


def print_settings(cap: cv2.VideoCapture, preset: dict[str, float] | None = None) -> None:
    """현재 값 표로 출력 (preset이 있으면 비교)"""
    current = read_settings(cap)
    print(f"\n{'항목':<12}{'현재 값':>10}" + (f"{'기준 값':>10}  결과" if preset else ""))
    print("-" * (40 if preset else 24))
    for k, v in current.items():
        line = f"{LABELS_KO[k]:<12}{v:>10.1f}"
        if preset and k in preset:
            ok = abs(v - float(preset[k])) < TOLERANCE
            line += f"{float(preset[k]):>10.1f}  {'OK' if ok else '불일치'}"
        print(line)
    print()


def scan_cameras(max_index: int = 8) -> None:
    backend = cv2.CAP_DSHOW if os.name == "nt" else cv2.CAP_ANY
    print(f"[카메라 스캔] 0 ~ {max_index - 1}")
    for idx in range(max_index):
        cap = cv2.VideoCapture(idx, backend)
        if cap.isOpened():
            ok, frame = cap.read()
            if ok and frame is not None:
                print(f"  - {idx}번: 연결됨 ({frame.shape[1]}x{frame.shape[0]}), 포커스 {cap.get(cv2.CAP_PROP_FOCUS):.0f}")
            else:
                print(f"  - {idx}번: 열림 (화면 읽기 실패)")
        cap.release()
    print("※ 같은 SC-200이라 이름으로는 구분이 안 됩니다. 각 번호로 화면을 띄워 상단/왼쪽/오른쪽을 확인하세요.")


def main() -> None:
    p = argparse.ArgumentParser(description="SC-200 카메라 고정 설정 적용·확인")
    p.add_argument("--scan", action="store_true", help="카메라 번호 확인 후 종료")
    p.add_argument("--cam", type=int, help="카메라 번호")
    p.add_argument("--preset", choices=sorted(PRESETS), help="적용할 설정 (top / left / right)")
    p.add_argument("--read-only", action="store_true", help="설정을 넣지 않고 현재 값만 읽기")
    p.add_argument("--no-view", action="store_true", help="화면 표시 없이 적용·확인만 하고 종료")
    a = p.parse_args()

    if a.scan:
        scan_cameras()
        return
    if a.cam is None or (a.preset is None and not a.read_only):
        p.error("--cam 과 --preset 을 지정하세요 (현재 값만 볼 때는 --read-only)")

    preset = PRESETS.get(a.preset) if a.preset else None
    cap = open_camera(a.cam)
    print(f"[카메라] {a.cam}번 열림 ({int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))}x{int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))})")

    if a.read_only:
        print_settings(cap, preset)
    else:
        apply_settings(cap, preset)
        print_settings(cap, preset)

    if a.no_view:
        cap.release()
        return

    win = f"SC-200 cam {a.cam} [{a.preset or 'read-only'}]  R:재적용 V:확인 D:설정창 Q:종료"
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                print("[카메라] 화면을 읽지 못했습니다 (USB 연결 확인)")
                time.sleep(0.5)
                continue
            cv2.imshow(win, frame)
            key = cv2.waitKey(1) & 0xFF
            if key in (ord("q"), ord("Q"), 27):
                break
            if key in (ord("r"), ord("R")) and preset:
                apply_settings(cap, preset)
            elif key in (ord("v"), ord("V")):
                print_settings(cap, preset)
            elif key in (ord("d"), ord("D")):
                cap.set(cv2.CAP_PROP_SETTINGS, 1)
    finally:
        cap.release()
        cv2.destroyAllWindows()


if __name__ == "__main__":
    if sys.stdout and hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    main()
