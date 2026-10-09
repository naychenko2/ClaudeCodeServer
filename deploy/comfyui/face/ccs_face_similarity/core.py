"""Чистая арифметика сходства лиц.

Живёт без зависимостей: только numpy. Импортируется и узлом ComfyUI для редких проверок,
и воркером — и тестами. Узлу сам модуль не нужен, но unit-тесты на нём не зависят от
insightface/cv2.

Контракт:
- drop_nonfinite(embs)           — отбрасывает векторы с NaN/Inf, возвращает список.
- centroid_l2(embs)              — L2-нормализованный центроид непустого списка (или None).
- cosine(a, b)                   — косинус двух 1D-массивов.
- best_similarity_from(embs_variant, embs_ref) — сходство = максимум косинуса каждого
  эмбеддинга варианта к центроиду эталонов; None — лиц в варианте нет или все эталоны
  с NaN/Inf. Возвращает (similarity, used_refs) для stats воркера.
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


def best_similarity_from(embs_variant, embs_ref):
    """Сходство варианта = максимум косинуса каждого эмбеддинга лица к L2-центроиду
    эталонов. В сценах с несколькими людьми самым крупным может оказаться не тот, кто нужен
    (Артём: «Андрей рядом с …» — 0,549 на крупном фоне, нужное лицо — на фоне меньше);
    максимум по всем лицам берёт правильное.

    None возвращается в трёх случаях: в варианте нет лиц (пустой список), все эталоны
    с NaN/Inf (центроид не построить), все эмбеддинги варианта с NaN/Inf. Возвращает
    (similarity, used_refs) для stats воркера: used_refs — длина списка после drop_nonfinite,
    даже если сходство получилось None из-за варианта."""
    centroid = centroid_l2(embs_ref)
    used = 0 if centroid is None else len(drop_nonfinite(embs_ref))
    if not embs_variant or centroid is None:
        return None, used
    best = None
    for e in embs_variant:
        arr = np.asarray(e, dtype=np.float32)
        if not np.isfinite(arr).all():
            continue
        s = cosine(arr, centroid)
        if best is None or s > best:
            best = s
    return best, used
