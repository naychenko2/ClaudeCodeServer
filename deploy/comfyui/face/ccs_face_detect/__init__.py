"""Детектор лиц: рамки всех лиц картинки в порядке номеров.

Использование в графе: самостоятельный узел (ComfyWorkflows.FaceDetect), вход references —
имя одного уже загруженного файла в input/ ComfyUI (как у CcsFacePose). На выходе — текст
ccs_face_detect={"width":W,"height":H,"faces":[{"bbox":[x1,y1,x2,y2],"score":0.87,
"small":false}, ...]}; сбой — ccs_face_detect=error. Лиц нет — "faces":[], это не ошибка.

Лица — объекты, а не массивы: на этапе 3 добавятся необязательные поля (similarity), не
ломая формат. Порядок, пороги и параметры детектора — в core.py воркера; узел их не
повторяет, только проверяет форму ответа.

Тяжёлые зависимости (insightface, onnxruntime, opencv) — в отдельном venv
~/ai-data/face-workers/.venv, как у CcsFacePose: узел тонкий (stdlib + folder_paths),
расчёт делает worker_detect.py отдельным процессом. Ошибка воркера, таймаут (60 с),
отсутствие venv или путей → ccs_face_detect=error и предупреждение в лог ComfyUI, а не
исключение, роняющее граф.

Конфиг:
  CCS_FACE_WORKERS  — путь к workers.json   (по умолчанию ~/ai-data/face-workers/workers.json)
  CCS_FACE_MODELS   — каталог с buffalo_l    (по умолчанию ~/ai-data/face-models)
CCS_FACE_DET_SIZE детектор не читает: сторона детектора — константа core.DET_SIZE.
"""
import json
import logging
import os
import shutil
import signal
import subprocess
import tempfile
import threading
import time

import folder_paths

log = logging.getLogger("ccs_face_detect")

TEXT_KEY = "ccs_face_detect"
RESULT_MARK = "CCS_RESULT "
# Загрузка детектора buffalo_l ≈2–3 с + одна картинка; потолок — минута, как у соседей
DEFAULT_TIMEOUT_S = 60

# Тот же контракт входа, что у CcsFacePose / CcsFaceSimilarity
ALLOWED_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".bmp"}
MAX_REF_BYTES = 50 * 1024 * 1024


def _config():
    path = os.environ.get("CCS_FACE_WORKERS") or os.path.expanduser(
        "~/ai-data/face-workers/workers.json")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _face_models():
    return os.environ.get("CCS_FACE_MODELS") or os.path.expanduser("~/ai-data/face-models")


def _safe_input(name):
    name = (name or "").strip()
    if not name:
        return None
    if os.path.isabs(name) or ".." in name.replace("\\", "/").split("/"):
        raise ValueError(f"недопустимое имя входа: {name}")
    root = os.path.realpath(folder_paths.get_input_directory())
    full = os.path.realpath(os.path.join(root, name))
    if not full.startswith(root + os.sep) or not os.path.isfile(full):
        raise ValueError(f"вход не найден: {name}")
    ext = os.path.splitext(name)[1].lower()
    if ext not in ALLOWED_EXTS:
        raise ValueError(f"расширение не из белого списка: {ext}")
    size = os.path.getsize(full)
    if size > MAX_REF_BYTES:
        raise ValueError(f"картинка больше {MAX_REF_BYTES // (1024*1024)} МБ: {size}")
    return full


def _error(reason):
    """Возврат 'error' с записью причины в лог. Граф НЕ роняем."""
    log.warning("ccs_face_detect: %s", reason)
    return {"ui": {"text": [f"{TEXT_KEY}=error"]}}


def _is_int(v):
    return isinstance(v, int) and not isinstance(v, bool)


def _checked_faces(result):
    """Ответ воркера → {"width", "height", "faces"} строго по контракту или None, если
    форма неожиданная (это error, а не «лиц нет»)."""
    w, h, faces = result.get("width"), result.get("height"), result.get("faces")
    if not (_is_int(w) and _is_int(h) and w > 0 and h > 0 and isinstance(faces, list)):
        return None
    out = []
    for f in faces:
        if not isinstance(f, dict):
            return None
        bbox, score, small = f.get("bbox"), f.get("score"), f.get("small")
        if not (isinstance(bbox, list) and len(bbox) == 4 and all(_is_int(v) for v in bbox)):
            return None
        if not (isinstance(score, (int, float)) and not isinstance(score, bool)):
            return None
        if not isinstance(small, bool):
            return None
        out.append({"bbox": bbox, "score": score, "small": small})
    return {"width": w, "height": h, "faces": out}


class CcsFaceDetect:
    CATEGORY = "image/ccs"
    FUNCTION = "run"
    RETURN_TYPES = ()
    OUTPUT_NODE = True

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                # имя одного файла во входной папке ComfyUI (как у CcsFacePose)
                "references": ("STRING", {"default": "", "multiline": True}),
            },
        }

    @classmethod
    def IS_CHANGED(cls, **kwargs):
        # nan — каждый запуск новая работа; совпадает с CcsFacePose
        return float("nan")

    def run(self, references):
        # 1. Где воркер
        try:
            family = _config()["families"]["detect"]
        except Exception as e:
            return _error(f"нет конфига воркера: {e!r}")
        for key in ("python", "script"):
            if key not in family:
                return _error(f"в workers.json нет ключа {key!r} у семейства detect")
        if not (os.path.isfile(family["python"]) and os.path.isfile(family["script"])):
            return _error("python или скрипт воркера не найдены на диске")

        # 2. Ровно одна картинка из input/
        names = [l.strip() for l in (references or "").splitlines() if l.strip()]
        if len(names) != 1:
            return _error(f"ожидается одно имя файла, получено {len(names)}")
        try:
            in_path = _safe_input(names[0])
        except ValueError as e:
            return _error(str(e))

        # 3. job.json во временной папке и subprocess с воркером (как у CcsFacePose)
        tmp = tempfile.mkdtemp(prefix="ccs-face-detect-")
        job_path = os.path.join(tmp, "job.json")
        lines = []
        started = time.time()
        try:
            with open(job_path, "w", encoding="utf-8") as f:
                json.dump({"image": in_path, "face_models": _face_models()}, f,
                          ensure_ascii=False)

            log.info("ccs_face_detect: старт воркера")
            env = dict(os.environ, PYTHONUNBUFFERED="1",
                       HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
            for key in ("PYTHONPATH", "LD_LIBRARY_PATH", "LD_PRELOAD"):
                env.pop(key, None)
            env.update({str(k): str(v) for k, v in (family.get("env") or {}).items()})
            try:
                proc = subprocess.Popen(
                    [family["python"], family["script"], job_path],
                    env=env,
                    cwd=family.get("cwd") or os.path.dirname(family["script"]),
                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                    start_new_session=True,
                )
            except (FileNotFoundError, PermissionError, OSError) as e:
                return _error(f"не запустить воркера: {type(e).__name__}: {e}")

            def pump():
                for line in proc.stdout:
                    s = line.rstrip("\n")
                    lines.append(s)
                    if not s.startswith(RESULT_MARK):
                        log.info("[face] %s", s[:300])
                    if len(lines) > 200:
                        del lines[:-100]

            reader = threading.Thread(target=pump, daemon=True)
            reader.start()
            while True:
                if proc.poll() is not None:
                    reader.join(timeout=5)
                    break
                if time.time() - started > DEFAULT_TIMEOUT_S:
                    try:
                        os.killpg(proc.pid, signal.SIGKILL)
                    except OSError:
                        pass
                    return _error(f"таймаут воркера {DEFAULT_TIMEOUT_S} с")
                # отмена кнопкой или /interrupt: убиваем воркера, пробрасываем дальше
                try:
                    import comfy.model_management as mm
                    mm.throw_exception_if_processing_interrupted()
                except ImportError:
                    pass
                except BaseException:
                    try:
                        os.killpg(proc.pid, signal.SIGKILL)
                    except OSError:
                        pass
                    raise
                time.sleep(0.5)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

        # 4. Разбор результата
        result = None
        for line in reversed(lines):
            if line.startswith(RESULT_MARK):
                try:
                    result = json.loads(line[len(RESULT_MARK):])
                except json.JSONDecodeError:
                    result = None
                break

        checked = _checked_faces(result) if isinstance(result, dict) else None
        if proc.returncode != 0 or checked is None:
            tail = "\n".join(l for l in lines if not l.startswith(RESULT_MARK))[-2000:]
            return _error(f"воркер вернул код {proc.returncode}: {tail[:300]}")

        log.info("ccs_face_detect: готово, лиц %d, %.2f с, stats=%s",
                 len(checked["faces"]), time.time() - started, result.get("stats"))
        text = json.dumps(checked, ensure_ascii=False, separators=(",", ":"))
        return {"ui": {"text": [f"{TEXT_KEY}={text}"]}}


NODE_CLASS_MAPPINGS = {"CcsFaceDetect": CcsFaceDetect}
NODE_DISPLAY_NAME_MAPPINGS = {"CcsFaceDetect": "CCS Face Detect (buffalo_l)"}
