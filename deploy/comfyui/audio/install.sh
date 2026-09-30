#!/bin/bash
# Установка аудиостека local-media на машину с ComfyUI (GPU1). Идемпотентно: повторный запуск
# доставляет недостающее. Подробности и причины каждого шага — README.md рядом.
#
#   AUDIO_ROOT   — venv и код моделей           (по умолчанию ~/ai-data/audio-workers)
#   AUDIO_MODELS — веса вне HF-кэша             (по умолчанию ~/ai-data/audio-models)
#   COMFY_ROOT   — ComfyUI                      (по умолчанию ~/ComfyUI-h3)
#   HTTPS_PROXY  — если туннель режет загрузки, прокси с выходом мимо него (README, «Сеть»)
#   --link-only  — без окружений и весов: обновить узел и воркеры после выкатки новой версии
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
AUDIO_ROOT=${AUDIO_ROOT:-$HOME/ai-data/audio-workers}
AUDIO_MODELS=${AUDIO_MODELS:-$HOME/ai-data/audio-models}
COMFY_ROOT=${COMFY_ROOT:-$HOME/ComfyUI-h3}
export PATH=$HOME/.local/bin:$PATH
TI="--index-url https://download.pytorch.org/whl/cu128 --extra-index-url https://pypi.org/simple --index-strategy unsafe-best-match"
T28="torch==2.8.0+cu128 torchaudio==2.8.0+cu128"
mkdir -p "$AUDIO_ROOT" "$AUDIO_MODELS"
# Узел и воркеры — копией вне git-дерева: симлинк ComfyUI на рабочую копию сломался бы при первом
# checkout другой ветки (или удалении worktree). Повторный запуск install.sh обновляет копию
DEPLOY="$AUDIO_ROOT/ccs"
mkdir -p "$DEPLOY"
rsync -a --delete --exclude '__pycache__' "$HERE/" "$DEPLOY/"

venv() { # venv <каталог> <python> — создаёт .venv, если его нет
  mkdir -p "$AUDIO_ROOT/$1"
  [ -d "$AUDIO_ROOT/$1/.venv" ] || uv venv -q -p "$2" "$AUDIO_ROOT/$1/.venv"
  echo "$AUDIO_ROOT/$1/.venv/bin/python"
}
clone() { [ -d "$AUDIO_ROOT/$2" ] || git clone -q --depth 1 "https://github.com/$1" "$AUDIO_ROOT/$2"; }

if [ "${1:-}" != "--link-only" ]; then
echo "== окружения"
# стемы + мастеринг; audioread audio-separator не тянет сам
P=$(venv util 3.12); uv pip install -q -p "$P" $TI $T28 "audio-separator[gpu]" audioread matchering soundfile
# basic-pitch тянет старый tensorflow — ставим без зависимостей, модель ONNX
P=$(venv midi 3.11); uv pip install -q -p "$P" "numpy<2" onnxruntime librosa mir_eval pretty_midi resampy scipy soundfile
uv pip install -q -p "$P" --no-deps basic-pitch
# у deepfilterlib нет колеса под 3.12 (собирается только с cargo)
P=$(venv dfn 3.11); uv pip install -q -p "$P" $TI $T28 "numpy<2" deepfilternet soundfile
clone QwenLM/Qwen3-TTS qwen3-tts
P=$(venv qwen3tts-env 3.12); uv pip install -q -p "$P" $TI $T28 -e "$AUDIO_ROOT/qwen3-tts" faster-whisper
# perth (водяной знак) и webrtcvad импортируют pkg_resources — его нет в setuptools>=81
P=$(venv chatterbox 3.12); uv pip install -q -p "$P" $TI chatterbox-tts "setuptools<81"
clone Plachtaa/seed-vc seed-vc
# Seed-VC ищет веса в двух местах: свои — в ./checkpoints (cache_dir), BigVGAN и Whisper — в
# ./checkpoints/hf_cache (inference.py ставит HF_HUB_CACHE). Оба ведём в общий HF-кэш
HUB=${HF_HOME:-$HOME/.cache/huggingface}/hub
mkdir -p "$AUDIO_ROOT/seed-vc/checkpoints"
ln -sfn "$HUB" "$AUDIO_ROOT/seed-vc/checkpoints/hf_cache"
for r in models--Plachta--Seed-VC models--Plachta--ASTRAL-quantization models--funasr--campplus models--lj1995--VoiceConversionWebUI; do
  ln -sfn "$HUB/$r" "$AUDIO_ROOT/seed-vc/checkpoints/$r"
done
P=$(venv seedvc-env 3.12)
grep -vE '^(torch|torchvision|torchaudio|gradio|FreeSimpleGUI|sounddevice|--)' "$AUDIO_ROOT/seed-vc/requirements.txt" > /tmp/seedvc-req.txt
uv pip install -q -p "$P" $TI $T28 torchvision==0.23.0+cu128 -r /tmp/seedvc-req.txt "setuptools<81"
clone IAHispano/Applio applio
[ -d "$AUDIO_ROOT/applio/.venv" ] || uv venv -q -p 3.12 "$AUDIO_ROOT/applio/.venv"
grep -vE '^(torch|torchaudio|gradio|pypresence|sounddevice|edge-tts)=' "$AUDIO_ROOT/applio/requirements.txt" > /tmp/applio-req.txt
uv pip install -q -p "$AUDIO_ROOT/applio/.venv/bin/python" $TI $T28 -r /tmp/applio-req.txt
# extract_model читает assets/config.json, который иначе создаёт только интерфейс Applio:
# без него итоговая модель .pth молча не сохраняется
cp -n "$AUDIO_ROOT/applio/assets/config_template.json" "$AUDIO_ROOT/applio/assets/config.json"
P=$(venv audiosr 3.11); uv pip install -q -p "$P" $TI $T28 audiosr "setuptools<81" "matplotlib<3.8" "numpy==1.23.5"
clone ace-step/ACE-Step-1.5 acestep15
(cd "$AUDIO_ROOT/acestep15" && uv sync -q)
P=$(venv bench 3.12); uv pip install -q -p "$P" $TI $T28 nvidia-cudnn-cu12 faster-whisper num2words soundfile numpy pyloudnorm

echo "== веса (только вне шлагбаума: воркеры запускаются с HF_HUB_OFFLINE=1)"
export HF_HUB_DISABLE_XET=1   # xet-клиент не видит HTTPS_PROXY
"$AUDIO_ROOT/applio/.venv/bin/python" "$AUDIO_ROOT/applio/core.py" prerequisites --pretraineds-hifigan --models --no-exe
"$AUDIO_ROOT/seedvc-env/.venv/bin/python" "$DEPLOY/warm_models.py" seedvc
"$AUDIO_ROOT/audiosr/.venv/bin/python" "$DEPLOY/warm_models.py" audiosr
"$AUDIO_ROOT/bench/.venv/bin/python" "$DEPLOY/warm_models.py" whisper
"$AUDIO_ROOT/qwen3tts-env/.venv/bin/python" "$DEPLOY/warm_models.py" qwen3tts "$AUDIO_MODELS"
"$AUDIO_ROOT/chatterbox/.venv/bin/python" "$DEPLOY/warm_models.py" chatterbox "$AUDIO_MODELS"
"$AUDIO_ROOT/util/.venv/bin/python" "$DEPLOY/warm_models.py" separator "$AUDIO_MODELS"
"$AUDIO_ROOT/acestep15/.venv/bin/python" "$DEPLOY/warm_models.py" acestep "$AUDIO_MODELS"

fi  # --link-only: только копия узла и воркеров, workers.json и симлинк — при выкатке новой версии

echo "== конфиг воркеров"
W="$DEPLOY/workers"
cat > "$AUDIO_ROOT/workers.json" <<JSON
{"families": {
  "tts": {"python": "$AUDIO_ROOT/qwen3tts-env/.venv/bin/python", "script": "$W/worker_tts.py"},
  "chatterbox": {"python": "$AUDIO_ROOT/chatterbox/.venv/bin/python", "script": "$W/worker_chatterbox.py"},
  "sep": {"python": "$AUDIO_ROOT/util/.venv/bin/python", "script": "$W/worker_sep.py"},
  "midi": {"python": "$AUDIO_ROOT/midi/.venv/bin/python", "script": "$W/worker_midi.py"},
  "dfn": {"python": "$AUDIO_ROOT/dfn/.venv/bin/python", "script": "$W/worker_dfn.py"},
  "seedvc": {"python": "$AUDIO_ROOT/seedvc-env/.venv/bin/python", "script": "$W/worker_seedvc.py", "cwd": "$AUDIO_ROOT/seed-vc"},
  "rvc": {"python": "$AUDIO_ROOT/applio/.venv/bin/python", "script": "$W/worker_rvc.py", "cwd": "$AUDIO_ROOT/applio"},
  "audiosr": {"python": "$AUDIO_ROOT/audiosr/.venv/bin/python", "script": "$W/worker_audiosr.py"},
  "asr": {"python": "$AUDIO_ROOT/bench/.venv/bin/python", "script": "$W/worker_asr.py"},
  "acestep": {"python": "$AUDIO_ROOT/acestep15/.venv/bin/python", "script": "$W/worker_acestep.py", "cwd": "$AUDIO_ROOT/acestep15",
              "env": {"ACESTEP_CHECKPOINTS_DIR": "$AUDIO_MODELS/acestep-checkpoints"}}
}}
JSON

echo "== узел ComfyUI"
ln -sfn "$DEPLOY/ccs_audio_worker" "$COMFY_ROOT/custom_nodes/ccs_audio_worker"
echo "Готово. Дальше: веса музыки в ComfyUI (README, «Музыка»), рестарт comfyui-h3 при ПУСТОЙ очереди,"
echo "LocalMedia:AudioEnabled=true в appsettings.Local.json бэкенда."
