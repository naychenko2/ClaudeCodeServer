# Маршрут `GET /ccs/stats` на машине с ComfyUI

Фича `local-model-stats`, шаг 32 плана. ComfyUI медиа живёт на отдельном узле (одна
RTX 3090), и хостовый `nvidia-smi` бэкенда эту карту не видит. Поэтому вкладка «Медиа»
панели «Локальные модели» берёт отсюда две вещи: «В памяти» (что ComfyUI держит загруженным
прямо сейчас, а не «чем последний раз работали») и карту «RTX 3090 · медиа».

Узлов графа пакет не добавляет, только маршрут на сервере ComfyUI. ComfyUI дублирует его с
префиксом: `/api/ccs/stats` отвечает тем же.

## Что где живёт

| Что | Где |
|---|---|
| Регистрация маршрута (тонкая, `server` + `aiohttp`) | `ccs_stats/__init__.py` → копия в `~/ai-data/ccs-stats/ccs_stats/` → симлинк в `ComfyUI-h3/custom_nodes/` |
| Сборка ответа (чистая, источники — аргументами) | `ccs_stats/core.py` |
| Тесты (на узел не едут) | `ccs_stats/tests/` |
| Пример ответа (фикстура для теста бэкенда, шаг 31) | `example-response.json` |

## Ответ

```json
{
  "version": 1,
  "loadedModels": [
    { "name": "qwen_image_2.1_int8_convrot.safetensors", "sizeMb": 19530, "loadedMb": 19530 },
    { "name": "AutoencoderKL", "sizeMb": 242, "loadedMb": 242 }
  ],
  "gpus": [
    { "index": 0, "name": "NVIDIA GeForce RTX 3090", "utilPerc": 97, "memUsedMb": 21874,
      "memTotalMb": 24576, "tempC": 71, "powerW": 338, "powerLimitW": 350 }
  ]
}
```

- `version` — версия формата. Растёт только при ломающем изменении; новые поля добавляются
  без смены версии, клиент обязан пропускать незнакомые.
- `loadedModels` берётся из `comfy.model_management.current_loaded_models`, не больше 16 записей.
  - `name` — basename файла из `ModelPatcher.cached_patcher_init`: его заполняют штатные
    загрузчики ComfyUI 0.37 (диффузия, CLIP, чекпойнт). У CLIP из нескольких файлов имена
    склеены через `+`. Если файла нет (например, у VAE из чекпойнта), берётся имя класса
    модели (`QwenImage`, `AutoencoderKL`). Символы вне `[A-Za-z0-9._+-]` заменяются на `_`.
  - Путь с компонентом `loras` целиком заменяется на `lora`: LoRA пользователей лежат в
    `loras/ccs/<владелец>/<слаг>`, наружу не отдаём ни владельца, ни слаг.
  - `sizeMb` — полный размер модели, `loadedMb` — сколько из него сейчас в VRAM; оба в МиБ.
    Если ComfyUI размер не отдал, поле равно `null`.
- `gpus` — карты узла через `pynvml`; память в МиБ, мощность в ваттах. Поле, которое NVML не
  отдал (в WSL так бывает с мощностью), приходит как `null`.
- `error` (рядом с пустым `loadedModels`) и `gpusError` (рядом с `gpus: null`) появляются
  только при сбое. В них только место и имя типа исключения (`nvmlInit: OSError`), без текста:
  текст исключений ComfyUI и NVML бывает с путями.

Маршрут только читает и не падает: любой сбой даёт пустой блок с причиной и код 200.
Путей файловой системы и данных пользователей в ответе нет, заголовок —
`Cache-Control: no-store`.

## Установка — только в тихое окно

Подключение пакета требует **рестарта `comfyui-h3`**. Рестарт обрывает идущую задачу и
сбрасывает очередь, а очередь у ComfyUI одна на оба инстанса продукта, дев и бой. Поэтому:

1. На узле, из копии репозитория (рабочей ветки или master после мержа):

   ```bash
   ./deploy/comfyui/stats/install.sh
   ```

   Скрипт копирует пакет в `~/ai-data/ccs-stats/ccs_stats` и ставит симлинк в `custom_nodes`.
   Ещё он проверяет, что `pynvml` импортируется в venv ComfyUI. ComfyUI скрипт не
   перезапускает и venv не трогает. Если `pynvml` нет, он печатает команду установки
   `nvidia-ml-py`: это чистый Python без зависимостей. Без него маршрут всё равно работает,
   только `gpus` будет `null`.
2. Проверить, что очередь пуста: `curl -s :8188/queue`, в `queue_running` и `queue_pending`
   должно быть пусто. Это и есть общая очередь обоих инстансов. Если в ней что-то есть,
   ждать: на бою у задачи может быть живой пользователь.
3. Рестарт юнита `comfyui-h3`.
4. Живость: `curl -s :8188/system_stats` отвечает JSON (ping не годится, нужен именно HTTP).
5. Маршрут: `curl -s :8188/ccs/stats` отдаёт ответ формата выше. В логе ComfyUI нет строки
   `Cannot import … ccs_stats`.

Повторный запуск `install.sh` идемпотентен. Правка пакета вступает в силу после следующего
рестарта, тоже в тихое окно.

## Откат

```bash
rm "$HOME/ComfyUI-h3/custom_nodes/ccs_stats"   # только симлинк; копия в ~/ai-data/ccs-stats не мешает
```

Затем рестарт `comfyui-h3` при пустой очереди и проверка живости, как в шагах 2–4. Если
маршрута нет, бэкенд получает 404 и честно пишет в панели «не сообщает»: на генерацию это не
влияет.

## Тесты

Из каталога пакета; ComfyUI, `pynvml` и `aiohttp` для запуска не нужны:

```bash
cd deploy/comfyui/stats/ccs_stats
python3 -m unittest discover -s tests -v
```

Тест `ContractTests.test_keys_match_example` сверяет ключи ответа с `example-response.json`.
Если поменялся формат, пример правится в том же коммите, и тест бэкенда (шаг 31) увидит это
по фикстуре.

Самопроверка сбора карт на любой машине с NVIDIA и `nvidia-ml-py`: команда только читает
NVML, ComfyUI не нужен.

```bash
python3 -I -c "import sys,types,json; sys.path.insert(0,'deploy/comfyui/stats/ccs_stats'); \
import core, pynvml; print(json.dumps(core.build_response(types.SimpleNamespace(current_loaded_models=[]), pynvml)))"
```
