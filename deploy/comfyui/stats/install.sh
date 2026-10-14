#!/bin/bash
# Установка маршрута GET /ccs/stats (пакет ccs_stats) на машину с ComfyUI. Идемпотентно.
# ComfyUI сам НЕ перезапускается: рестарт роняет общую очередь дева и боя, его делает
# человек в тихое окно. Подробности — README.md рядом.
#
#   STATS_ROOT — куда копируется пакет               (по умолчанию ~/ai-data/ccs-stats)
#   COMFY_ROOT — ComfyUI                              (по умолчанию ~/ComfyUI-h3)
#   COMFY_PY   — python из venv ComfyUI для проверки  (по умолчанию $COMFY_ROOT/.venv/bin/python
#                                                       или $COMFY_ROOT/venv/bin/python)
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
STATS_ROOT=${STATS_ROOT:-$HOME/ai-data/ccs-stats}
COMFY_ROOT=${COMFY_ROOT:-$HOME/ComfyUI-h3}
NODE=ccs_stats

[ -d "$COMFY_ROOT/custom_nodes" ] || { echo "ОШИБКА: нет $COMFY_ROOT/custom_nodes" >&2; exit 1; }

# Копия вне git-дерева, как у face и audio: симлинк на рабочую копию сломался бы при
# checkout другой ветки или удалении worktree. Тесты на узел не везём
echo "== пакет"
mkdir -p "$STATS_ROOT/$NODE"
rsync -a --delete --exclude '__pycache__' --exclude 'tests' "$HERE/$NODE/" "$STATS_ROOT/$NODE/"
ln -sfn "$STATS_ROOT/$NODE" "$COMFY_ROOT/custom_nodes/$NODE"
echo "   $COMFY_ROOT/custom_nodes/$NODE -> $STATS_ROOT/$NODE"

echo "== зависимость pynvml (пакет nvidia-ml-py)"
# Только проверка: venv ComfyUI скрипт не трогает. Без pynvml маршрут работает, но отдаёт
# gpus: null и gpusError — карта медиа в панели будет «не сообщает»
if [ -z "${COMFY_PY:-}" ]; then
  for p in "$COMFY_ROOT/.venv/bin/python" "$COMFY_ROOT/venv/bin/python"; do
    [ -x "$p" ] && { COMFY_PY=$p; break; }
  done
fi
if [ -z "${COMFY_PY:-}" ] || [ ! -x "$COMFY_PY" ]; then
  echo "   ВНИМАНИЕ: python ComfyUI не найден, проверка пропущена (задай COMFY_PY)"
elif "$COMFY_PY" -c "import pynvml; pynvml.nvmlInit(); print('   pynvml: карт', pynvml.nvmlDeviceGetCount())" 2>/dev/null; then
  :
else
  echo "   ВНИМАНИЕ: pynvml не импортируется или NVML недоступен."
  echo "   Поставить (чистый Python, без зависимостей): uv pip install -p \"$COMFY_PY\" nvidia-ml-py"
fi

echo "Готово. ComfyUI НЕ перезапущен. Дальше вручную, в тихое окно:"
echo "  1. очередь пуста:   curl -s :8188/queue   (queue_running и queue_pending — пустые)"
echo "  2. рестарт:         systemctl restart comfyui-h3   (с --user, если юнит пользовательский)"
echo "  3. живость:         curl -s :8188/system_stats"
echo "  4. маршрут:         curl -s :8188/ccs/stats"
