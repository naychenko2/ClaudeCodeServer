#!/bin/bash
# Установка узлов лиц (все каталоги ccs_face_* рядом: CcsFaceSimilarity, CcsFacePose...) на
# машину с ComfyUI (GPU1). Идемпотентно: повторный запуск доставляет недостающее, а
# workers.json сливается, а не перезаписывается. Подробности и причины каждого шага —
# README.md рядом.
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
mkdir -p "$FACE_ROOT" "$FACE_MODELS"

# Узел и воркер — копией вне git-дерева: симлинк ComfyUI на рабочую копию сломался бы при
# первом checkout другой ветки или удалении worktree. Структура повторяет audio: внутри
# $DEPLOY лежат папка узла (для custom_nodes) и warm_models.py (для скачивания весов)
DEPLOY="$FACE_ROOT/ccs"
NODES=()
for dir in "$HERE"/ccs_face_*/; do
  [ -d "$dir" ] || continue
  NODES+=("$(basename "$dir")")
done
[ ${#NODES[@]} -gt 0 ] || { echo "ОШИБКА: рядом нет ни одного каталога ccs_face_*" >&2; exit 1; }
for node in "${NODES[@]}"; do
  mkdir -p "$DEPLOY/$node"
  rsync -a --delete --exclude '__pycache__' "$HERE/$node/" "$DEPLOY/$node/"
done
cp -f "$HERE/warm_models.py" "$DEPLOY/warm_models.py"

if [ "${1:-}" != "--link-only" ]; then
  echo "== окружения"
  # CPU-onnxruntime: buffalo_l на RTX 3090 через torch-onnxruntime запрещён — он конфликтует
  # с cu128, который в venv ComfyUI. Пусть ArcFace считается на CPU (≈0,5 с на эталон), чем
  # мы сломаем ComfyUI. Здесь НЕТ torch/onnxruntime-gpu — face-workers живёт в своём venv
  # только с CPU-исполнителем
  P="$FACE_ROOT/.venv"
  [ -d "$P" ] || uv venv -q -p 3.12 "$P"
  uv pip install -q -p "$P/bin/python" "numpy<2" "opencv-python<4.12" insightface onnxruntime

  echo "== веса (только вне шлагбаума: узел работает офлайн)"
  export HF_HUB_DISABLE_XET=1   # xet-клиент не видит HTTPS_PROXY
  export TQDM_DISABLE=1         # глушит прогресс insightface при скачивании buffalo_l
  "$P/bin/python" "$DEPLOY/warm_models.py" "$FACE_MODELS"
  unset TQDM_DISABLE

  # buffalo_l.zip (~289 МБ) insightface распаковывает рядом с моделью и забывает удалить.
  # На диске остаётся только распакованная модель (~280 МБ). На повторной установке zip
  # уже нет — find молча выходит с нулём, ошибки не будет.
  find "$FACE_MODELS" -type f -name 'buffalo_l.zip' -delete
fi  # --link-only: только копия узла, при выкатке новой версии

echo "== конфиг воркера"
# Слияние, а не перезапись: трогаем только семейства своих узлов (ccs_face_<семейство> с
# worker_<семейство>.py), остальные ключи файла остаются как были. Узел без воркера
# пропускаем. Битый JSON — отказ, а не молчаливая перезапись. Запись через временный файл
python3 - "$FACE_ROOT/workers.json" "$FACE_ROOT/.venv/bin/python" "$DEPLOY" "${NODES[@]}" <<'PY'
import json, os, sys
path, python, deploy, nodes = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
cfg = {}
if os.path.exists(path):
    with open(path, encoding="utf-8") as f:
        cfg = json.load(f)
families = cfg.setdefault("families", {})
for node in nodes:
    fam = node[len("ccs_face_"):]
    script = os.path.join(deploy, node, f"worker_{fam}.py")
    if not os.path.isfile(script):
        print(f"   {node}: нет worker_{fam}.py — семейство пропущено")
        continue
    families[fam] = {**families.get(fam, {}), "python": python, "script": script}
    print(f"   {fam}: {script}")
tmp = path + ".tmp"
with open(tmp, "w", encoding="utf-8") as f:
    json.dump(cfg, f, ensure_ascii=False, indent=2)
os.replace(tmp, path)
PY

echo "== узлы ComfyUI"
for node in "${NODES[@]}"; do
  ln -sfn "$DEPLOY/$node" "$COMFY_ROOT/custom_nodes/$node"
  echo "   $node"
done
echo "Готово. Дальше: рестарт comfyui-h3 при ПУСТОЙ очереди,"
echo "узлы в /object_info/CcsFaceSimilarity, /object_info/CcsFacePose..."
