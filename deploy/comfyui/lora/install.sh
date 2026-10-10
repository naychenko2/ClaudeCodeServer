#!/bin/bash
# Установка воркера обучения LoRA (AD-026, op lora_train / lora_delete) на машину с ComfyUI.
# Идемпотентно: повторный запуск доставляет недостающее.
#
#   LORA_ROOT   — venv и репозиторий ai-toolkit       (по умолчанию ~/ai-data/audio-workers)
#   COMFY_ROOT  — ComfyUI                              (по умолчанию ~/ComfyUI-h3)
#   COMFY_MODELS — каталог models/ (loras и т. п.)     (по умолчанию ~/ai-data/comfy-models)
#   --link-only — без окружений и весов: обновить узел и воркеры после выкатки новой версии
#
# Сценарий:
#   * ai-toolkit ставится в свой venv (отдельный от ComfyUI): ему нужны свои torch / diffusers.
#   * InsightFace для кропов лиц ставится в тот же venv — у ComfyUI его нет.
#   * Узел очереди — тот же CcsAudioWorker (правка в deploy/comfyui/audio/.../__init__.py),
#     семейство 'lora' прописывается в workers.json на машине.
#   * Веса qwen_image_2.1_int8_convrot лежат в ComfyUI/diffusion_models и попадают в ai-toolkit
#     через симлинк.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
LORA_ROOT=${LORA_ROOT:-$HOME/ai-data/audio-workers}
COMFY_ROOT=${COMFY_ROOT:-$HOME/ComfyUI-h3}
COMFY_MODELS=${COMFY_MODELS:-$HOME/ai-data/comfy-models}
export PATH=$HOME/.local/bin:$PATH
TI="--index-url https://download.pytorch.org/whl/cu128 --extra-index-url https://pypi.org/simple --index-strategy unsafe-best-match"
T28="torch==2.8.0+cu128 torchvision==0.23.0+cu128"

mkdir -p "$LORA_ROOT"
# Узел и воркеры — копией вне git-дерева: симлинк ComfyUI на рабочую копию сломался бы при
# первом checkout другой ветки (или удалении worktree). Повторный запуск install.sh обновляет
# копию
DEPLOY="$LORA_ROOT/ccs"
mkdir -p "$DEPLOY"
rsync -a --delete --exclude '__pycache__' "$HERE/" "$DEPLOY/"

venv() { # venv <каталог> — создаёт .venv, если его нет
  mkdir -p "$LORA_ROOT/$1"
  [ -d "$LORA_ROOT/$1/.venv" ] || uv venv -q -p 3.12 "$LORA_ROOT/$1/.venv"
  echo "$LORA_ROOT/$1/.venv/bin/python"
}

clone() { [ -d "$LORA_ROOT/$2" ] || git clone -q https://github.com/ostris/ai-toolkit "$LORA_ROOT/$2"; }

if [ "${1:-}" != "--link-only" ]; then
echo "== ai-toolkit + InsightFace (CPU-детектор лиц)"
# ai-toolkit — это репо ostris; версия пинается коммитом a949f21 (фиксируем в ADR-026).
clone ostris/ai-toolkit ai-toolkit
(cd "$LORA_ROOT/ai-toolkit" && git fetch -q --depth 1 origin a949f21 && git checkout -q a949f21)

P=$(venv lora)
# ai-toolkit ставит свои torch/diffusers; torch — явно cu128, иначе из QA-индекса
uv pip install -q -p "$P" $TI $T28
# InsightFace для кропов лиц: buffalo_l (детектор + landmark); на усмотрение Марка — CPU или
# GPU. На gpuhost CPU проще: иначе воркер держит второй CUDA-context, и аллокатор рвёт
# память, как было с AudioSR. Релиз фиксируем, чтобы не приехала версия с переименованием API
uv pip install -q -p "$P" "insightface==0.7.3" onnxruntime
# ai-toolkit как пакет: edittable, чтобы подтянуть его зависимости
uv pip install -q -p "$P" -e "$LORA_ROOT/ai-toolkit"

echo "== веса qwen_image_2.1_int8_convrot"
# ai-toolkit ждёт путь к весам на диске. У ComfyUI они в diffusion_models; симлинк в
# ai-toolkit/models/<guid> держим шум, чтобы обновление весов в ComfyUI было видно без правок
mkdir -p "$LORA_ROOT/ai-toolkit/models"
WEIGHT_SRC="$COMFY_MODELS/diffusion_models/qwen_image_2.1_int8_convrot.safetensors"
[ -f "$WEIGHT_SRC" ] || { echo "Нет весов $WEIGHT_SRC — поставьте в Comfy-models"; exit 1; }
ln -sfn "$WEIGHT_SRC" "$LORA_ROOT/ai-toolkit/models/qwen_image_2.1_int8_convrot.safetensors"

echo "== прогрев моделей InsightFace (CPU, загружается один раз)"
"$P" - <<'PY' || true
import os
os.environ.setdefault("INSIGHTFACE_HOME", os.path.expanduser("~/.insightface"))
from insightface.app import FaceAnalysis
fa = FaceAnalysis(name="buffalo_l", providers=["CPUExecutionProvider"])
fa.prepare(ctx_id=-1, det_size=(640, 640))
print("insightface готов")
PY

fi  # --link-only: только копия узла и воркеров, workers.json и симлинк — при выкатке новой версии

echo "== конфиг воркеров"
W="$DEPLOY/workers"
cat > "$LORA_ROOT/workers.json" <<JSON
{"families": {
  "lora": {
    "python": "$LORA_ROOT/lora/.venv/bin/python",
    "script": "$W/worker_lora.py",
    "cwd": "$LORA_ROOT/ai-toolkit",
    "loras_root": "$COMFY_MODELS/loras",
    "env": {"AI_TOOLKIT_DIR": "$LORA_ROOT/ai-toolkit"}
  }
}}
JSON

echo "== узел ComfyUI"
ln -sfn "$DEPLOY/ccs_audio_worker" "$COMFY_ROOT/custom_nodes/ccs_audio_worker"
echo "Готово. Дальше: рестарт comfyui-h3 при ПУСТОЙ очереди,"
echo "LocalMedia:LoraTrainEnabled=true в appsettings.Local.json бэкенда."