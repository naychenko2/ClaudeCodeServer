# Обучение LoRA персонажа на машине с ComfyUI

Решение и контракты — [ADR-026](../../../docs/adr/ADR-026-character-lora-training.md) и
[ADR-026-contracts](../../../docs/adr/ADR-026-contracts.md). Эта страница — оперативная:
что где живёт, как ставить, как проверять.

## Что где живёт

| Что | Где |
|---|---|
| Узел очереди | `ccs_audio_worker/` (тот же, что для аудио), правится в `deploy/comfyui/audio/...` |
| Семейство воркера | `lora` в `~/ai-data/audio-workers/workers.json` (пишет `install.sh`) |
| Скрипт воркера | `workers/worker_lora.py` (копия в `~/ai-data/audio-workers/ccs/workers/`) |
| venv и ai-toolkit | `~/ai-data/audio-workers/lora/.venv`, `~/ai-data/audio-workers/ai-toolkit/` |
| Шаблон yaml | `deploy/comfyui/lora/templates/standard.yaml` (копия приходит через `install.sh`) |
| Веса qwen_image_2.1 | `~/ai-data/comfy-models/diffusion_models/qwen_image_2.1_int8_convrot.safetensors` |

Один процесс воркера — одно обучение. После выхода видеопамять освобождается; следующая
картинка сама подгрузит свою модель. Так GPU1 общая: ComfyUI рисует, H3 распознаёт,
аудиостеки и теперь LoRA встают в одну очередь через единый узел `CcsAudioWorker`.

`Op` живёт в белом списке `OPS` узла `ccs_audio_worker` (`deploy/comfyui/audio/.../__init__.py`):
`lora_train` → семейство `lora`, `lora_delete` → семейство `lora`. Обе op уходят в
`_run_lora` узла и обрабатываются без общего `_run`, потому что у LoRA свои проверки
путей и сценарий без `vram_mb` (выгрузка полная, без бюджета — на замере пик VRAM 17,6 ГБ
из 24,6 без `/free` не влезает).

## Установка

```bash
./install.sh                  # ai-toolkit + InsightFace + симлинк узла + workers.json
./install.sh --link-only      # после выкатки новой версии: только узел, воркеры и workers.json
```

Сценарий идемпотентный: повторный запуск не переустанавливает venv и не перекачивает
веса, только обновляет копию воркера и симлинк узла.

Узел и воркеры копируются в `~/ai-data/audio-workers/ccs`, и ComfyUI ссылается на
копию, а не на рабочее дерево git: иначе `checkout` другой ветки или удаление worktree
ломает LoRA. Правка узла вступает в силу после рестарта `comfyui-h3` (при пустой
очереди), правка воркера — сразу.

Потом вручную:

1. Весы qwen_image_2.1_int8_convrot в `~/ai-data/comfy-models/diffusion_models/`.
2. Рестарт `comfyui-h3` — **только при пустой очереди** (`curl :8188/queue`).
3. В `appsettings.Local.json` бэкенда — `"LocalMedia": { "LoraTrainEnabled": true }`.

## Грабли

- **Перед стартом обучения воркер ОБЯЗАН полностью выгрузить модели ComfyUI.** На замере
  2026-10-10 пик VRAM 17,6 ГБ из 24,6 с `/free`, без него — 24,19/24,58 ГБ и задача
  падает с OOM. Узел зовёт `_free_vram(need_mb=None)` (тот же путь, что у `_run_lora`),
  воркер страхуется `torch.cuda.empty_cache()`.
- **Один процесс обучения — 2 часа.** Во время него ComfyUI не может ни картинку нарисовать,
  ни видео собрать: вся очередь встала. ADR-026 так и задумано (одно обучение на инстанс),
  но при стейджинге это нужно помнить.
- **InsightFace — только CPU.** На gpuhost второй CUDA-контекст рвёт память, как было с
  AudioSR. buffalo_l детектирует на CPU за 1–3 с на фото, что укладывается в таймаут.
- **ai-toolkit пишет чекпоинты в формате `<slug>_NNNNNNN.safetensors`**, финальный — на
  шаге 1750 при 2000 шагах и `save_every: 250`. Если шаги или save_every поменяют —
  править и `FINAL_CHECKPOINT_STEP` в `worker_lora.py`.
- **Входы — только из `input/ccs/<owner>/<run>/`.** Бэкенд фильтрует имена по `^[0-9a-f]{16}`
  и режет пути до 4 сегментов; воркер повторяет регэкспы и сверяет путь — нельзя
  перепутать `..` и попасть в чужой каталог.
- **После выхода воркера `input/ccs/<owner>/<run>/` и `out_dir` чистятся.** Фото и
  промежуточные артефакты ai-toolkit (samplers/, optimizer.bin, scheduler.bin) удаляются;
  остаётся только `<slug>.safetensors` и `config.yaml` (для отладки).