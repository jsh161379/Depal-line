r"""
상단 카메라 모델 시험 (eval_top.py)
====================================
학습에 쓰지 않은 확인용 사진으로 상단 모델이 극 방향을 얼마나 맞히는지 잰다.
판정 방법은 yolo_vision.py와 같다: 신뢰도가 가장 높은 minus_up/plus_up 박스, TOP_CONF 미만이면 "없음"

확인용 사진 폴더 구조 (capture_top.py가 만드는 구조와 같음, 폴더 이름 = 정답)
  test\
    minus_up\  *.jpg      −극이 위
    plus_up\   *.jpg      +극이 위
    empty\     *.jpg      빈 퍽 (아무것도 안 찾는 게 정답)

사용법
  python eval_top.py --model models\top_best.pt --data D:\top_test
  python eval_top.py --model models\top_best.pt --data D:\top_test --conf 0.6   (기준 신뢰도 바꿔 보기)

결과 (eval_결과\날짜_시각\ 폴더)
  report.txt   정답률, 헷갈림 표, 기준 신뢰도별 정답률(TOP_CONF 정할 때), 사진 1장 처리 시간
  results.csv  사진별 정답·판정·신뢰도
  wrong\       틀린 사진에 박스를 그려 저장 → 이런 사진을 더 찍어 다시 학습
"""

import argparse
import csv
import sys
import time
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent

# ===================== 수정할 값 =====================
TOP_CONF = 0.50                      # yolo_vision.py의 TOP_CONF와 같게
IMGSZ = 640
CLASSES = ["minus_up", "plus_up"]
NONE = "none"                        # 판정 없음 (빈 퍽의 정답)
FOLDER_TRUTH = {"minus_up": "minus_up", "plus_up": "plus_up", "empty": NONE}
SWEEP = [0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90]   # 기준 신뢰도별 정답률을 볼 값
IMAGE_EXT = {".jpg", ".jpeg", ".png", ".bmp"}
# ====================================================


def judge(dets, conf_min):
    """yolo_vision.judge_top과 같은 규칙 → (판정, 신뢰도)"""
    best = None
    for name, conf, _ in dets:
        if name in CLASSES and (best is None or conf > best[1]):
            best = (name, conf)
    if best is None or best[1] < conf_min:
        return NONE, (best[1] if best else 0.0)
    return best[0], best[1]


class Detector:
    def __init__(self, path):
        from ultralytics import YOLO
        self.yolo = YOLO(str(path))

    def detect(self, img):
        r = self.yolo.predict(img, conf=0.05, imgsz=IMGSZ, verbose=False)[0]
        if r.boxes is None:
            return []
        return [(r.names[int(c)], float(p), tuple(b))
                for c, p, b in zip(r.boxes.cls.tolist(), r.boxes.conf.tolist(), r.boxes.xyxy.tolist())]


def load_items(data: Path):
    items = []
    for folder, truth in FOLDER_TRUTH.items():
        d = data / folder
        if not d.is_dir():
            print(f"[알림] {d} 폴더가 없음 → 건너뜀")
            continue
        for p in sorted(d.iterdir()):
            if p.suffix.lower() in IMAGE_EXT:
                items.append((p, truth))
    return items


def draw(img, dets, title):
    import cv2
    v = img.copy()
    for name, conf, (x1, y1, x2, y2) in dets:
        if conf < 0.25:
            continue
        col = (0, 200, 0) if name == "minus_up" else (0, 0, 255)
        cv2.rectangle(v, (int(x1), int(y1)), (int(x2), int(y2)), col, 2)
        cv2.putText(v, f"{name} {conf:.2f}", (int(x1), max(18, int(y1) - 6)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, col, 2)
    cv2.rectangle(v, (0, 0), (v.shape[1], 30), (0, 0, 0), -1)
    cv2.putText(v, title, (8, 21), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 1)
    return v


def table(rows_truth, rows_pred, labels):
    """헷갈림 표 글자"""
    w = max(len(x) for x in labels) + 2
    lines = ["정답 \\ 판정".ljust(w + 4) + "".join(l.rjust(w) for l in labels)]
    for t in labels:
        cnt = [sum(1 for a, b in zip(rows_truth, rows_pred) if a == t and b == p) for p in labels]
        lines.append(t.ljust(w + 4) + "".join(str(c).rjust(w) for c in cnt))
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description="상단 카메라 모델 시험")
    ap.add_argument("--model", required=True, help="best.pt 경로")
    ap.add_argument("--data", required=True, help="확인용 사진 폴더 (minus_up / plus_up / empty)")
    ap.add_argument("--conf", type=float, default=TOP_CONF, help=f"기준 신뢰도 (기본 {TOP_CONF})")
    ap.add_argument("--out", default=str(HERE / "eval_결과"), help="결과 폴더")
    a = ap.parse_args()
    if sys.stdout and hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    import cv2
    model_path, data = Path(a.model), Path(a.data)
    if not model_path.exists():
        raise SystemExit(f"모델 파일이 없음: {model_path}")
    items = load_items(data)
    if not items:
        raise SystemExit(f"사진이 없음: {data} 안에 minus_up / plus_up / empty 폴더를 만드세요")

    out = Path(a.out) / datetime.now().strftime("%Y%m%d_%H%M%S")
    (out / "wrong").mkdir(parents=True, exist_ok=True)
    det = Detector(model_path)
    print(f"모델 {model_path.name}, 사진 {len(items)}장, 기준 신뢰도 {a.conf}")

    rows, times, all_dets = [], [], []
    for i, (p, truth) in enumerate(items, 1):
        img = cv2.imdecode(__import__("numpy").fromfile(str(p), dtype="uint8"), cv2.IMREAD_COLOR)  # 한글 경로 대응
        if img is None:
            print(f"[알림] 읽을 수 없음: {p}")
            continue
        t0 = time.perf_counter()
        dets = det.detect(img)
        times.append((time.perf_counter() - t0) * 1000)
        pred, conf = judge(dets, a.conf)
        rows.append({"file": str(p.relative_to(data)), "truth": truth, "pred": pred,
                     "conf": round(conf, 3), "ok": pred == truth})
        all_dets.append((truth, dets))
        if pred != truth:
            v = draw(img, dets, f"truth {truth} / pred {pred} {conf:.2f}")
            ok, buf = cv2.imencode(".jpg", v)
            if ok:
                buf.tofile(str(out / "wrong" / f"{truth}__{p.stem}.jpg"))
        if i % 50 == 0:
            print(f"  {i}/{len(items)}")

    labels = CLASSES + [NONE]
    truths, preds = [r["truth"] for r in rows], [r["pred"] for r in rows]
    n_ok = sum(r["ok"] for r in rows)
    acc = n_ok / len(rows) * 100

    # 위험한 실수: +극이 위인데 minus_up으로 판정 → 측정 자리에서만 걸러짐 (2중 확인)
    danger = sum(1 for r in rows if r["truth"] == "plus_up" and r["pred"] == "minus_up")
    # 극 방향 사진 중 판정 없음 → 라인에서는 앞·뒤 값 또는 unknown(측정)으로 넘어감
    missed = sum(1 for r in rows if r["truth"] in CLASSES and r["pred"] == NONE)

    sweep_lines = []
    for c in SWEEP:
        ok = sum(1 for (t, d) in all_dets if judge(d, c)[0] == t)
        sweep_lines.append(f"  {c:.2f}  {ok / len(all_dets) * 100:5.1f}%")

    first = times[0] if times else 0
    avg = sum(times[1:]) / max(1, len(times) - 1) if len(times) > 1 else first
    per_class = []
    for t in FOLDER_TRUTH.values():
        sub = [r for r in rows if r["truth"] == t]
        if sub:
            per_class.append(f"  {t:9s} {sum(r['ok'] for r in sub)}/{len(sub)} "
                             f"({sum(r['ok'] for r in sub) / len(sub) * 100:.1f}%)")

    report = f"""상단 카메라 모델 시험 결과  {datetime.now():%Y-%m-%d %H:%M}
모델: {model_path}
사진: {data} ({len(rows)}장)
기준 신뢰도: {a.conf}

정답률: {n_ok}/{len(rows)} = {acc:.1f}%
{chr(10).join(per_class)}

헷갈림 표 (행 = 정답, 열 = 판정)
{table(truths, preds, labels)}

주의해서 볼 것
  +극 위를 −극 위로 판정: {danger}장  ← 측정 자리에서만 걸러짐. 0이어야 좋음
  극 방향 사진인데 판정 없음: {missed}장  ← 라인에서는 앞·뒤 카메라 값 또는 측정으로 넘어감

기준 신뢰도별 정답률 (TOP_CONF 정할 때 참고)
{chr(10).join(sweep_lines)}

처리 시간: 첫 장 {first:.0f}ms (모델 준비 포함), 이후 평균 {avg:.1f}ms/장
틀린 사진: wrong\\ 폴더 ({len(rows) - n_ok}장)
"""
    (out / "report.txt").write_text(report, encoding="utf-8")
    with open(out / "results.csv", "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=["file", "truth", "pred", "conf", "ok"])
        w.writeheader()
        w.writerows(rows)
    print(report)
    print(f"결과 폴더: {out}")


if __name__ == "__main__":
    main()
