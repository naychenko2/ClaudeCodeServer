"""Ракурс лица: {yaw, pitch, roll} для каждой картинки.

Использование в графе: тензор IMAGE после VAE-декода заводится во вход image. Имена файлов
во входе references — по одному на строку (как у CcsFaceSimilarity, как у CcsAudioWorker).
Узел идёт параллельно с SaveImage: на выходе — текст ccs_face_pose={"file.png": {"yaw":…,
"pitch":…, "roll":…}, ...}. Лицо не найдено → null для файла.

Оси — именованными полями. У InsightFace `face.pose` отдаётся как `np.array([pitch, yaw,
roll])` (сверено с `insightface/model_zoo/landmark.py: pose = np.array([rx, ry, rz])` с
комментарием `#pitch, yaw, roll`; оригинальный путь `transform.matrix2angle` — `x: pitch /
y: yaw / z: roll`). Порядок важен: первая проба перепутала pitch с yaw и получила мусорные
пороги (см. docs/research/face-pose-probe-2026-10.md, §§ 1–9). Воркер проверяет порядок
по исходнику при старте и падает, если версия InsightFace его изменила — иначе граф
выдаёт неверные числа без видимой ошибки.

Тяжёлые зависимости (insightface, onnxruntime, opencv) живут в отдельном venv
~/ai-data/face-workers/.venv, как у CcsFaceSimilarity: venv ComfyUI они ломают. Поэтому:
- узел внутри ComfyUI тонкий: stdlib + folder_paths + то, что уже есть в ComfyUI;
- расчёт делает воркер-скрипт (subprocess) ~/ai-data/face-workers/ccs/worker_pose.py,
  запускается интерпретатором ~/ai-data/face-workers/.venv/bin/python;
- ошибка воркера, таймаут (60 с), отсутствие venv или путей → ccs_face_pose="error" в
  выходе узла и предупреждение в лог ComfyUI, а не исключение, роняющее граф.

Конфиг:
  CCS_FACE_WORKERS  — путь к workers.json                 (по умолчанию ~/ai-data/face-workers/workers.json)
  CCS_FACE_MODELS   — каталог с buffalo_l                  (по умолчанию ~/ai-data/face-models)
  CCS_FACE_DET_SIZE — сторона детектора (квадрат, int)     (по умолчанию 640)
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

log = logging.getLogger("ccs_face_pose")

TEXT_KEY = "ccs_face_pose"
RESULT_MARK = "CCS_RESULT "
# Один прогон на персонажа (≤ 10 фото): загрузка buffalo_l + 0,36 с/фото; потолок — минута
DEFAULT_TIMEOUT_S = 60

# Тот же контракт, что у CcsFaceSimilarity / CcsAudioWorker
ALLOWED_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".bmp"}
MAX_REF_BYTES = 50 * 1024 * 1024


def _config():
    path = os.environ.get("CCS_FACE_WORKERS") or os.path.expanduser(
        "~/ai-data/face-workers/workers.json")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _det_size():
    raw = os.environ.get("CCS_FACE_DET_SIZE", "640")
    try:
        n = int(raw)
        return n if n > 0 else 640
    except ValueError:
        return 640


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
        raise ValueError(f"эталон больше {MAX_REF_BYTES // (1024*1024)} МБ: {size}")
    return full


def _safe_none(reason):
    """Возврат 'error' с записью причины в лог. Граф НЕ роняем."""
    log.warning("ccs_face_pose: %s", reason)
    return {"ui": {"text": [f"{TEXT_KEY}=error"]}}


class CcsFacePose:
    CATEGORY = "image/ccs"
    FUNCTION = "run"
    RETURN_TYPES = ()
    OUTPUT_NODE = True

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                # имена файлов во входной папке ComfyUI, по одному на строку
                # (как у CcsFaceSimilarity). Поддерживается и самостоятельный
                # вызов без варианта (ComfyWorkflows.FacePose(images=…)).
                "references": ("STRING", {"default": "", "multiline": True}),
            },
            "optional": {
                # тензор IMAGE после VAE-декода: если подключён, воркер считает
                # его первым (вариант) и добавляет позу варианта в отдельный
                # канал; в ui.text отдаются только references (вариант человек
                # видит через SaveImage).
                "image": ("IMAGE",),
            }
        }

    @classmethod
    def IS_CHANGED(cls, **kwargs):
        # nan — каждый запуск новая работа; совпадает с CcsFaceSimilarity
        return float("nan")

    def run(self, references, image=None):
        # image — опционален (ComfyUI не передаёт его, если вход не подключён).
        # references — список имён файлов в input/ ComfyUI, multiline STRING.
        # 1. Где воркер
        try:
            family = _config()["families"]["pose"]
        except Exception as e:
            return _safe_none(f"нет конфига воркера: {e!r}")
        for key in ("python", "script"):
            if key not in family:
                return _safe_none(f"в workers.json нет ключа {key!r} у семейства pose")
        if not (os.path.isfile(family["python"]) and os.path.isfile(family["script"])):
            return _safe_none("python или скрипт воркера не найдены на диске")

        # 2. Безопасный разбор путей эталонов
        in_paths = []
        for line in (references or "").splitlines():
            try:
                p = _safe_input(line)
            except ValueError as e:
                log.warning("ccs_face_pose: %s", e)
                continue
            if p is not None:
                in_paths.append(p)

        # 3. PNG варианта во временной папке и job.json рядом. Воркеру передаём
        # все картинки единым списком (variant первым, дальше references);
        # в ответе поза для каждого по тому же индексу — это согласуется со
        # швом ILocalImageMedia.FacePoseAsync(IReadOnlyList<byte[]>).
        tmp = tempfile.mkdtemp(prefix="ccs-face-pose-")
        variant_path = os.path.join(tmp, "variant.png")
        job_path = os.path.join(tmp, "job.json")
        try:
            # _save_variant_png подключается локально, чтобы не светить numpy/torch
            # в шапке модуля (ComfyUI подгружает файл при сканировании custom_nodes)
            from . import _save_variant_png
            # Если image — None/пустой тензор (самостоятельный вызов без генерации),
            # воркер получает только references; variant.png не создаётся.
            has_variant = image is not None
            if has_variant:
                try:
                    _save_variant_png(image, variant_path)
                except Exception as e:
                    return _safe_none(f"вариант не сохранён: {type(e).__name__}: {e}")

            all_paths = ([variant_path] if has_variant else []) + in_paths
            job = {
                "images": all_paths,
                "face_models": _face_models(),
                "det_size": _det_size(),
            }
            with open(job_path, "w", encoding="utf-8") as f:
                json.dump(job, f, ensure_ascii=False)

            # 4. Subprocess с воркером (как у CcsFaceSimilarity)
            started = time.time()
            log.info("ccs_face_pose: старт воркера, эталонов %d", len(in_paths))
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
                return _safe_none(f"не запустить воркера: {type(e).__name__}: {e}")

            lines = []

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
            try:
                while True:
                    if proc.poll() is not None:
                        reader.join(timeout=5)
                        break
                    if time.time() - started > DEFAULT_TIMEOUT_S:
                        try:
                            os.killpg(proc.pid, signal.SIGKILL)
                        except OSError:
                            pass
                        return _safe_none(f"таймаут воркера {DEFAULT_TIMEOUT_S} с")
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
                try:
                    os.remove(job_path)
                except OSError:
                    pass
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

        # 5. Разбор результата
        result = None
        for line in reversed(lines):
            if line.startswith(RESULT_MARK):
                try:
                    result = json.loads(line[len(RESULT_MARK):])
                except json.JSONDecodeError:
                    result = None
                break

        if proc.returncode != 0 or result is None or "poses" not in result:
            tail = "\n".join(l for l in lines if not l.startswith(RESULT_MARK))[-2000:]
            return _safe_none(f"воркер вернул код {proc.returncode}: {tail[:300]}")

        # 6. Ответ воркера: {"poses": [pose|null, ...]} — в том же порядке, что images.
        # Если был variant, images[0] — он; иначе images[0] — первый ref. В ui.text
        # отдаём только references: variant — служебный, его позу человек видит
        # прямо через SaveImage; при самостоятельном вызове (без variant) выдаём
        # результат по всему списку.
        poses = result["poses"]
        elapsed = round(time.time() - started, 2)
        stats = result.get("stats") or {}
        log.info("ccs_face_pose: готово, %d/%d фото, %.2f с, stats=%s",
                 stats.get("used", "?"), len(in_paths) + (1 if has_variant else 0),
                 elapsed, stats)

        out = {}
        offset = 1 if has_variant else 0
        for idx, path in enumerate(in_paths):
            pose_idx = idx + offset
            entry = {"yaw": None, "pitch": None, "roll": None}
            if pose_idx < len(poses) and poses[pose_idx] is not None:
                p = poses[pose_idx]
                if not all(isinstance(p.get(x), (int, float)) for x in ("yaw", "pitch", "roll")):
                    log.warning("ccs_face_pose: путь #%d без числовых углов, пропускаю", idx)
                else:
                    entry = {"yaw": p["yaw"], "pitch": p["pitch"], "roll": p["roll"]}
            out[path] = entry

        text = json.dumps(out, ensure_ascii=False, sort_keys=True)
        return {"ui": {"text": [f"{TEXT_KEY}={text}"]}}


def _save_variant_png(image, png_path):
    """ComfyUI IMAGE — torch.Tensor [B,H,W,C] float32 RGB 0..1. Сохраняем как PNG через
    numpy+PIL: оба уже есть в venv ComfyUI; cv2 не нужен. Копия CcsFaceSimilarity._save_variant_png,
    повторять не стали: воркеры тестируются отдельно."""
    import numpy as np
    from PIL import Image

    if hasattr(image, "detach") and hasattr(image, "cpu"):
        import torch
        arr = image[0].detach().cpu().numpy()
    else:
        arr = np.asarray(image)
    if arr.ndim == 4:
        arr = arr[0]
    arr = np.clip(arr * 255.0, 0, 255).astype(np.uint8)
    Image.fromarray(arr).save(png_path)


NODE_CLASS_MAPPINGS = {"CcsFacePose": CcsFacePose}
NODE_DISPLAY_NAME_MAPPINGS = {"CcsFacePose": "CCS Face Pose (buffalo_l)"}