"""Сходство лица варианта (максимум по всем лицам) с центроидом эталонов персонажа.

Использование в графе: ветка после VAE-декода (тот же тензор IMAGE, что идёт в SaveImage)
заводится во вход image. Во вход references построчно кладутся имена файлов в input ComfyUI
(папка characters/ проекта — у агента есть «Андрей»). На выходе узла — текст
ccs_face_similarity=0.771 либо ccs_face_similarity=none, если лица нет ни в варианте,
ни в одном эталоне. Это не ошибка: граф должен завершиться успешно, чтобы автопрогон
вариантов не падал на одной картинке без лица.

Сходство считается как максимум косинуса каждого найденного лица варианта к L2-центроиду
эталонов: в сценах с несколькими людьми нужное лицо может быть меньше самого крупного
(«Андрей рядом с прохожим» — крупный прохожий тянет сходство вниз).

Тяжёлые зависимости (insightface, onnxruntime, opencv) живут в отдельном venv
(~/ai-data/face-workers/.venv): в venv ComfyUI им не место — numpy<2 и opencv рядом с
torch cu128 ломают весь ComfyUI целиком. Поэтому:

- узел внутри ComfyUI тонкий: stdlib + folder_paths + то, что уже есть в ComfyUI
  (numpy, torch, PIL); никаких импортов insightface/onnxruntime/cv2 на верхнем уровне;
- расчёт делает воркер-скрипт (subprocess): ~/ai-data/face-workers/ccs/worker_similarity.py
  запускается интерпретатором ~/ai-data/face-workers/.venv/bin/python;
- ошибка воркера, таймаут (60 с), отсутствие venv или путей → ccs_face_similarity=none в
  выходе узла и предупреждение в лог ComfyUI, а не исключение, роняющее граф.

Загрузку модели на каждый вызов считаем приемлемой (CPU-onnxruntime, единицы секунд
на фоне ~25 с генерации картинки Qwen-Image); измерено, см. README.

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

log = logging.getLogger("ccs_face_similarity")

TEXT_KEY = "ccs_face_similarity"
RESULT_MARK = "CCS_RESULT "
# Загрузка buffalo_l + эмбеддинги на CPU; в среднем десятки секунд на всё, потолок — минута
DEFAULT_TIMEOUT_S = 60

# Белый список расширений и потолок размера эталонов; тот же контракт, что у CcsAudioWorker
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


def _save_variant_png(image, png_path):
    """ComfyUI IMAGE — torch.Tensor [B,H,W,C] float32 RGB 0..1. Сохраняем как PNG через
    numpy+PIL: оба уже есть в venv ComfyUI; cv2 не нужен."""
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


def _safe_none(reason):
    """Возврат 'none' с записью причины в лог. Граф НЕ роняем."""
    log.warning("ccs_face_similarity: %s", reason)
    return {"ui": {"text": [f"{TEXT_KEY}=none"]}}


class CcsFaceSimilarity:
    CATEGORY = "image/ccs"
    FUNCTION = "run"
    RETURN_TYPES = ()
    OUTPUT_NODE = True

    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            # тензор IMAGE после VAE-декода, тот же, что идёт в SaveImage
            "image": ("IMAGE",),
            # имена файлов во входной папке ComfyUI, по одному на строку (как у CcsAudioWorker)
            "references": ("STRING", {"default": "", "multiline": True}),
        }}

    @classmethod
    def IS_CHANGED(cls, **kwargs):
        # nan — каждый запуск новая работа; совпадает с ccs_audio_worker
        return float("nan")

    def run(self, image, references):
        # 1. Где воркер
        try:
            family = _config()["families"]["similarity"]
        except Exception as e:
            return _safe_none(f"нет конфига воркера: {e!r}")
        for key in ("python", "script"):
            if key not in family:
                return _safe_none(f"в workers.json нет ключа {key!r} у семейства similarity")
        if not (os.path.isfile(family["python"]) and os.path.isfile(family["script"])):
            return _safe_none("python или скрипт воркера не найдены на диске")

        # 2. Безопасный разбор путей эталонов
        in_paths = []
        for line in (references or "").splitlines():
            try:
                p = _safe_input(line)
            except ValueError as e:
                log.warning("ccs_face_similarity: %s", e)
                continue
            if p is not None:
                in_paths.append(p)

        # 3. PNG варианта во временной папке и job.json рядом
        tmp = tempfile.mkdtemp(prefix="ccs-face-")
        variant_path = os.path.join(tmp, "variant.png")
        job_path = os.path.join(tmp, "job.json")
        try:
            try:
                _save_variant_png(image, variant_path)
            except Exception as e:
                return _safe_none(f"вариант не сохранён: {type(e).__name__}: {e}")

            job = {
                "variant": variant_path,
                "references": in_paths,
                "face_models": _face_models(),
                "det_size": _det_size(),
            }
            with open(job_path, "w", encoding="utf-8") as f:
                json.dump(job, f, ensure_ascii=False)

            # 4. Subprocess с воркером. clean env: HF_HUB_OFFLINE/TRANSFORMERS_OFFLINE
            # есть в воркере через машинный конфиг семейства (или прокидываем как fallback);
            # чистим PYTHONPATH/LD_LIBRARY_PATH/LD_PRELOAD — библиотеки ComfyUI (cu128)
            # воркеру чужие, см. CcsAudioWorker.
            started = time.time()
            log.info("ccs_face_similarity: старт воркера, эталонов %d", len(in_paths))
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

        if proc.returncode != 0 or result is None or "similarity" not in result:
            tail = "\n".join(l for l in lines if not l.startswith(RESULT_MARK))[-2000:]
            return _safe_none(f"воркер вернул код {proc.returncode}: {tail[:300]}")

        sim = result["similarity"]
        elapsed = round(time.time() - started, 2)
        stats = result.get("stats") or {}
        log.info("ccs_face_similarity: готово, %d/%d эталонов, %.2f с, stats=%s",
                 stats.get("references_used", "?"), len(in_paths), elapsed, stats)

        if sim is None:
            return {"ui": {"text": [f"{TEXT_KEY}=none"]}}
        return {"ui": {"text": [f"{TEXT_KEY}={sim:.3f}"]}}


NODE_CLASS_MAPPINGS = {"CcsFaceSimilarity": CcsFaceSimilarity}
NODE_DISPLAY_NAME_MAPPINGS = {"CcsFaceSimilarity": "CCS Face Similarity (buffalo_l)"}
