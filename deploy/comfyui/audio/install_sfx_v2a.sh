#!/bin/bash
# Эффекты по тексту и звук по видео (Ш4): ноды Foley и MMAudio в прод-ComfyUI и семейство sfx_moss.
#
#   install_sfx_v2a.sh nodes      — ноды в ~/ComfyUI-h3 ТОЛЬКО если pip не меняет ни одного существующего
#                                   пакета (снимок pip freeze до/после, бэкап, rollback-sfx-v2a.sh). Иначе
#                                   отказ: тогда звук по видео — семейством воркера со своим venv
#   install_sfx_v2a.sh moss       — venv MOSS-SFX (копия проверенного venv стенда либо сборка по пинам)
#   install_sfx_v2a.sh link       — симлинки весов в models/ ComfyUI и семейство в workers.json
#
#   EXTRA_MODELS — веса вне HF-кэша (по умолчанию ~/ai-data/audio-extra; /mnt/wsl/models заполнен)
#   NODE_SRC     — каталог с исходниками нод Foley и MMAudio (по умолчанию ~/media-bench/src)
#   COMFY_ROOT   — ComfyUI (по умолчанию ~/ComfyUI-h3)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
AUDIO_ROOT=${AUDIO_ROOT:-$HOME/ai-data/audio-workers}
EXTRA_MODELS=${EXTRA_MODELS:-$HOME/ai-data/audio-extra}
COMFY_ROOT=${COMFY_ROOT:-$HOME/ComfyUI-h3}
NODE_SRC=${NODE_SRC:-$HOME/media-bench/src}
NODES="ComfyUI-HunyuanVideo-Foley ComfyUI-MMAudio"
PY="$COMFY_ROOT/.venv/bin/python"
STAMP=$(date +%Y%m%d-%H%M%S)
SNAP="$COMFY_ROOT/.sfx-v2a-snapshots/$STAMP"

cmd_nodes() {
  mkdir -p "$SNAP"
  "$PY" -m pip freeze 2>/dev/null | sort > "$SNAP/freeze-before.txt"
  ls "$COMFY_ROOT/custom_nodes" > "$SNAP/custom_nodes-before.txt"
  # у requirements.txt нод нет завершающего перевода строки — склеиваем с явным
  for n in $NODES; do cat "$NODE_SRC/$n/requirements.txt"; echo; done | grep -vE '^\s*(#|$)' > "$SNAP/req.txt" || true
  # констрейнты = всё установленное: пробный прогон покажет только НОВЫЕ пакеты либо конфликт
  grep -E '^[A-Za-z0-9_.-]+==' "$SNAP/freeze-before.txt" > "$SNAP/constraints.txt"
  if ! "$PY" -m pip install --dry-run -r "$SNAP/req.txt" -c "$SNAP/constraints.txt" > "$SNAP/dry-run.log" 2>&1; then
    echo "ОТКАЗ: зависимости нод не встают без смены существующих пакетов (см. $SNAP/dry-run.log)." >&2
    echo "Ставить в прод нельзя — оформить как семейство аудио-воркера со своим venv." >&2
    exit 3
  fi
  grep -E '^Would install' "$SNAP/dry-run.log" || echo "Новых пакетов нет"
  "$PY" -m pip install -r "$SNAP/req.txt" -c "$SNAP/constraints.txt" > "$SNAP/install.log" 2>&1
  "$PY" -m pip freeze 2>/dev/null | sort > "$SNAP/freeze-after.txt"
  # существующие пакеты: ни одной смены версии и ни одного удаления
  if comm -23 "$SNAP/freeze-before.txt" "$SNAP/freeze-after.txt" | grep -q .; then
    echo "ТРЕВОГА: pip изменил или удалил существующие пакеты — откат:" >&2
    comm -23 "$SNAP/freeze-before.txt" "$SNAP/freeze-after.txt" >&2
    "$PY" -m pip install -r "$SNAP/freeze-before.txt" >> "$SNAP/install.log" 2>&1 || true
    exit 4
  fi
  for n in $NODES; do
    [ -d "$COMFY_ROOT/custom_nodes/$n" ] || rsync -a --exclude .git "$NODE_SRC/$n/" "$COMFY_ROOT/custom_nodes/$n/"
  done
  cat > "$COMFY_ROOT/rollback-sfx-v2a.sh" <<ROLL
#!/bin/bash
# Откат установки нод Foley/MMAudio ($STAMP): убрать ноды и удалить пакеты, которых не было до установки.
# Нужен рестарт comfyui-h3 при пустой очереди.
set -e
for n in $NODES; do rm -rf "$COMFY_ROOT/custom_nodes/\$n"; done
comm -13 "$SNAP/freeze-before.txt" "$SNAP/freeze-after.txt" | sed 's/==.*//' | xargs -r "$PY" -m pip uninstall -y
echo "Откат выполнен; перезапусти comfyui-h3"
ROLL
  chmod +x "$COMFY_ROOT/rollback-sfx-v2a.sh"
  echo "Ноды и пакеты установлены. Снимки: $SNAP; откат: $COMFY_ROOT/rollback-sfx-v2a.sh"
}

cmd_moss() {
  local R="$AUDIO_ROOT/moss-sfx"
  mkdir -p "$R"
  [ -d "$R/MOSS-TTS" ] || git clone -q --depth 1 https://github.com/OpenMOSS/MOSS-TTS "$R/MOSS-TTS"
  if [ ! -d "$R/.venv" ]; then
    uv venv -q -p 3.12 "$R/.venv"
    # пины автора (transformers 4.57.1, diffusers 0.37.1, numpy 1.26.4, без sageattention)
    uv pip install -q -p "$R/.venv/bin/python" --index-url https://download.pytorch.org/whl/cu130 \
      --extra-index-url https://pypi.org/simple --index-strategy unsafe-best-match \
      -r "$HERE/moss-sfx-requirements.txt" -e "$R/MOSS-TTS/moss_soundeffect_v2"
  fi
  "$R/.venv/bin/python" -I -c "import moss_soundeffect_v2, torch; print('MOSS-SFX ok, torch', torch.__version__)"
}

cmd_link() {
  mkdir -p "$COMFY_ROOT/models"
  [ -e "$COMFY_ROOT/models/foley" ] || ln -s "$EXTRA_MODELS/foley" "$COMFY_ROOT/models/foley"
  [ -e "$COMFY_ROOT/models/mmaudio" ] || ln -s "$EXTRA_MODELS/mmaudio" "$COMFY_ROOT/models/mmaudio"
  # TORCHDYNAMO_DISABLE: одноразовый процесс платил ~70 с компиляции inductor на каждый запуск (замер 13.11:
  # 110 с → 44 с на клип 5 с), кэш inductor/triton не помогал
  local part
  part=$(mktemp)
  cat > "$part" <<JSON
{"families": {"sfx_moss": {"python": "$AUDIO_ROOT/moss-sfx/.venv/bin/python",
  "script": "$AUDIO_ROOT/ccs/workers/worker_sfx_moss.py", "cwd": "$AUDIO_ROOT/moss-sfx", "vram_mb": 18000,
  "env": {"CCS_MOSS_SFX_DIR": "$EXTRA_MODELS/moss-sfx-v2", "TORCHDYNAMO_DISABLE": "1"}}}}
JSON
  python3 "$HERE/merge_workers.py" "$AUDIO_ROOT/workers.json" "$part"
  rm -f "$part"
}

case "${1:-}" in
  nodes) cmd_nodes ;;
  moss) cmd_moss ;;
  link) cmd_link ;;
  *) sed -n 2,13p "$0"; exit 1 ;;
esac
