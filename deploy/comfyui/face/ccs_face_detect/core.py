"""Чистая арифметика детектора лиц: отбор, порядок и признак «мелкое».

Живёт без insightface/cv2: только numpy. Воркер зовёт её на сырых рамках детектора, тесты —
напрямую. Пороги и параметры детектора — здесь, а не в C#: одна точка правды для человека
(рамки на картинке) и агента (номер лица).

Контракт arrange_faces(raw, width, height) → список лиц в порядке номеров:
  1. рамка округляется до целых пикселей и обрезается краями картинки;
  2. отбрасываются лица со score < MIN_SCORE;
  3. если лиц больше MAX_FACES — остаются MAX_FACES самых крупных по площади рамки;
  4. порядок слева направо по центру рамки по x; если центры двух лиц по x ближе половины
     ширины меньшего из них — между ними сверху вниз по центру y;
  5. лицо с короткой стороной рамки меньше SMALL_SIDE_PX — "small": true.
Номера (индекс + 1) присваивает бэкенд.
"""
import functools

import numpy as np

# Параметры детектора фиксированы, а не берутся из окружения: одинаковые байты картинки
# обязаны давать одни и те же рамки и номера у человека и у агента
DET_SIZE = 640
MIN_SCORE = 0.5
MAX_FACES = 8
# Qwen не держит сходство на мелком лице, ArcFace на лице меньше ~80 px ненадёжен
SMALL_SIDE_PX = 64
SCORE_DIGITS = 4


def int_bbox(bbox, width, height):
    """Рамка детектора (float, может вылезать за край) → [x1, y1, x2, y2] целыми пикселями
    внутри картинки."""
    b = np.asarray(bbox, dtype=np.float64).reshape(4)
    x1, y1, x2, y2 = (int(round(float(v))) for v in b)
    x1 = min(max(x1, 0), width)
    x2 = min(max(x2, 0), width)
    y1 = min(max(y1, 0), height)
    y2 = min(max(y2, 0), height)
    return [x1, y1, x2, y2]


def _area(b):
    return (b[2] - b[0]) * (b[3] - b[1])


def _center(b):
    return (b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0


def _reading_order(a, b):
    ax, ay = _center(a["bbox"])
    bx, by = _center(b["bbox"])
    narrower = min(a["bbox"][2] - a["bbox"][0], b["bbox"][2] - b["bbox"][0])
    if abs(ax - bx) < narrower / 2.0:
        if ay != by:
            return -1 if ay < by else 1
    if ax != bx:
        return -1 if ax < bx else 1
    return 0


def arrange_faces(raw, width, height):
    """raw — список пар (bbox, score) от детектора в его порядке. Возвращает список
    {"bbox": [x1, y1, x2, y2], "score": float, "small": bool} в порядке номеров."""
    faces = []
    for bbox, score in raw:
        score = float(score)
        if not np.isfinite(score) or score < MIN_SCORE:
            continue
        b = int_bbox(bbox, width, height)
        if b[2] <= b[0] or b[3] <= b[1]:
            continue
        faces.append({"bbox": b, "score": round(score, SCORE_DIGITS)})

    if len(faces) > MAX_FACES:
        # sorted устойчив: при равной площади остаётся то лицо, что детектор дал раньше
        faces = sorted(faces, key=lambda f: -_area(f["bbox"]))[:MAX_FACES]

    faces = sorted(faces, key=functools.cmp_to_key(_reading_order))

    for f in faces:
        b = f["bbox"]
        f["small"] = min(b[2] - b[0], b[3] - b[1]) < SMALL_SIDE_PX
    return faces
