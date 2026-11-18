"""
상단 카메라 YOLO 학습용 사진 찍기 (capture_top.py)

쓰는 법
  1) pip install opencv-python
  2) python capture_top.py
  3) 키
     1 : 저장 폴더를 minus_up (−극이 위, 정상 방향) 으로
     2 : 저장 폴더를 plus_up  (+극이 위, 거꾸로) 으로
     0 : 저장 폴더를 empty    (퍽만 있거나 아무것도 없는 화면) 으로
     SPACE : 지금 화면 1장 저장
     A : 자동 저장 켜기/끄기 (벨트를 돌리면서 찍을 때)
     Q 또는 ESC : 종료

  - 저장되는 사진에는 화면의 글자·선이 들어가지 않는다 (원본 그대로).
  - 폴더 이름은 라벨링할 때 참고용이다. 최종 라벨은 라벨링 도구에서 붙인다.
"""

import os
import time
from datetime import datetime

import cv2

# ===================== 수정할 값 =====================
CAMERA_INDEX = 0                    # 상단 카메라 번호. 다른 카메라가 뜨면 1, 2로 바꿔 보기
FRAME_WIDTH = 1280                  # 요청 해상도 (카메라가 지원 안 하면 다른 값으로 열림)
FRAME_HEIGHT = 720
USE_DSHOW = True                    # Windows에서 카메라를 빨리 열고 해상도 설정이 잘 먹게 함
SAVE_ROOT = r"C:\depal\yolo_top\raw"  # 사진 저장 위치
AUTO_INTERVAL_SEC = 0.5             # 자동 저장 간격(초). 비슷한 사진이 너무 많으면 늘리기
JPEG_QUALITY = 95
CLASS_KEYS = {
    ord("1"): "minus_up",
    ord("2"): "plus_up",
    ord("0"): "empty",
}
START_CLASS = "minus_up"
# =====================================================


def open_camera():
    if USE_DSHOW and os.name == "nt":
        cap = cv2.VideoCapture(CAMERA_INDEX, cv2.CAP_DSHOW)
    else:
        cap = cv2.VideoCapture(CAMERA_INDEX)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, FRAME_WIDTH)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, FRAME_HEIGHT)
    return cap


def count_files(folder):
    if not os.path.isdir(folder):
        return 0
    return len([f for f in os.listdir(folder) if f.lower().endswith(".jpg")])


def save_frame(frame, cls):
    folder = os.path.join(SAVE_ROOT, cls)
    os.makedirs(folder, exist_ok=True)
    name = datetime.now().strftime("top_%Y%m%d_%H%M%S_%f")[:-3] + ".jpg"
    path = os.path.join(folder, name)
    ok = cv2.imwrite(path, frame, [cv2.IMWRITE_JPEG_QUALITY, JPEG_QUALITY])
    return path if ok else None


def main():
    cap = open_camera()
    if not cap.isOpened():
        print(f"[오류] 카메라 {CAMERA_INDEX}번을 열 수 없음. CAMERA_INDEX를 바꿔 보세요.")
        return

    real_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    real_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    print(f"[정보] 실제 해상도: {real_w} x {real_h}")

    cls = START_CLASS
    auto = False
    last_auto = 0.0
    counts = {c: count_files(os.path.join(SAVE_ROOT, c)) for c in CLASS_KEYS.values()}

    while True:
        ok, frame = cap.read()
        if not ok:
            print("[오류] 화면을 읽지 못함 (카메라 연결 확인)")
            break

        now = time.time()
        if auto and now - last_auto >= AUTO_INTERVAL_SEC:
            if save_frame(frame, cls):
                counts[cls] += 1
            last_auto = now

        # 미리보기 (저장 사진에는 안 들어감)
        view = frame.copy()
        h, w = view.shape[:2]
        cv2.line(view, (w // 2, 0), (w // 2, h), (0, 255, 255), 1)
        cv2.line(view, (0, h // 2), (w, h // 2), (0, 255, 255), 1)
        info = f"class={cls}  auto={'ON' if auto else 'off'}  " + \
               "  ".join(f"{c}:{n}" for c, n in counts.items())
        cv2.putText(view, info, (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 4)
        cv2.putText(view, info, (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1)
        cv2.imshow("top camera capture", view)

        key = cv2.waitKey(1) & 0xFF
        if key in (ord("q"), ord("Q"), 27):
            break
        if key in CLASS_KEYS:
            cls = CLASS_KEYS[key]
            print(f"[정보] 저장 폴더 → {cls}")
        elif key == ord(" "):
            path = save_frame(frame, cls)
            if path:
                counts[cls] += 1
                print(f"[저장] {path}")
        elif key in (ord("a"), ord("A")):
            auto = not auto
            last_auto = 0.0
            print(f"[정보] 자동 저장 {'켜짐' if auto else '꺼짐'}")

    cap.release()
    cv2.destroyAllWindows()
    print("[끝] 저장 수:", counts)


if __name__ == "__main__":
    main()
