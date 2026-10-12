# Замер сходства «подобранное фото против главного» для local — 2026-10

**Задача:** `28d2bef8-4f70-4a30-b219-80394c9cbe63` (шаг 21 плана `6cd0b115`).
**Цель:** подобрать порог `LocalConfidentScore` (ADR-027, §7), при котором подмена
главного фото на «подобранное» по сюжету на local **не роняет** сходство.
**Персонаж:** `andrey` (10 фото, размечены ракурсы автоматом, эмоции `null`).

## Условия и стенд

- Сетка — локальный ComfyUI gpuhost/WSL2 (PID 39736, `python main.py --listen 0.0.0.0 --port 8188`, очередь пуста).
- Модели: `qwen_image_2.1_int8_convrot.safetensors`, `qwen3vl_8b_int8_convrot.safetensors`,
  `qwen_image_2.1_vae_bf16.safetensors`.
- LoRA персонажа (та, что в манифесте):
  `ccs/a300cc8f007447ab865c138cd39c2c18/andreyk/andreyk_qwen21_v1_000001750.safetensors`,
  strength 0.8, триггер `andreyk`. Граф повторяет
  [`ComfyWorkflows.EditImage`](../../backend/ClaudeHomeServer.Images/Services/LocalMedia/ComfyWorkflows.cs)
  с `lora` и одним фото-образцом в `LoadImage → TextEncodeQwenImage21.images.image_1`.
- Размер холста — 1024×1024 (кратен 16), steps 25, cfg 1.0, sampler euler, scheduler simple, denoise 1.0.
- Тот же seed (42) для A и B в одной сцене — пара сравнивается напрямую.
- Замер сходства — локально, тем же способом, что и продукт:
  [`arcface_score.py`](../../.cc-attachments/char-tests/tools/arcface_score.py) с
  `/tmp/faceenv/bin/python` (InsightFace 2.1 buffalo_l, CPUExecutionProvider),
  центроид `andrey_centroid_v2.npy` (43 фото).
- **Сценарии приватности:** все фото — копия в ComfyUI input
  `ccs-similarity/`; PNG-результаты лежат локально на время анализа; по окончании
  удаляются. В отчёте — только числа и имена исходных фото.

## Сетка

8 сцен × 2 запуска (A/B) × seed 42 = 16 прогонов.

| Сцена | Промпт | Признак (rules) | A | B | pick |
|---|---|---|---|---|---|
| profile-armor | «портрет в профиль у окна, в средневековых доспехах» | `profile` | face-08 | face-03 | auto |
| profile-cyberpunk | «портрет в профиль на фоне ночного города, киберпанк» | `profile` | face-08 | face-03 | auto |
| profile-serious | «в профиль, серьёзный взгляд» | `profile`+`serious` | face-08 | face-03 | auto |
| three-quarter-tails | «стоит вполоборота, в смокинге на балу» | `three-quarter` | face-08 | face-02 | auto |
| three-quarter-cabinet | «три четверти, мягкий свет в кабинете» | `three-quarter` | face-08 | face-02 | auto |
| front-spaceman | «смотрит анфас, в космическом шлеме без стекла, крупный план» | `front` | face-08 | face-01 | auto |
| neutral-1 | «просто портрет на сером фоне» | (пусто) | face-08 | face-08 | fallback |
| neutral-2 | «вид у моря» | (пусто) | face-08 | face-08 | fallback |

Промпт с триггером LoRA — как `LocalImageEditor.WithTrigger`:
`photo of andreyk, <prompt>`.
Ничья по очкам решается в пользу фото с меньшим индексом манифеста
(`CharacterPhotoPicker.OrderByDescending(score).ThenBy(index)`) — даёт
**face-03** для profile, **face-02** для three-quarter, **face-01** для front.

Эмоции в манифесте `null` у всех фото, поэтому эмоциональные сюжеты
не дают ненулевого подбора (`CharacterPhotoPicker.Score` = 0 → fallback).
Дополнительные сюжеты с эмоцией я не добавлял, чтобы не размывать вывод:
результаты зависят от ещё не размеченных `emotion`.

## Результаты

| Сцена | A → B_score | A photo (sim) | B photo (sim) | Δ (B-A) |
|---|---:|---:|---:|---:|
| profile-armor | 2 | face-08 (0.649) | face-03 (0.603) | **−0.046** |
| profile-cyberpunk | 2 | face-08 (0.631) | face-03 (0.667) | **+0.036** |
| profile-serious | 2 | face-08 (0.657) | face-03 (0.649) | **−0.008** |
| three-quarter-tails | 2 | face-08 (0.744) | face-02 (0.785) | **+0.041** |
| three-quarter-cabinet | 2 | face-08 (0.713) | face-02 (0.783) | **+0.070** |
| front-spaceman | 2 | face-08 (0.724) | face-01 (0.680) | **−0.044** |
| neutral-1 | 0 (fallback) | face-08 (0.844) | face-08 (0.844) | 0 |
| neutral-2 | 0 (fallback) | face-08 (0.806) | face-08 (0.806) | 0 |

Сводка по 6 сценам с `pick=auto`:

- **Среднее Δ (B−A) = +0.008** — практически ноль.
- **B лучше A в 3 из 6:** three-quarter-tails (+0.041), three-quarter-cabinet (+0.070),
  profile-cyberpunk (+0.036).
- **B хуже A в 3 из 6:** profile-armor (−0.046), front-spaceman (−0.044),
  profile-serious (−0.008).
- **Минимальное B = 0.603** (profile-armor) — заметно ниже порога 0.72.
  Во всех трёх profile-сценах B ≤ 0.667; в обеих three-quarter — ≥ 0.783;
  в front-сцене B = 0.680.

## Сходство самих эталонов с центроидом

Контекст для интерпретации. Центроид построен по 43 фото
(`andrey_centroid_v2.npy`), и **profile-фото сами по себе заметно
дальше от него, чем анфас**:

| Файл | angle | sim к центроиду |
|---|---|---:|
| face-08 (главное) | front | **0.871** |
| face-10 | front | 0.862 |
| face-01 | front | 0.837 |
| face-05 | three-quarter | 0.832 |
| face-02 | three-quarter | 0.803 |
| face-09 | front | 0.797 |
| face-06 | profile | 0.754 |
| face-07 | profile | 0.705 |
| face-04 | profile | 0.703 |
| face-03 | profile | **0.695** |

Сам LoRA на тренировке помнит лицо в основном в анфас/three-quarter,
и в профиль центроид «слабее». Это структурное ограничение набора
Андрея, не дефект подбора.

## Вывод: порог `LocalConfidentScore`

**Подмену главного фото на «подобранное» на local у персонажа Андрей включать нельзя** — порог должен остаться «никогда» (`int.MaxValue`). Причины по критериям задачи:

1. **Среднее Δ (B−A) ≈ 0**, а не в пользу B. Подбор в среднем не помогает.
2. **В 3 из 6 сцен с `pick=auto` B строго хуже A** — это не «без эффекта»,
   это регрессия у трети сюжетов. Для пользователя это выглядит как
   «нажал сменить ракурс — стало хуже».
3. **Ни одна из profile-сцен не достигает 0.72 по B** (0.603 / 0.667 / 0.649).
   Условие плана «ни одна сцена не ниже 0.72» не выполнено.
4. В трёх-quarter-сценах подбор работает (B = 0.785 / 0.783), но без явного
   деления по ракурсу порог не отличит «где помогает» от «где вредит».

Любое конечное значение `LocalConfidentScore` (например, 2 — точное
совпадение ракурса) разрешит подмену и в profile-сценах, где B ниже A.
Чтобы порог был «полезен», нужны отдельные правила: например,
не подменять при `scene.angle == "profile"`; или подменять только если
B.score ≥ 4 (ракурс + наклон + эмоция). Эти правила — отдельная
разработка; до неё константа остаётся `int.MaxValue`.

Денис по этому числу править ничего не должен.

## Проба ракурса (для снятия флага)

Условие шага 14 ADR-027 — повторная проба `face.pose` (InsightFace
buffalo_l) на наборе без дублей со слепой ручной разметкой, ≥85 %
точных совпадений по трём категориям и ни одной путаницы
анфас ↔ профиль.

Условие выполнено: в
[`face-pose-probe-2026-10.md`](../research/face-pose-probe-2026-10.md) —
**45 из 50 фото = 90 %**, **0 путаниц** анфас ↔ профиль, именованные оси,
набор без дублей, метки закрыты до чисел.

## Скрипты

Скрипты замера лежат в `/tmp/smart-photos-similarity/scripts/` и не
коммитятся — это рабочая артефакт-папка в `/tmp/`. Сценарии:

- `scenes.py` — список сцен и `parse_by_rules` (повтор
  `CharacterSceneParser.ParseByRules`).
- `run_generate.py` — сборка графа и прогон через ComfyUI HTTP.
- `run_score.py` — замер ArcFace через `/tmp/faceenv/bin/python`.

Сводный запуск:

```bash
python3 /tmp/smart-photos-similarity/scripts/run_generate.py --seed 42
/tmp/faceenv/bin/python /home/an/Sources/ClaudeCodeServer/.cc-attachments/char-tests/tools/arcface_score.py \
  --ref /home/an/Sources/ClaudeCodeServer/.cc-attachments/char-tests/tools/andrey_centroid_v2.npy \
  /tmp/smart-photos-similarity/results/*/[AB]/seed42_*.png
```

## Удаление

Промежуточные PNG удалены после замера (`rm -rf
/tmp/smart-photos-similarity/results`); сами скрипты и этот отчёт —
единственные следы прогона в системе. Имена исходных фото (`face-01..10`)
из `/home/an/Sources/ClaudeCodeServer/characters/andrey/` не менялись.

## sha

Коммит этого отчёта: см. `git log --oneline -1 docs/research/smart-photos-similarity-2026-10.md`.
Базовая голова worktree ветки `feat/character-smart-photos`: `e2d86667`.