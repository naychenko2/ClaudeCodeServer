# Узел сходства лиц `CcsFaceSimilarity` на машине с ComfyUI

Решение и его причины — [docs/features/local-media.md](../../../docs/features/local-media.md),
раздел «LoRA-N»; контракт узла зафиксирован в задаче `352b5350-…`.

## Что где живёт

| Что | Где |
|---|---|
| Узел `CcsFaceSimilarity` | `ccs_face_similarity/` → копия в `~/ai-data/face-workers/ccs` → симлинк в `ComfyUI-h3/custom_nodes/` |
| venv | `~/ai-data/face-workers/.venv` |
| Веса buffalo_l (InsightFace) | `~/ai-data/face-models/models/buffalo_l/` (≈280 МБ) |

Узел ставится как обычный custom node, не идёт в общую очередь ComfyUI через
`CcsAudioWorker`: вычисление одношаговое, запускается в том же процессе ComfyUI и
считается на CPU. Между вызовами держится кеш моделей (`insightface.FaceAnalysis` —
тяжёлый объект, повторная инициализация отжирает секунды).

## Контракт

- Вход `image`: `IMAGE` — тензор после VAE-декода, тот же, что идёт в `SaveImage`.
- Вход `references`: `STRING` — имена файлов в `input/` ComfyUI, по одному на строку
  (как у `CcsAudioWorker.inputs`).
- Выход: `{"ui": {"text": ["ccs_face_similarity=0.771"]}}` или
  `ccs_face_similarity=none`, если лица нет ни в варианте, ни в одном эталоне. Это не
  ошибка: граф должен завершиться успешно.

Расчёт: самое крупное лицо в `image` и в каждом фото эталона (по площади bbox,
при равенстве — по `det_score`), 512-мерный эмбеддинг ArcFace (buffalo_l), косинус к
L2-нормализованному центроиду эталонов. Эталоны без лица пропускаются, без единого
эталона с лицом — `none`.

## Установка

```bash
./install.sh                 # venv + зависимости + buffalo_l + симлинк узла
./install.sh --link-only     # после выкатки новой версии: только копия узла и симлинк
```

Узел копируется в `~/ai-data/face-workers/ccs`, и ComfyUI ссылается на копию, а не на
рабочее дерево git: иначе `checkout` другой ветки или удаление worktree ломает узел.
Правка узла вступает в силу после рестарта `comfyui-h3` при пустой очереди.

Потом вручную:

1. Рестарт `comfyui-h3` — только при пустой очереди (`curl :8188/queue`).
2. Проверка: `curl -s :8188/object_info/CcsFaceSimilarity | head` отдаёт узел.

## Зависимости и лицензия

- **insightface** (Apache-2.0): менеджер моделей и детекция.
- **onnxruntime** (MIT), CPU-исполнитель. onnxruntime-gpu не ставим: в venv ComfyUI
  свой torch cu128, конфликт пакетов ломает весь ComfyUI; пусть ArcFace считается
  на CPU (десятки мс на эталон), чем мы потеряем GPU-очередь.
- **opencv-python** (Apache-2.0): чтение эталонов с диска.
- **buffalo_l** (InsightFace, **только некоммерческое использование** — лицензия модели
  разрешает research и некоммерческое применение; коммерция требует отдельного
  согласования с правообладателем). Это ограничивает обучение/правку, использующую
  центроид эталона, некоммерческой рамкой.

## Грабли

- **CPU-onnxruntime обязателен.** `onnxruntime-gpu` в venv ComfyUI молча ломает
  `torch` и весь процесс ComfyUI целиком — без видимой ошибки в узле, но с OOM и
  падением сервера.
- **Узел работает офлайн** (`HF_HUB_OFFLINE=1`/`TRANSFORMERS_OFFLINE=1` не нужны —
  insightface читает из `root/models/buffalo_l/`, но в install.sh качается явно,
  чтобы загрузка не держала GPU-очередь.
- **Имена файлов references валидируются** как у `CcsAudioWorker.inputs`: без
  абсолютных путей и `..`. Попытка заглянуть за пределы `input/` — ValueError,
  узел вернёт `ccs_face_similarity=error`.
- **Дефект CPU-детектора**: `det_size=(640, 640)` по умолчанию — лица меньше 80 px
  могут не опознаться. Меняется через `CCS_FACE_DET_SIZE` (целое).
- **Кеш модели модульный**: процесс ComfyUI держит одну `FaceAnalysis` на себя.
  Если на той же машине поднимут ещё один ComfyUI на другом порту — у каждого свой кеш.

## Сеть

Те же правила, что у [audio/README.md](../audio/README.md), раздел «Сеть»: трафик
идёт в туннель, для больших загрузок — `--interface br0` или локальный CONNECT-прокси
`HTTPS_PROXY=http://127.0.0.1:18888`. В install.sh `HF_HUB_DISABLE_XET=1` — xet-клиент
HF не видит `HTTPS_PROXY`.