"""Воркер сходства лиц для узла CcsFaceSimilarity (InsightFace + buffalo_l, CPU-onnxruntime).

Запускается отдельным процессом из узла ComfyUI, чтобы venv ComfyUI не трогать
(numpy<2 и opencv рядом с torch cu128 ломают весь ComfyUI целиком). Живёт в
~/ai-data/face-workers/ccs/worker_similarity.py и запускается интерпретатором того
venv, в котором стоят insightface, onnxruntime, opencv-python.

Протокол: argv[1] — путь к job.json. Формат:

{
  "variant": "/abs/path/variant.png",   # то, что посчитал узел из IMAGE-тензора
  "references": ["/abs/ref1.png", ...], # пути в input ComfyUI, проверены узлом
  "face_models": "~/ai-data/face-models",
  "det_size": 640,
  "mask": "/abs/path/mask.png",         # необязательно: PNG режима L, 0/255
}

С маской сходство — максимум только по лицам, чей bbox покрыт маской не меньше чем на 0,5
(core.faces_in_mask); таких нет или маска не читается — null. Без ключа mask — максимум по
всем лицам, как раньше.

Результат: одна строка CCS_RESULT {"similarity": <float|null>, "stats": {...}}
(последняя в stdout). Любая ошибка — CCS_RESULT {"similarity": null, "stats": {...}}
или короткое сообщение и sys.exit(2); узел трактует оба варианта как none.
"""
import json
import os
import sys
import time
import warnings


def _log(msg):
    print(msg, flush=True)


def _all_faces(app, bgr):
    """Все лица в BGR-кадре. В сцене с несколькими людьми нужное лицо может
    быть не самым крупным — отдаём список (с bbox и embedding), максимум косинуса
    к центроиду берёт core. В эталоне берём самое крупное — _biggest_face."""
    faces = app.get(bgr)
    if not faces:
        return []
    return list(faces)


def _biggest_face(faces):
    """Самое крупное лицо по площади bbox. В эталоне могут быть лишние лица
    (случайный человек на групповом фото), и по уверенности первым часто идёт
    именно чужое — а для центроида нужно главное лицо эталона."""
    return max(
        faces,
        key=lambda f: float((f.bbox[2] - f.bbox[0]) * (f.bbox[3] - f.bbox[1])),
    )


def _faces_from_path(app, cv2, path):
    bgr = cv2.imread(path, cv2.IMREAD_COLOR)
    if bgr is None:
        raise ValueError(f"cv2 не прочитал файл: {path}")
    return _all_faces(app, bgr)


def _load_mask(cv2, path, shape):
    """Маска как bool-массив размера варианта; None — файл не читается или пуст."""
    from core import resize_nearest

    raw = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    if raw is None or raw.ndim != 2 or raw.size == 0:
        return None
    h, w = shape
    if raw.shape != (h, w):
        _log(f"маска {raw.shape[1]}x{raw.shape[0]} не совпала с вариантом {w}x{h}, "
             f"растягиваю nearest")
        raw = resize_nearest(raw, h, w)
    return raw >= 128


def main():
    if len(sys.argv) != 2:
        _log("ОШИБКА: использование worker_similarity.py job.json")
        sys.exit(2)
    with open(sys.argv[1], encoding="utf-8") as f:
        job = json.load(f)

    variant = job["variant"]
    refs = job.get("references") or []
    face_models = job.get("face_models") or os.path.expanduser("~/ai-data/face-models")
    det_size = int(job.get("det_size") or 640)
    mask_path = job.get("mask")
    os.makedirs(face_models, exist_ok=True)

    t0 = time.time()
    # Локальные импорты: opencv/insightface/onnxruntime не должны попасть в процесс ComfyUI.
    import cv2
    import numpy as np
    from insightface.app import FaceAnalysis

    # FutureWarning «estimate is deprecated» от insightface.utils.face_align:
    # точечно по категории и модулю, чтобы не глушить чужие варнинги.
    warnings.filterwarnings(
        "ignore",
        category=FutureWarning,
        module="insightface.utils.face_align",
    )

    app = FaceAnalysis(name="buffalo_l", root=face_models, providers=["CPUExecutionProvider"],
                       allowed_modules=["detection", "recognition"])
    app.prepare(ctx_id=-1, det_size=(det_size, det_size))
    load_s = round(time.time() - t0, 2)

    # core.py лежит рядом — воркер и тесты зовут одну и ту же арифметику
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from core import best_similarity_from, faces_in_mask  # noqa: E402

    shape = None
    try:
        bgr = cv2.imread(variant, cv2.IMREAD_COLOR)
        if bgr is None:
            raise ValueError(f"cv2 не прочитал файл: {variant}")
        shape = bgr.shape[:2]
        faces_variant = _all_faces(app, bgr)
    except Exception as e:
        # ошибка детектора на варианте — это none, не валим граф
        _log(f"детектор variant: {type(e).__name__}: {e}")
        faces_variant = []
    faces_total = len(faces_variant)

    faces_masked = None
    if mask_path and faces_variant:
        try:
            mask = _load_mask(cv2, mask_path, shape)
        except Exception as e:
            _log(f"маска: {type(e).__name__}: {e}")
            mask = None
        if mask is None:
            # максимум по всем лицам тут не годится: он вернул бы сходство лица вне маски
            _log(f"ПРЕДУПРЕЖДЕНИЕ: маска не читается, сходство none: {mask_path}")
            faces_variant = []
        else:
            faces_variant = [faces_variant[i]
                             for i in faces_in_mask([f.bbox for f in faces_variant], mask)]
        faces_masked = len(faces_variant)
    elif mask_path:
        faces_masked = 0
    embs_variant = [np.asarray(f.embedding, dtype=np.float32) for f in faces_variant]

    embs_ref = []
    for p in refs:
        try:
            faces = _faces_from_path(app, cv2, p)
        except Exception as ex:
            _log(f"пропускаю эталон {p}: {type(ex).__name__}: {ex}")
            continue
        if not faces:
            continue
        if len(faces) > 1:
            _log(f"эталон {p}: в эталоне {len(faces)} лиц")
        face = _biggest_face(faces)
        arr = np.asarray(face.embedding, dtype=np.float32)
        if not np.isfinite(arr).all():
            _log(f"эталон {p}: нечисловой эмбеддинг, пропускаю")
            continue
        embs_ref.append(arr)

    sim, used = best_similarity_from(embs_variant, embs_ref)

    infer_s = round(time.time() - t0 - load_s, 2)
    total_s = round(time.time() - t0, 2)
    stats = {
        "load_s": load_s,
        "infer_s": infer_s,
        "total_s": total_s,
        "references_total": len(refs),
        "references_used": used,
        "faces_total": faces_total,
        # null — маски в задании не было
        "faces_in_mask": faces_masked,
    }
    result = {"similarity": sim, "stats": stats}
    # CCS_RESULT — последняя строка, чтобы узлу не гадать по хвосту
    sys.stdout.write("CCS_RESULT " + json.dumps(result, ensure_ascii=False) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    main()
