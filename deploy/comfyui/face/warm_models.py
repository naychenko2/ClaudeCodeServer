"""Предзагрузка весов InsightFace (buffalo_l): узел запускается с офлайн-режимом, чтобы
скачивание не держало общую очередь GPU. Запускается интерпретатором того venv, чьи
веса грузит.

warm_models.py [каталог весов]
"""
import os
import sys

models = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "CCS_FACE_MODELS") or os.path.expanduser("~/ai-data/face-models")

# buffalo_l — единственный нужный пакет моделей; det_model_2d, rec_model и пр.
# allowed_modules ограничивает набор подмоделей: детектор + распознаватель. Без
# landmark_3d_68 и прочего узел работает на ~3 с быстрее и не сыплет FutureWarning
# от landmark_3d_68.
from insightface.app import FaceAnalysis

os.makedirs(models, exist_ok=True)
# ctx_id=-1 явно отключает GPU: buffalo_l идёт на onnxruntime, его провайдер задаётся
# в узле; здесь только качаем веса и убеждаемся, что они встали в <models>/models/buffalo_l
app = FaceAnalysis(name="buffalo_l", root=models, providers=["CPUExecutionProvider"],
                   allowed_modules=["detection", "recognition"])
app.prepare(ctx_id=-1, det_size=(640, 640))
print(os.path.join(models, "models", "buffalo_l"), flush=True)