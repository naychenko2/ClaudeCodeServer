# Bonsai 2 на «Video» — дежурная модель одношаговых мест

Машина «Video» (192.168.7.107, WSL2, 1×RTX 3090) держит ComfyUI (картинки, видео H3, WhisperX)
и LoRA-тренер. Рядом с ними живёт **Bonsai 2 27B** (тернарный Qwen3.8-27B, 7,2 ГБ, llama-server
из форка PrismML) — на неё CCS уводит одношаговые места (заголовки, теги, классификация,
нормализация задач, сводки…), чтобы не ходить в облако и не грузить прод-движок ходов. Карту
не делит: **gpu-arbiter** в режиме `exclusive` останавливает Bonsai при любом задании ComfyUI,
аренде от CCS или чужом GPU-процессе и возвращает после тишины, предварительно выгрузив кэш
моделей ComfyUI (`/free`).

Замеры 26.10.2026 (сборка cuda-13.3 `prism-b10826`):

| | 27B PQ2_0 (в юните) | 9B PQ2_0 (запасной) |
|---|---|---|
| Генерация / промпт, tok/s | 85 / 1584 | 218 / 4377 |
| Старт до `/health` | 5,5 с из page cache (9,8 с с диска) | 1,8 с |
| VRAM | 8,3 ГБ | 2,9 ГБ |
| Качество (A/B против MiniMax-M3.1-Flash) | 10 мест на уровне MiniMax | только notes-tags и task-classify |
| Соседство с кэшем ComfyUI (17,9 ГБ в простое) | нет → режим `exclusive` | да → режим `shared` |

Уступает за 0,2 с (чужой процесс ловится за ≈1,3 с). Цена режима `exclusive`: первая картинка
серии после возврата Bonsai холодная (+15 с: 40 с против 25), видео H3 +20 с; поэтому пауза
возврата после заданий ComfyUI — `ARBITER_RETURN_QUIET_AFTER_COMFY_S` (180 с), а после аренды
или чужого процесса — `ARBITER_RETURN_QUIET_S` (10 с).

## Раскладка на Video (`/home/an/bonsai`)

```
bin/cuda133/            релиз форка PrismML-Eng/llama.cpp, linux-cuda-13.3 (bin/cuda128 — запас)
cudalibs/cu13/          libcudart/libcublas/libcublasLt .so.13 (копия из venv ComfyUI)
models/                 Ternary-Bonsai-2-9B-PQ2_0.gguf (+ -MTP-Q8_0, + 27B на будущее)
gpu-arbiter.py          арбитр (этот каталог репозитория — источник)
logs/
```

Юниты: `/etc/systemd/system/bonsai.service`, `/etc/systemd/system/gpu-arbiter.service`
(копии здесь). Установка:

```bash
sudo cp bonsai.service gpu-arbiter.service /etc/systemd/system/
cp gpu-arbiter.py /home/an/bonsai/
sudo systemctl daemon-reload && sudo systemctl enable --now bonsai gpu-arbiter
```

Откат: `sudo systemctl disable --now bonsai gpu-arbiter` — ComfyUI и остальное не затронуты.

## Арбитр (порт 18031)

Правила уступки: аренда от CCS (`POST /lease {"ttl_s", "reason"}` → `{"id"}`,
`DELETE /lease/{id}`), чужой процесс на GPU (всё, кроме ComfyUI и llama-server), задание в
очереди ComfyUI — в режиме `exclusive` любое, в `shared` только тяжёлый граф (маркеры
`h3, minimax, video, fasth3, whisper` в `class_type` и строковых входах узлов:
`MiniMaxH3ImageToVideo`, `H3MultiStream`, `minimax_h3_*`). Возврат — после паузы без оснований
(см. выше); в `exclusive` перед стартом `POST /free` ComfyUI и ожидание `ARBITER_VRAM_NEEDED_MB`
(11 000) свободных, иначе состояние `waiting-vram`. `GET /ready` — 200, когда Bonsai отвечает
(это и спрашивает CCS перед вызовом), `GET /state` — подробности, счётчики и `quiet_left_s`.

Режим `shared` (для 9B): картинки Qwen-Image и Klein Bonsai не выгоняют — 9B + KV ≈ 3 ГБ
умещаются рядом с кэшем ComfyUI (пик картинки 21,8 ГБ без OOM). Переключение на 9B — модель и
алиас в `bonsai.service`, `ARBITER_MODE=shared` в `gpu-arbiter.service`, `Model: bonsai-9b` в CCS.

## CCS

Секция `LocalEngines:bonsai` в `appsettings.Local.json` (см. `docs/architecture/llm-providers.md`,
раздел «Именованные локальные движки»).
