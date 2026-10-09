# Узел сходства лиц `CcsFaceSimilarity` на машине с ComfyUI

Решение и его причины — [docs/features/local-media.md](../../../docs/features/local-media.md),
раздел «LoRA-N»; контракт узла зафиксирован в задаче `352b5350-…`.

## Что где живёт

| Что | Где |
|---|---|
| Узел `CcsFaceSimilarity` (тонкий, только ComfyUI-зависимости) | `ccs_face_similarity/__init__.py` → копия в `~/ai-data/face-workers/ccs/ccs_face_similarity/` → симлинк в `ComfyUI-h3/custom_nodes/` |
| Воркер (тяжёлые зависимости в своём venv) | `ccs_face_similarity/worker_similarity.py` → копия в `~/ai-data/face-workers/ccs/ccs_face_similarity/` |
| Чистая арифметика (тестируется отдельно) | `ccs_face_similarity/core.py` |
| Тесты (рядом с узлом) | `ccs_face_similarity/tests/` |
| venv воркера | `~/ai-data/face-workers/.venv` |
| Конфиг воркера | `~/ai-data/face-workers/workers.json` |
| Веса buffalo_l (InsightFace) | `~/ai-data/face-models/models/buffalo_l/` (≈280 МБ) |

## Архитектура: воркер в своём venv

`install.sh` ставит `insightface`/`onnxruntime`/`opencv-python` в venv
`~/ai-data/face-workers/.venv`. Ставить их в venv ComfyUI нельзя: `numpy<2`
и `opencv` рядом с torch cu128 ComfyUI уронят весь ComfyUI целиком. Воркер
запускается отдельным процессом из узла по образцу `CcsAudioWorker`:

```
узел CcsFaceSimilarity                воркер
(процесс ComfyUI, тонкий):            (отдельный процесс в .venv face-workers):
- сохраняет IMAGE-тензор              - грузит InsightFace (CPU, ≈2-3 с)
  во временный PNG                      каждый вызов (см. «Время» ниже)
- парсит пути references               - считает эмбеддинги варианта и
- собирает job.json                     эталонов (≈0,5 с на эталон)
- запускает subprocess                 - центроид + cosine → similarity
  ~/face-workers/.venv/bin/python      - пишет CCS_RESULT {"similarity": ...}
   worker_similarity.py job.json       - выход
- читает stdout и парсит CCS_RESULT
- ui.text = ccs_face_similarity=
  <число> или none
```

Узел НЕ импортирует `insightface`, `onnxruntime` или `cv2`. На чистой машине с
только что поставленным ComfyUI узел загружается без `ModuleNotFoundError`.

Ошибка воркера, таймаут (60 с), отсутствие venv или путей → `ccs_face_similarity=none`
в `ui.text` и предупреждение в логе ComfyUI. Граф НЕ роняется: одна картинка без лица
не валит весь прогон автопараметров.

## Контракт

- Вход `image`: `IMAGE` — тензор после VAE-декода, тот же, что идёт в `SaveImage`.
- Вход `references`: `STRING` — имена файлов в `input/` ComfyUI, по одному на строку
  (как у `CcsAudioWorker.inputs`).
- Выход: `{"ui": {"text": ["ccs_face_similarity=0.771"]}}` или
  `ccs_face_similarity=none`, если лица нет ни в варианте, ни в одном эталоне.
- Внутри `ui.text` `stats` тоже пишется (в лог). Реальная строка от узла:

  ```
  ccs_face_similarity: готово, N/N эталонов, X с, stats={'load_s': ..., 'infer_s': ..., 'total_s': ..., 'references_total': ..., 'references_used': ...}
  ```

  Поля `stats` те же, что и раньше: `load_s`, `infer_s`, `total_s`, `references_total`,
  `references_used`. Старая строка `[face] stats=...` — это уже не отсюда, так что
  по ней искать в логах не надо.

Расчёт: 512-мерный эмбеддинг ArcFace (buffalo_l) считается для **каждого** найденного
лица в `image` и в каждом фото эталона. Сходство варианта = **максимум** косинуса этих
эмбеддингов к L2-нормализованному центроиду эталонов. Так в сценах с несколькими людьми
(«Андрей рядом с прохожим») нужное лицо даст сходство само, даже если оно меньше самого
крупного: раньше бралось только самое крупное, и на сцене «Андрей рядом с прохожим»
Артём получил 0,549 — крупный прохожий тянул центроид в свою сторону. Эталоны без лица
или с NaN/Inf в эмбеддинге пропускаются; без единого живого эталона — `none`.

### Шкала сходства

Ориентир, **не порог**: в фиче нет жёстких границ, сравниваются варианты одного запуска
(«этот вариант похож на Андрея больше, чем тот»). Абсолютная цифра зависит от набора
эталонов, и общей шкалы у узла нет. Замеры Артёма на тех же генерациях:

- 10 фото персонажа «Андрей» как эталоны: генерации ~0,68–0,73, реальные фото ~0,74–0,89.
- Центроид 43 фото датасета (скрипт Веры) как эталон: ~0,76–0,83 на тех же генерациях.

Один и тот же вариант на разных наборах эталонов даёт разные числа — на выбор лучшего
варианта это не влияет, потому что сравнение идёт внутри одного запуска (одни и те же
эталоны на все варианты). Ниже 0,5 — почти наверняка не тот человек; на 0,55–0,7 бывают
спорные случаи, окончательное решение остаётся за человеком.

## Установка

```bash
./install.sh                 # venv + зависимости + buffalo_l + workers.json + симлинк узла
./install.sh --link-only     # после выкатки новой версии: только копия узла и симлинк
```

Узел копируется в `~/ai-data/face-workers/ccs/ccs_face_similarity`, и ComfyUI ссылается
на копию, а не на рабочее дерево git: иначе `checkout` другой ветки или удаление worktree
ломает узел. Правка узла вступает в силу после рестарта `comfyui-h3` при пустой очереди.

Потом вручную:

1. Рестарт `comfyui-h3` — только при пустой очереди (`curl :8188/queue`).
2. Проверка: `curl -s :8188/object_info/CcsFaceSimilarity | head` отдаёт узел.

## Время

Загрузка `buffalo_l` поднимается на каждом вызове (CPU-onnxruntime, нет смысла
держать в памяти ComfyUI процесс): на GPU-машине Артёма `allowed_modules=["detection",
"recognition"]` срезал путь с ~5,6 с до ~3,2 с на вызов и убрал `FutureWarning` от
`landmark_3d_68`. На фоне ~25 с генерации Qwen-Image единицы секунд — терпимо.
Точные цифры `load_s`/`infer_s`/`total_s` пишутся воркером в `stats` (видно в логе
ComfyUI, см. выше).

Альтернатива — кеш модели в воркере: архитектурно сложнее (отдельный long-running
процесс с очередью, как у `CcsAudioWorker` для тяжёлых моделей вроде AudioSR).
`buffalo_l` на CPU стоит инициализации пара секунд, не окупается.

## Тесты

```bash
cd ccs_face_similarity
python3 -m unittest discover -s tests -v
```

Покрытие:
- `TestCore` — чистая арифметика на numpy (`centroid_l2`, `cosine`, `drop_nonfinite`,
  `best_similarity_from`): центроид двух ортогональных → 0.7071, длина центроида = 1,
  NaN/Inf отбрасываются, нет лица → `None`, два лица в варианте → берётся максимум.
- `TestWorker` — воркер с моками `insightface`/`cv2` через `sys.modules`: smoke
  сходства, smoke «нет лица», smoke «NaN/Inf», smoke «NaN отброшен, остальные
  живые», smoke «нужное лицо меньше самого крупного».
- `TestNode` — узел с моком `_config`, `folder_paths.get_input_directory`,
  `subprocess.Popen`, `time.sleep`: недоступный воркер → `none`, абсолютный путь /
  `..` отбрасываются, воркер вернул число → `0.771`, воркер вернул `null` → `none`,
  ненулевой код → `none`.

**Мутации и какие тесты краснеют:**

- «Снять финальную L2-нормировку центроида» (убрать
  `centroid / np.linalg.norm(centroid)` в `core.py:centroid_l2`): краснеют три теста —
  `test_centroid_is_l2_normalized_after_l2_step` (длина ≠ 1),
  `test_two_orthogonal_cosine_is_0_7071` (cosine тоже сдвигается на этой геометрии
  из-за проверки длины), `test_centroid_keeps_unnormalized_vectors_in_check`.
  `NaN/Inf` и `no_face` остаются зелёными — эти свойства от L2-шага не зависят.
- «Вернуть самое крупное лицо в варианте» (в `worker_similarity.py:_all_embeddings`
  вместо `[f.embedding for f in faces]` оставить `[biggest.embedding]`): краснеет
  `test_worker_max_face_picks_smaller_face_with_higher_similarity` — взяли бы
  прохожего, similarity ушла бы в 0. Остальные worker-тесты с одним лицом
  проходят (max от одного = оно само).
- «Вернуть `es[0]` в эталоне» (в `worker_similarity.py` цикл по `refs` вместо
  `_biggest_face(es).embedding` взять `es[0].embedding`): краснеет
  `test_worker_picks_biggest_face_in_reference` — на эталоне с двумя лицами
  `es[0]` в маппинге теста стоит мелким чужим, similarity падает в 0.

## Зависимости и лицензия

Узел ComfyUI: только то, что уже есть в ComfyUI (`numpy`, `PIL`, `torch`, `folder_paths`).
Никаких новых зависимостей в `requirements.txt` ComfyUI не добавляется.

Воркер (venv `~/ai-data/face-workers/.venv`):
- **insightface** (Apache-2.0): менеджер моделей и детекция.
- **onnxruntime** (MIT), **CPU-исполнитель**. onnxruntime-gpu не ставим — конфликтовал бы
  с cu128 ComfyUI по той же причине, по которой раньше ломался весь ComfyUI. Пусть
  ArcFace считается на CPU (десятки мс на эталон), чем мы потеряем GPU-очередь.
- **opencv-python** (Apache-2.0): чтение PNG-вариантов и эталонов.
- **numpy<2** (BSD): иначе opencv и insightface падают на 2.x.

Веса **buffalo_l** (InsightFace, **только некоммерческое использование** — лицензия
модели разрешает research и некоммерческое применение; коммерция требует отдельного
согласования с правообладателем). Это ограничивает обучение/правку, использующую
центроид эталона, некоммерческой рамкой.

## Грабли

- **CPU-onnxruntime обязателен.** `onnxruntime-gpu` в venv ComfyUI молча ломает
  `torch` и весь процесс ComfyUI целиком — без видимой ошибки в узле, но с OOM и
  падением сервера.
- **Воркер работает офлайн**: `HF_HUB_OFFLINE=1`/`TRANSFORMERS_OFFLINE=1` ставятся
  узлом перед запуском subprocess; `install.sh` качает buffalo_l явно, чтобы
  загрузка не держала общую GPU-очередь. После распаковки `buffalo_l.zip`
  (≈289 МБ) удаляется — оставлять его рядом с распакованной моделью не было
  причины, а место на диске не резиновое.
- **Имена файлов references валидируются** как у `CcsAudioWorker.inputs`: без
  абсолютных путей и `..`. Попытка заглянуть за пределы `input/` — ValueError,
  эталон пропускается, воркеру передаётся укороченный список.
- **Дефект CPU-детектора**: `det_size=(640, 640)` по умолчанию — лица меньше 80 px
  могут не опознаться. Меняется через `CCS_FACE_DET_SIZE` (целое).
- **Таймаут воркера**: 60 с по умолчанию. Если на вашей машине `buffalo_l` поднимается
  дольше (медленный диск, загруженный CPU) — добавьте запас, уменьшив частоту
  вызовов узла или прогревая модель отдельно.

## Сеть

Те же правила, что у [audio/README.md](../audio/README.md), раздел «Сеть»: трафик
идёт в туннель, для больших загрузок — `--interface br0` или локальный CONNECT-прокси
`HTTPS_PROXY=http://127.0.0.1:18888`. В install.sh `HF_HUB_DISABLE_XET=1` — xet-клиент
HF не видит `HTTPS_PROXY`. `TQDM_DISABLE=1` глушит прогресс insightface при скачивании
`buffalo_l` — иначе в логе простыня из символов прогресс-бара.
