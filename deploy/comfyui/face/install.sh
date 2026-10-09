#!/bin/bash
# Установка узла сходства лиц (CcsFaceSimilarity) на машину с ComfyUI (GPU1). Идемпотентно:
# повторный запуск доставляет недостающее. Подробности и причины каждого шага — README.md
# рядом.
#
#   FACE_ROOT    — venv и код                        (по умолчанию ~/ai-data/face-workers)
#   FACE_MODELS  — веса buffalo_l вне HF-кэша         (по умолчанию ~/ai-data/face-models)
#   COMFY_ROOT   — ComfyUI                            (по умолчанию ~/ComfyUI-h3)
#   HTTPS_PROXY  — если туннель режет загрузки, прокси с выходом мимо него (audio/README, «Сеть»)
#   --link-only  — без окружений и весов: обновить узел после выкатки новой версии
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
FACE_ROOT=${FACE_ROOT:-$HOME/ai-data/face-workers}
FACE_MODELS=${FACE_MODELS:-$HOME/ai-data/face-models}
COMFY_ROOT=${COMFY_ROOT:-$HOME/ComfyUI-h3}
export PATH=$HOME/.local/bin:$PATH
TI="--index-url https://download.pytorch.org/whl/cu128 --extra-index-url https://pypi.org/simple --index-strategy unsafe-best-match"
mkdir -p "$FACE_ROOT" "$FACE_MODELS"

# Узел и warm_models.py — копией вне git-дерева: симлинк ComfyUI на рабочую копию сломался
# бы при первом checkout другой ветки или удалении worktree. Повторный install.sh обновляет
# копию
DEPLOY="$FACE_ROOT/ccs"
mkdir -p "$DEPLOY"
rsync -a --delete --exclude '__pycache__' "$HERE/" "$DEPLOY/"

if [ "${1:-}" != "--link-only" ]; then
  echo "== окружения"
  # CPU-onnxruntime: buffalo_l на RTX 3090 через torch-onnxruntime запрещён — он конфликтует
  # с cu128, который в venv ComfyUI. Пусть ArcFace считается на CPU (≈0,5 с на эталон), чем
  # мы сломаем ComfyUI
  P="$FACE_ROOT/.venv"
  [ -d "$P" ] || uv venv -q -p 3.12 "$P"
  uv pip install -q -p "$P/bin/python" $TI "numpy<2" "opencv-python<4.12" insightface onnxruntime

  echo "== веса (только вне шлагбаума: узел работает офлайн)"
  export HF_HUB_DISABLE_XET=1   # xet-клиент не видит HTTPS_PROXY
  "$P/bin/python" "$DEPLOY/warm_models.py" "$FACE_MODELS"
fi  # --link-only: только копия узла, при выкатке новой версии

echo "== узел ComfyUI"
ln -sfn "$DEPLOY/ccs_face_similarity" "$COMFY_ROOT/custom_nodes/ccs_face_similarity"
echo "Готово. Дальше: рестарт comfyui-h3 при ПУСТОЙ очереди,"
echo "узел в /object_info/CcsFaceSimilarity."