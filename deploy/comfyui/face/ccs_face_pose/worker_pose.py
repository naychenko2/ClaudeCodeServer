"""Воркер ракурса лица для узла CcsFacePose (InsightFace + buffalo_l, CPU-onnxruntime).

Запускается отдельным процессом из узла ComfyUI, чтобы venv ComfyUI не трогать
(numpy<2 и opencv рядом с torch cu128 ломают весь ComfyUI целиком). Живёт в
~/ai-data/face-workers/ccs/worker_pose.py и запускается интерпретатором того venv,
в котором стоят insightface, onnxruntime, opencv-python.

Протокол: argv[1] — путь к job.json. Формат:

{
  "images": ["/abs/path/img1.png", ...],  # все картинки: variant + references, в этом порядке
  "face_models": "~/ai-data/face-models",
  "det_size": 640,
}

Результат: одна строка CCS_RESULT {"poses": [...], "stats": {...}}.
poses — массив той же длины, что и images; каждый элемент — {yaw, pitch, roll}
(градусы, именованные поля) или null, если лицо не найдено. Любая ошибка —
CCS_RESULT {"poses": null, "stats": {...}} или короткое сообщение и sys.exit(2);
узел трактует оба варианта как error.

Самопроверка порядка осей при старте: первая проба (см. docs/research/face-pose-probe-
2026-10.md, §§ 1–9) перепутала pitch с yaw, и пороги из неё отправились в отчёт.
Чтобы не повторить, читаем исходник insightface/model_zoo/landmark.py в установленном
venv и падаем, если не находим ожидаемый паттерн: `pose = np.array([rx, ry, rz])` с
комментарием `#pitch, yaw, roll`. Этот же тест покрыт pytest'ом — воркер должен
упасть на собственной проверке, если версия InsightFace сменит порядок, иначе
граф начнёт выдавать неверные числа без видимой ошибки.
"""
import inspect
import json
import os
import re
import sys
import time
import warnings


def _log(msg):
    print(msg, flush=True)


# Канонический паттерн из insightface/model_zoo/landmark.py:115 (проверено в venv 2.1):
#   pose = np.array( [rx, ry, rz], dtype=np.float32 )
#   face['pose'] = pose #pitch, yaw, roll
# Если исходник сменился — упадём при старте, узел вернёт error.
EXPECTED_POSE_LINE = re.compile(
    r"pose\s*=\s*np\.array\(\s*\[rx\s*,\s*ry\s*,\s*rz\s*\][^\)]*\)"
)
EXPECTED_COMMENT = re.compile(r"#\s*pitch\s*,\s*yaw\s*,\s*roll")


def _verify_pose_order(landmark_module_or_source):
    """Сверяет исходник landmark.py с ожидаемым паттерном порядка осей.
    Падает, если паттерн не найден или порядок комментария не сводится к pitch/yaw/roll.

    Принимает модуль (читает исходник через inspect.getsource) или сразу строку —
    так тестам не нужен реальный InsightFace."""
    if isinstance(landmark_module_or_source, str):
        source = landmark_module_or_source
    else:
        try:
            source = inspect.getsource(landmark_module_or_source)
        except (OSError, TypeError) as e:
            raise RuntimeError(f"не прочитать исходник landmark.py: {type(e).__name__}: {e}")

    lines = source.splitlines()
    pose_idx = None
    for i, line in enumerate(lines):
        if EXPECTED_POSE_LINE.search(line):
            pose_idx = i
            pose_line = line
            break
    if pose_idx is None:
        raise RuntimeError(
            "порядок осей в order сменился: не нашёл `pose = np.array([rx, ry, rz])` "
            "в исходнике landmark.py — проверь новую версию InsightFace руками и "
            "обнови EXPECTED_* и описание в worker_pose.py"
        )
    # комментарий идёт рядом (строкой ниже в исходнике 2.1)
    nearby = "\n".join(lines[max(0, pose_idx - 1):pose_idx + 3])
    if not EXPECTED_COMMENT.search(nearby):
        raise RuntimeError(
            "порядок осей в order сменился: возле `pose = np.array([rx, ry, rz])` "
            "нет комментария `#pitch, yaw, roll` — проверь новую версию InsightFace"
        )
    # ну и сам комментарий должен идти про питч-яу-ролл именно в этом порядке
    m = EXPECTED_COMMENT.search(nearby)
    comment = m.group(0).lower()
    if "pitch" not in comment or "yaw" not in comment or "roll" not in comment:
        raise RuntimeError(
            f"порядок осей в order не сводится к pitch/yaw/roll: {m.group(0)!r}"
        )
    _log(f"order_check=ok (landmark.py строка {pose_idx + 1}: {pose_line.strip()})")


def _biggest_face(faces):
    """Самое крупное лицо по площади bbox. Лиц на фото может быть несколько (групповое);
    по уверенности первым часто идёт чужое, а для ракурса нужно главное лицо эталона."""
    return max(
        faces,
        key=lambda f: float((f.bbox[2] - f.bbox[0]) * (f.bbox[3] - f.bbox[1])),
    )


def _poses_for_path(app, path):
    """Возвращает dict {yaw, pitch, roll} для самого крупного лица или None, если лиц нет.
    face.pose у InsightFace — [pitch, yaw, roll] (сверено через _verify_pose_order)."""
    import cv2
    bgr = cv2.imread(path, cv2.IMREAD_COLOR)
    if bgr is None:
        raise ValueError(f"cv2 не прочитал файл: {path}")
    faces = app.get(bgr)
    if not faces:
        return None
    face = _biggest_face(faces)
    pose = face.pose  # [pitch, yaw, roll] — порядок проверен при старте
    return {"yaw": float(pose[1]), "pitch": float(pose[0]), "roll": float(pose[2])}


def main():
    if len(sys.argv) != 2:
        _log("ОШИБКА: использование worker_pose.py job.json")
        sys.exit(2)
    with open(sys.argv[1], encoding="utf-8") as f:
        job = json.load(f)

    images = job.get("images") or []
    face_models = job.get("face_models") or os.path.expanduser("~/ai-data/face-models")
    det_size = int(job.get("det_size") or 640)
    os.makedirs(face_models, exist_ok=True)

    t0 = time.time()
    # Локальные импорты: opencv/insightface/onnxruntime не должны попасть в процесс ComfyUI.
    import cv2
    import numpy as np
    from insightface.app import FaceAnalysis
    import insightface.model_zoo.landmark as _landmark_mod

    # Самопроверка порядка осей — до загрузки модели: если упадём, узел получит error
    # и не будет гонять buffalo_l ради битых углов. Подсовываем исходник строкой:
    # так функция принимает и модуль (через inspect.getsource), и текст напрямую —
    # последним пользуются тесты, чтобы не зависеть от install insightface.
    try:
        _landmark_source = inspect.getsource(_landmark_mod)
    except (OSError, TypeError) as e:
        raise RuntimeError(f"не прочитать исходник landmark.py: {type(e).__name__}: {e}")
    _verify_pose_order(_landmark_source)

    warnings.filterwarnings(
        "ignore",
        category=FutureWarning,
        module="insightface.utils.face_align",
    )

    app = FaceAnalysis(name="buffalo_l", root=face_models, providers=["CPUExecutionProvider"],
                       allowed_modules=["detection", "landmark_2d_106", "landmark_3d_68"])
    app.prepare(ctx_id=-1, det_size=(det_size, det_size))
    load_s = round(time.time() - t0, 2)

    poses = []
    used = 0
    for path in images:
        try:
            res = _poses_for_path(app, path)
        except Exception as ex:
            _log(f"пропускаю {path}: {type(ex).__name__}: {ex}")
            res = None
        if res is not None:
            used += 1
        poses.append(res)

    infer_s = round(time.time() - t0 - load_s, 2)
    total_s = round(time.time() - t0, 2)
    stats = {
        "load_s": load_s,
        "infer_s": infer_s,
        "total_s": total_s,
        "total": len(images),
        "used": used,
    }
    result = {"poses": poses, "stats": stats}
    # CCS_RESULT — последняя строка, чтобы узлу не гадать по хвосту
    sys.stdout.write("CCS_RESULT " + json.dumps(result, ensure_ascii=False) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    main()