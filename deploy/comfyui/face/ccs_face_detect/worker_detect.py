"""Воркер детектора лиц для узла CcsFaceDetect (InsightFace + buffalo_l, CPU-onnxruntime).

Запускается отдельным процессом из узла ComfyUI, как worker_pose.py: venv ComfyUI не
трогаем. Живёт в ~/ai-data/face-workers/ccs/ccs_face_detect/worker_detect.py.

Протокол: argv[1] — путь к job.json:

{
  "image": "/abs/path/img.png",
  "face_models": "~/ai-data/face-models"
}

Результат — одна строка CCS_RESULT {"width": W, "height": H, "faces": [...], "stats": {...}}.
width/height — размер картинки, которую прочитал cv2 (с учётом EXIF-поворота: бэкенд сверяет
его со своим размером). faces — уже в порядке номеров (core.arrange_faces). Лиц нет —
"faces": [], это не ошибка. Картинка не читается — без CCS_RESULT и с кодом 2: узел вернёт
error.

Только модуль detection: ни эмбеддинги, ни landmarks детектору не нужны, загрузка ≈2–3 с.
Параметры детектора — константы core.py, а не окружение (детерминизм номеров).
"""
import json
import os
import sys
import time


def _log(msg):
    print(msg, flush=True)


def main():
    if len(sys.argv) != 2:
        _log("ОШИБКА: использование worker_detect.py job.json")
        sys.exit(2)
    with open(sys.argv[1], encoding="utf-8") as f:
        job = json.load(f)

    image = job.get("image") or ""
    face_models = job.get("face_models") or os.path.expanduser("~/ai-data/face-models")
    os.makedirs(face_models, exist_ok=True)

    # core.py лежит рядом — воркер и тесты зовут одну и ту же арифметику
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import core

    t0 = time.time()
    # Локальные импорты: opencv/insightface/onnxruntime не должны попасть в процесс ComfyUI
    import cv2
    from insightface.app import FaceAnalysis

    bgr = cv2.imread(image, cv2.IMREAD_COLOR)
    if bgr is None:
        _log(f"ОШИБКА: cv2 не прочитал картинку: {image}")
        sys.exit(2)
    height, width = int(bgr.shape[0]), int(bgr.shape[1])

    app = FaceAnalysis(name="buffalo_l", root=face_models, providers=["CPUExecutionProvider"],
                       allowed_modules=["detection"])
    app.prepare(ctx_id=-1, det_thresh=core.MIN_SCORE, det_size=(core.DET_SIZE, core.DET_SIZE))
    load_s = round(time.time() - t0, 2)

    detected = app.get(bgr)
    raw = [(f.bbox, f.det_score) for f in detected]
    faces = core.arrange_faces(raw, width, height)

    stats = {
        "load_s": load_s,
        "infer_s": round(time.time() - t0 - load_s, 2),
        "detected": len(raw),
        "faces": len(faces),
        "det_size": core.DET_SIZE,
    }
    result = {"width": width, "height": height, "faces": faces, "stats": stats}
    # CCS_RESULT — последняя строка, чтобы узлу не гадать по хвосту
    sys.stdout.write("CCS_RESULT " + json.dumps(result, ensure_ascii=False) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    main()
