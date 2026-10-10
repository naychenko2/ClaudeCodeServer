#!/bin/bash
# Установка воркера обучения LoRA персонажа (ADR-026, op lora_train / lora_delete) на машину с ComfyUI.
# Идемпотентно: повторный запуск доставляет недостающее. Подробности — README.md рядом.
#
#   AUDIO_ROOT  — корень воркеров узла CcsAudioWorker   (по умолчанию ~/ai-data/audio-workers)
#   COMFY_ROOT  — ComfyUI                               (по умолчанию ~/ComfyUI-h3)
#   COMFY_LORAS — каталог loras ComfyUI для весов      (по умолчанию ~/ai-data/comfy-models/loras)
#   LORA_V1     — ручной прогон v1, откуда берутся уже скачанные extras (по умолчанию ~/lora)
#   HTTPS_PROXY — для загрузки extras мимо туннеля (README аудио, «Сеть»)
#   --link-only — без окружения и весов: обновить узел, воркер и workers.json после выкатки
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
AUDIO_ROOT=${AUDIO_ROOT:-$HOME/ai-data/audio-workers}
COMFY_ROOT=${COMFY_ROOT:-$HOME/ComfyUI-h3}
LORA_V1=${LORA_V1:-$HOME/lora}
export PATH=$HOME/.local/bin:$PATH
ROOT="$AUDIO_ROOT/lora"            # venv и extras
TOOLKIT="$AUDIO_ROOT/ai-toolkit"   # ai-toolkit на зафиксированном коммите
DEPLOY="$AUDIO_ROOT/ccs-lora"      # копия воркера: свой каталог, audio/install.sh чистит ccs/ через --delete
AI_TOOLKIT_COMMIT=a949f21f8e021320b1af6792e3dfbf6661a8caa9
# как у ручного v1 (~/lora/install.sh): на этой связке замерены 2,3 с/шаг
TI="--index-url https://download.pytorch.org/whl/cu130 --extra-index-url https://pypi.org/simple --index-strategy unsafe-best-match"
TORCH="torch==2.13.0 torchvision==0.28.0 torchaudio==2.11.0"

echo "== узел CcsAudioWorker и общий код воркеров (штатная выкатка аудио)"
AUDIO_ROOT="$AUDIO_ROOT" COMFY_ROOT="$COMFY_ROOT" bash "$HERE/../audio/install.sh" --link-only
mkdir -p "$DEPLOY" "$ROOT"
rsync -a --delete --exclude '__pycache__' "$HERE/" "$DEPLOY/"

EXTRAS="$ROOT/models/Qwen-Image-2.1"
WEIGHTS=""
for d in "$HOME/ai-data/comfy-models/diffusion_models" "$HOME/ComfyUI/models/diffusion_models"; do
  [ -f "$d/qwen_image_2.1_int8_convrot.safetensors" ] && { WEIGHTS="$d/qwen_image_2.1_int8_convrot.safetensors"; break; }
done
[ -n "$WEIGHTS" ] || { echo "Нет весов qwen_image_2.1_int8_convrot.safetensors в diffusion_models ComfyUI"; exit 1; }

if [ "${1:-}" != "--link-only" ]; then
echo "== ai-toolkit $AI_TOOLKIT_COMMIT"
[ -d "$TOOLKIT/.git" ] || git clone -q https://github.com/ostris/ai-toolkit "$TOOLKIT"
git -C "$TOOLKIT" cat-file -e "$AI_TOOLKIT_COMMIT^{commit}" 2>/dev/null || git -C "$TOOLKIT" fetch -q origin
git -C "$TOOLKIT" checkout -q "$AI_TOOLKIT_COMMIT"

echo "== окружение (torch как у v1, зависимости ai-toolkit, InsightFace на CPU)"
[ -d "$ROOT/.venv" ] || uv venv -q -p 3.12 "$ROOT/.venv"
P="$ROOT/.venv/bin/python"
uv pip install -q -p "$P" $TI $TORCH
# ai-toolkit — скрипты (run.py), а не пакет: ставим только его requirements
uv pip install -q -p "$P" $TI $TORCH -r "$TOOLKIT/requirements.txt"
# детектор лиц на CPU: второй CUDA-контекст в процессе обучения отъедал бы видеопамять
uv pip install -q -p "$P" $TI $TORCH "insightface==0.7.3" onnxruntime

echo "== extras Qwen-Image-2.1 (энкодер, VAE, конфиги; transformer — наши int8-веса ComfyUI)"
if [ ! -f "$EXTRAS/text_encoder/model.safetensors.index.json" ]; then
  mkdir -p "$(dirname "$EXTRAS")"
  if [ -f "$LORA_V1/models/Qwen-Image-2.1/text_encoder/model.safetensors.index.json" ]; then
    # жёсткие ссылки: без второй копии 16 ГБ; на другой файловой системе — обычная копия
    cp -al "$LORA_V1/models/Qwen-Image-2.1" "$EXTRAS" 2>/dev/null \
      || { rm -rf "$EXTRAS"; cp -a "$LORA_V1/models/Qwen-Image-2.1" "$EXTRAS"; }
  else
    HF_HUB_DISABLE_XET=1 "$P" -c "from huggingface_hub import snapshot_download as s; s('Qwen/Qwen-Image-2.1', local_dir='$EXTRAS', ignore_patterns=['transformer/*'])"
  fi
fi

echo "== прогрев InsightFace buffalo_l"
"$P" -c "from insightface.app import FaceAnalysis as F; F(name='buffalo_l', providers=['CPUExecutionProvider']).prepare(ctx_id=-1)" >/dev/null
fi  # --link-only

echo "== семейство lora в workers.json"
# каталог loras секции h3data в extra_model_paths.yaml; узел при каждом запуске сверяет его со списком ComfyUI
LORAS=${COMFY_LORAS:-$HOME/ai-data/comfy-models/loras}
mkdir -p "$LORAS/ccs"
cat > "$AUDIO_ROOT/workers.lora.json" <<JSON
{"families": {
  "lora": {"python": "$ROOT/.venv/bin/python", "script": "$DEPLOY/worker_lora.py", "cwd": "$TOOLKIT",
           "loras_root": "$LORAS",
           "env": {"AI_TOOLKIT_DIR": "$TOOLKIT", "CCS_LORA_BASE_WEIGHTS": "$WEIGHTS",
                   "CCS_LORA_EXTRAS": "$EXTRAS", "CCS_WORKERS_DIR": "$AUDIO_ROOT/ccs/workers"}}
}}
JSON
python3 "$AUDIO_ROOT/ccs/merge_workers.py" "$AUDIO_ROOT/workers.json" "$AUDIO_ROOT/workers.lora.json"
rm -f "$AUDIO_ROOT/workers.lora.json"
echo "Готово. Дальше: рестарт comfyui-h3 при ПУСТОЙ очереди, LocalMedia:LoraTrainEnabled=true в appsettings.Local.json."
