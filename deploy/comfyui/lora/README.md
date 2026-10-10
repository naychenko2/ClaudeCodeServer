# Обучение LoRA персонажа на машине с ComfyUI

Решение — [ADR-026](../../../docs/adr/ADR-026-character-lora-training.md), провод и коды ошибок —
[ADR-026-contracts](../../../docs/adr/ADR-026-contracts.md) §5. Здесь — что где живёт и как ставить.

## Что где живёт

| Что | Где |
|---|---|
| Узел очереди | `CcsAudioWorker` (`deploy/comfyui/audio/ccs_audio_worker`): op `lora_train`, `lora_delete` → `_run_lora` |
| Воркер и шаблон пресета | `worker_lora.py`, `templates/standard.yaml` → копия в `~/ai-data/audio-workers/ccs-lora` |
| ai-toolkit | `~/ai-data/audio-workers/ai-toolkit` на коммите `a949f21` |
| venv и extras Qwen-Image-2.1 | `~/ai-data/audio-workers/lora/.venv`, `~/ai-data/audio-workers/lora/models/Qwen-Image-2.1` |
| Базовые веса | `qwen_image_2.1_int8_convrot.safetensors` из `diffusion_models` ComfyUI, путь — в env семейства |
| Результат | `~/ai-data/comfy-models/loras/ccs/<owner>/<slug>-<runId>/<slug>.safetensors` |
| Семейство в конфиге | `lora` в `~/ai-data/audio-workers/workers.json` |

Пресет `standard` повторяет ручной прогон v1 (`~/lora/config/andreyk.yaml`): rank/alpha 16, lr 1e-4,
adamw8bit, bf16, convrot8 для модели и энкодера, `low_vram`, разрешения 512/768/1024, 2000 шагов,
`save_every` 250; итог — чекпоинт шага 1750. Пробные картинки выключены (`disable_sampling`).

## Установка

```bash
./install.sh               # ai-toolkit, venv, extras, InsightFace, узел, workers.json
./install.sh --link-only   # после выкатки: узел, воркер и workers.json
```

Оба режима сначала зовут `../audio/install.sh --link-only`: узел общий с аудио и выкатывается
только так. Затем рестарт `comfyui-h3` при ПУСТОЙ очереди и `LocalMedia:LoraTrainEnabled=true`
в `appsettings.Local.json` бэкенда.

Ставить юнитом, а не из шелла ssh: `setsid`-процесс внутри `wsl -e` умирает вместе с сессией.

```bash
sudo systemd-run --unit=ccs-lora-install --uid=an --setenv=HOME=$HOME bash -c './install.sh > ~/lora-install.log 2>&1'
```

## Грабли

- **`workers.json` общий с аудио.** Оба установщика пишут его только через `merge_workers.py` — своё
  семейство, не трогая чужие. Запись целиком однажды стёрла все аудиосемейства на gpuhost.
- **Каталоги раздельные.** Аудио выкатывается в `ccs/` с `rsync --delete`, поэтому LoRA живёт в `ccs-lora/`.
- **Без полной выгрузки моделей ComfyUI обучение не влезает** (замер 1г: 24,19 из 24,58 ГБ) — узел
  зовёт `_free_vram(None)`; пик самого обучения 17,6 ГБ.
- **Extras — только при установке.** ai-toolkit берёт энкодер, VAE и конфиги из `extras_name_or_path`;
  без локального каталога он идёт в HF и падает под `HF_HUB_OFFLINE=1`, с которым узел запускает воркер.
- **ai-toolkit — в той же группе процессов, что и воркер**: `/interrupt` гасит группу, и обучение
  освобождает карту вместе с воркером.
- **`CCS_LORA_MAX_STEPS`** в env семейства укорачивает прогон для проверки на стенде; итог тогда —
  последний шаг. В рабочем конфиге переменной быть не должно.

## Миграция ручных LoRA в `ccs/<ownerKey>/`

После фильтра видимости (`LoraNamespace.VisibleTo`) LoRA вне `ccs/<ownerKey>/…` не видны и не подключаются.
Переносятся двумя парными скриптами, по умолчанию оба в `--dry-run`; применять вместе:

- `migrate-manual-loras.sh` — файлы на gpuhost (`~/ComfyUI/models/loras`, корень «photo»), только `mv`, существующее не перезаписывается, имена вне `[A-Za-z0-9._-]` пропускаются. `ssh -o BatchMode=yes gpuhost 'wsl -e bash -s -- --owner-key <32 hex> [--only <имя>] [--apply]' < migrate-manual-loras.sh`; журнал отката пишется в `.ccs-migration/` рядом, откат — `--rollback <журнал>`.
- `scripts/migrate-lora-manifests.py` — манифесты `characters/*/character.json` серверных проектов владельца (`providers.local.lora` → `ccs/<ownerKey>/<имя>`), запись атомарная, бэкап `character.json.bak-<дата>`; тесты — `python3 -m unittest scripts/test_migrate_lora_manifests.py`. `python3 scripts/migrate-lora-manifests.py --username <имя> [--apply]`.
