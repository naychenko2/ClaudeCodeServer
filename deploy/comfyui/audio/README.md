# Аудиостек local-media на машине с ComfyUI

Решение и его причины — [ADR-020](../../../docs/adr/ADR-020-local-media-audio.md), инструменты
и время — [local-media.md](../../../docs/features/local-media.md), раздел «Аудио».

## Что где живёт

| Что | Где |
|---|---|
| Музыка по тексту (ACE-Step 1.5 XL, YuE2, MiniMax Music 3) | нативные ноды ComfyUI 0.37, веса в `~/ai-data/comfy-models` |
| Узел очереди для остальных моделей | `ccs_audio_worker/` → копия в `~/ai-data/audio-workers/ccs` → симлинк в `ComfyUI-h3/custom_nodes/` |
| Воркеры (один процесс на задачу) | `workers/worker_*.py`, общий код — `workers/ccs_common.py` |
| venv и код моделей | `~/ai-data/audio-workers/<семейство>` |
| Веса вне HF-кэша | `~/ai-data/audio-models` (Qwen3-TTS, Chatterbox, стемы, ACE-Step) |
| Машинный конфиг семейств | `~/ai-data/audio-workers/workers.json` (пишет `install.sh`) |

Узел `CcsAudioWorker` ставится в общую очередь ComfyUI, как шлагбаум транскрибации:
выгружает модели ComfyUI, запускает воркер в его venv и ждёт. Процесс вышел — видеопамять
свободна. Так у GPU1 одна очередь на картинки, видео, транскрибацию и аудио.

## Установка

```bash
./install.sh                     # окружения, веса, workers.json, симлинк узла
./install.sh --link-only         # после выкатки новой версии: только узел, воркеры и workers.json
```

Узел и воркеры копируются в `~/ai-data/audio-workers/ccs`, и ComfyUI ссылается на копию, а не на
рабочее дерево git: иначе `checkout` другой ветки или удаление worktree ломает аудио. Правка узла
вступает в силу после рестарта `comfyui-h3` (при пустой очереди), правка воркеров — сразу.

Потом вручную:

1. Веса музыки для ComfyUI (Comfy-Org, repackaged) в `~/ai-data/comfy-models`:
   `diffusion_models/acestep_v1.5_xl_sft_bf16`, `text_encoders/qwen_{0.6b,4b}_ace15`,
   `vae/ace_1.5_vae`, `checkpoints/yue2_3b_int8_convrot`, `audio_encoders/sheetsage2_bf16`,
   `diffusion_models/minimax_music3_dit_fp16`, `text_encoders/minimax_music3_text_encoder_pruned_int8_convrot`,
   `vae/minimax_music3_dav`. Каталоги `checkpoints` и `audio_encoders` добавить в
   `extra_model_paths.yaml` (секция `h3data`).
2. Рестарт `comfyui-h3` — **только при пустой очереди** (`curl :8188/queue`).
3. В `appsettings.Local.json` бэкенда — `"LocalMedia": { "AudioEnabled": true }`.

## Грабли

- **Скачивание весов — только при установке.** Узел запускает воркеры с `HF_HUB_OFFLINE=1`:
  докачка внутри задачи держала бы всю очередь GPU (на замере — 10 минут на одном BigVGAN).
- **`HF_HUB_DISABLE_XET=1` при загрузке через прокси**: xet-клиент HF не видит `HTTPS_PROXY`.
- **Applio**: `assets/config.json` создаёт только его интерфейс; без файла `extract_model`
  падает, а ошибка теряется в `os._exit` — модель `.pth` молча не сохраняется.
- **Seed-VC и Chatterbox** импортируют `pkg_resources` — нужен `setuptools<81`.
- **audio-separator**: лишние `demucs*.th` (v1) в каталоге моделей ломают HTDemucs
  («Duplicate pre-trained model exist for signature demucs»).
- **Qwen3-TTS клон**: расшифровка образца обязана совпадать с записью слово в слово, иначе
  модель «договаривает» и выдаёт кашу (CER 77 % против 0,7 %). Без `reference_text` воркер
  распознаёт образец сам и режет его по границе слова до 15 с.
- **Замеры и ручные прогоны** запускать отдельным юнитом `systemd-run --user`, а не из
  шелла агента: `ccs-agents.slice` ограничен по памяти, обучение RVC (≈7 ГБ RSS) плюс сборка
  дали OOM-убийство сессии.

## Сеть

Трафик пользователя по `ip rule` идёт в туннель (таблица 2022): 3 МБ/с на поток и почти ноль
на параллели. Правило `from 192.168.7.83 lookup main` пускает соединения с адресом `br0`
напрямую (≈9 МБ/с, потолок линии). Для больших загрузок: `curl --interface br0` или локальный
CONNECT-прокси с этим исходным адресом (`HTTPS_PROXY=http://127.0.0.1:18888`).
