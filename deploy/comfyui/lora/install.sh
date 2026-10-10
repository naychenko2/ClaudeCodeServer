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
# Свои файлы (install.sh, worker_lora.py, README, шаблон) кладём в $DEPLOY/lora — чтобы не
# мешать audio install.sh, который rsync-ит в $DEPLOY напрямую. Узел ccs_audio_worker общий
# и обновляется из исходника $HERE/../audio/ccs_audio_worker/__init__.py (рядом в репо)
DEPLOY="$LORA_ROOT/ccs"
mkdir -p "$DEPLOY/lora" "$DEPLOY/workers" "$DEPLOY/ccs_audio_worker"
rsync -a --exclude '__pycache__' "$HERE/" "$DEPLOY/lora/"
cp "$HERE/../audio/ccs_audio_worker/__init__.py" "$DEPLOY/ccs_audio_worker/__init__.py"

venv() { # venv <каталог> — создаёт .venv, если его нет
  mkdir -p "$LORA_ROOT/$1"
  [ -d "$LORA_ROOT/$1/.venv" ] || uv venv -q -p 3.12 "$LORA_ROOT/$1/.venv"
  echo "$LORA_ROOT/$1/.venv/bin/python"
}

clone() { [ -d "$LORA_ROOT/$2" ] || git clone -q --no-single-branch https://github.com/ostris/ai-toolkit "$LORA_ROOT/$2"; }

if [ "${1:-}" != "--link-only" ]; then
echo "== ai-toolkit + InsightFace (CPU-детектор лиц)"
# ai-toolkit — это репо ostris; версия пинается коммитом a949f21 (фиксируем в ADR-026).
# --no-single-branch: shallow-клон с одним коммитом не содержит a949f21 (он предок HEAD)
clone ostris/ai-toolkit ai-toolkit
(cd "$LORA_ROOT/ai-toolkit" && git checkout -q a949f21)

P=$(venv lora)
# ai-toolkit ставит свои torch/diffusers; torch — явно cu128, иначе из QA-индекса
uv pip install -q -p "$P" $TI $T28
# InsightFace для кропов лиц: buffalo_l (детектор + landmark); на усмотрение Марка — CPU или
# GPU. На gpuhost CPU проще: иначе воркер держит второй CUDA-context, и аллокатор рвёт
# память, как было с AudioSR. Релиз фиксируем, чтобы не приехала версия с переименованием API
uv pip install -q -p "$P" "insightface==0.7.3" onnxruntime
# ai-toolkit — набор скриптов без pyproject.toml; ставим его зависимости из requirements.txt
# (репо — коллекция run.py + toolkit/, а не pip-пакет). requirements.txt ссылается на
# requirements_base.txt — содержит transformers/accelerate/peft/diffusers и пр.
uv pip install -q -p "$P" -r "$LORA_ROOT/ai-toolkit/requirements.txt"

echo "== веса qwen_image_2.1_int8_convrot"
# ai-toolkit ждёт путь к весам на диске. У ComfyUI они в diffusion_models; симлинк в
# ai-toolkit/models/<guid> держим шум, чтобы обновление весов в ComfyUI было видно без правок.
# Путь ищем по всем каталогам, которые знает ComfyUI (extra_model_paths.yaml): h3data
# ($COMFY_MODELS) и photo ($COMFY_MODELS/../ComfyUI/models). Без этого ai-toolkit не найдёт
mkdir -p "$LORA_ROOT/ai-toolkit/models"
WEIGHT_SRC=""
for d in "$COMFY_MODELS/diffusion_models" "${COMFY_MODELS%/comfy-models}/ComfyUI/models/diffusion_models"; do
  if [ -f "$d/qwen_image_2.1_int8_convrot.safetensors" ]; then
    WEIGHT_SRC="$d/qwen_image_2.1_int8_convrot.safetensors"
    break
  fi
done
[ -n "$WEIGHT_SRC" ] || { echo "Нет весов qwen_image_2.1_int8_convrot — поставьте в ComfyUI/models/diffusion_models"; exit 1; }
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
W="$DEPLOY/lora"
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