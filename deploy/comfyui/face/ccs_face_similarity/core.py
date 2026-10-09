"""Чистая арифметика сходства лиц.

Живёт без зависимостей: только numpy. Импортируется и узлом ComfyUI для редких проверок,
и воркером — и тестами. Узлу сам модуль не нужен, но unit-тесты на нём не зависят от
insightface/cv2.

Контракт:
- drop_nonfinite(embs)        — отбрасывает векторы с NaN/Inf, возвращает список.
- centroid_l2(embs)           — L2-нормализованный центроид непустого списка (или None).
- cosine(a, b)                — косинус двух 1D-массивов.
- similarity_from(variant, refs) — собирает всё: пропуски NaN/Inf, пустые эталоны, центроид.
"""
import numpy as np


def drop_nonfinite(embs):
    """Убирает эмбеддинги с NaN/Inf — битый файл или чужой формат разносит центроид в NaN,
    в ui тогда уезжает «nan»."""
    out = []
    for e in embs:
        arr = np.asarray(e, dtype=np.float32)
        if not np.isfinite(arr).all():
            continue
        out.append(arr)
    return out


def centroid_l2(embs):
    """L2-нормализованный центроид непустого списка L2-нормализованных эмбеддингов.

    Шаг 1: делим каждый на свою норму — иначе эталоны с большей длиной доминируют.
    Шаг 2: среднее.
    Шаг 3: делим итог на норму — cosine инвариантен к длине, но без шага численная
    «длина центроида» не равна 1, что закладывают в L2-нормировки дальше по графу."""
    ems = drop_nonfinite(embs)
    if not ems:
        return None
    normed = [e / np.linalg.norm(e) for e in ems]
    centroid = np.mean(normed, axis=0)
    return centroid / np.linalg.norm(centroid)


def cosine(a, b):
    a = np.asarray(a, dtype=np.float32)
    b = np.asarray(b, dtype=np.float32)
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-12))


def similarity_from(emb_variant, embs_ref):
    """Сходство эмбеддинга варианта с центроидом эталонов. None — лица нет или все эталоны
    с NaN/Inf. Возвращает (similarity, used_refs) для stats воркера."""
    centroid = centroid_l2(embs_ref)
    used = 0 if centroid is None else len(embs_ref)
    if emb_variant is None or centroid is None:
        return None, used
    arr = np.asarray(emb_variant, dtype=np.float32)
    if not np.isfinite(arr).all():
        return None, used
    return cosine(arr, centroid), used


def biggest_face(app, bgr):
    """Самое крупное лицо в BGR-кадре (по площади bbox; при равенстве — последнее попавшееся).
    Устойчиво к случаю, когда детекторы прыгают по score."""
    faces = app.get(bgr)
    if not faces:
        return None
    return max(faces, key=lambda f: (f.bbox[2] - f.bbox[0]) * (f.bbox[3] - f.bbox[1]))
