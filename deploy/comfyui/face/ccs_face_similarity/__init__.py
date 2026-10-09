"""Сходство самого крупного лица в варианте с центроидом эталонов персонажа (InsightFace + buffalo_l).

Использование в графе: ветка после VAE-декода (тот же тензор IMAGE, что идёт в SaveImage)
заводится во вход image. Во вход references построчно кладутся имена файлов в input ComfyUI
(папка characters/ проекта — у агента есть «Андрей»). На выходе узла — текст
ccs_face_similarity=0.771 либо ccs_face_similarity=none, если лица нет ни в варианте,
ни в одном эталоне. Это не ошибка: граф должен завершиться успешно, чтобы автопрогон
вариантов не падал на одной картинке без лица.

Модель загружается лениво и держится между вызовами — InsightFace держит свои буферы в
памяти, повторная инициализация отжирает секунды. CPU-исполнитель onnx-runtime: в venv
ComfyUI свой torch и свои onnxruntime-конфликты; ставить onnxruntime-gpu опасно именно
тем, что ломает весь ComfyUI целиком, поэтому пусть ArcFace считается медленнее, чем слом
общего процесса.

Безопасность: имена файлов references валидируются как у audio-узла — без абсолютных
путей и «..». Внутри узла никаких записей на хост нет.

Конфиг:
  CCS_FACE_MODELS  каталог с buffalo_l             (по умолчанию ~/ai-data/face-models)
  CCS_FACE_DET_SIZE сторона детектора, квадрат      (по умолчанию 640)
"""
import logging
import os

import folder_paths
import numpy as np
import torch

log = logging.getLogger("ccs_face_similarity")

# Модульный кеш: FaceAnalysis тяжёлый, держится на весь процесс ComfyUI
_FACE_APP = None
_FACE_APP_ROOT = None

# Значение ccs_face_similarity= ставится в ui.text, валидаторов нет
TEXT_KEY = "ccs_face_similarity"


def _models_root():
    return os.environ.get("CCS_FACE_MODELS") or os.path.expanduser("~/ai-data/face-models")


def _det_size():
    raw = os.environ.get("CCS_FACE_DET_SIZE", "640")
    try:
        n = int(raw)
        return n if n > 0 else 640
    except ValueError:
        return 640


def _get_app():
    """Ленивая инициализация: первый вызов тянет buffalo_l с диска (install.sh кладёт
    его офлайн) и собирает детектор. Дальше — переиспользование."""
    global _FACE_APP, _FACE_APP_ROOT
    root = _models_root()
    if _FACE_APP is None or _FACE_APP_ROOT != root:
        # ленивый импорт: сам модуль тянет opencv-python и onnxruntime, нельзя падать на
        # этапе сканирования custom_nodes, если они ещё не установлены
        from insightface.app import FaceAnalysis

        os.makedirs(root, exist_ok=True)
        _FACE_APP = FaceAnalysis(
            name="buffalo_l",
            root=root,
            providers=["CPUExecutionProvider"],
        )
        _FACE_APP.prepare(ctx_id=-1, det_size=(_det_size(), _det_size()))
        _FACE_APP_ROOT = root
        log.info("ccs_face_similarity: buffalo_l загружен из %s", root)
    return _FACE_APP


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
    return full


def _tensor_to_bgr(image):
    """ComfyUI IMAGE — torch.Tensor [B,H,W,C], float32, RGB, значения 0..1. Возвращаем
    numpy BGR uint8 — формат, который ждёт insightface."""
    if isinstance(image, torch.Tensor):
        arr = image[0].detach().cpu().numpy()
    else:
        arr = np.asarray(image)
    if arr.ndim == 4:
        arr = arr[0]
    arr = np.clip(arr * 255.0, 0, 255).astype(np.uint8)
    return arr[:, :, ::-1].copy()  # RGB → BGR


def _biggest_face_embedding(app, bgr):
    faces = app.get(bgr)
    if not faces:
        return None
    # det_score и bbox — оба признака «главного лица»; на тестовом материале они
    # совпадают, но bbox устойчивее к случаю, когда уверенность детектора скачет
    return max(faces, key=lambda f: (f.bbox[2] - f.bbox[0]) * (f.bbox[3] - f.bbox[1])).embedding


def _cosine(a, b):
    a = np.asarray(a, dtype=np.float32)
    b = np.asarray(b, dtype=np.float32)
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-12))


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
            # имена файлов во входной папке ComfyUI, по одному на строку
            "references": ("STRING", {"default": "", "multiline": True}),
        }}

    @classmethod
    def IS_CHANGED(cls, **kwargs):
        # одна и та же пара вариант+эталоны всегда пересчитывается: для батча — это
        # правильно, для единственного вызова — безвредно; кэшировать нечего
        return float("nan")

    def run(self, image, references):
        try:
            app = _get_app()
        except Exception as e:
            log.exception("ccs_face_similarity: не удалось инициализировать InsightFace")
            return {"ui": {"text": [f"{TEXT_KEY}=error:{type(e).__name__}"]}}

        bgr = _tensor_to_bgr(image)
        try:
            emb_image = _biggest_face_embedding(app, bgr)
        except Exception as e:
            log.exception("ccs_face_similarity: ошибка детектора лица на варианте")
            return {"ui": {"text": [f"{TEXT_KEY}=error:detect"]}}

        embs_ref = []
        for line in (references or "").splitlines():
            try:
                path = _safe_input(line)
            except ValueError as e:
                log.warning("ccs_face_similarity: %s", e)
                continue
            if path is None:
                continue
            ref_img = _load_bgr(path)
            try:
                emb = _biggest_face_embedding(app, ref_img)
            except Exception as e:
                log.warning("ccs_face_similarity: пропускаю эталон %s: %s", line, e)
                continue
            if emb is not None:
                embs_ref.append(emb)

        if emb_image is None or not embs_ref:
            return {"ui": {"text": [f"{TEXT_KEY}=none"]}}

        # L2-нормализованный центроид эталонов: сначала единичные нормы, потом среднее
        # и нормирование итога — иначе эмбеддинги с большей длиной будут доминировать
        normed = [e / np.linalg.norm(e) for e in embs_ref]
        centroid = np.mean(normed, axis=0)
        centroid = centroid / np.linalg.norm(centroid)
        sim = _cosine(emb_image, centroid)
        return {"ui": {"text": [f"{TEXT_KEY}={sim:.3f}"]}}


def _load_bgr(path):
    """Чтение файла эталона в BGR через cv2 — он же в нашей цепочке."""
    import cv2

    bgr = cv2.imread(path, cv2.IMREAD_COLOR)
    if bgr is None:
        raise ValueError(f"cv2 не прочитал файл: {path}")
    return bgr


NODE_CLASS_MAPPINGS = {"CcsFaceSimilarity": CcsFaceSimilarity}
NODE_DISPLAY_NAME_MAPPINGS = {"CcsFaceSimilarity": "CCS Face Similarity (buffalo_l)"}