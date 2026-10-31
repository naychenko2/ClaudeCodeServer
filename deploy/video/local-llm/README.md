# Дежурная LLM на «Video» — Gemma 4 26B-A4B

Машина «Video» (192.168.7.107, WSL2, 1×RTX 3090) держит ComfyUI (картинки, видео H3, WhisperX)
и LoRA-тренер. Рядом с ними живёт **Gemma 4 26B-A4B** (MoE, ~4B активных, квант unsloth
UD-IQ4_XS 13,6 ГБ, llama-server из форка PrismML) в двух ролях:

- **движок мест** CCS (`LocalEngines:gemma`): одношаговые места — заголовки, теги, классификация,
  нормализация задач, сводки, сообщения коммитов, решения памяти (18 мест);
- **локальный провайдер чатов** (`LlmProviders:local-video-gemma`, модель `gemma-4-26b-a4b` в каталоге):
  лёгкие разговорные роли через Claude CLI (Anthropic API llama-server).

Карту не делит: **gpu-arbiter** в режиме `exclusive` останавливает модель при любом задании
ComfyUI, аренде от CCS или чужом GPU-процессе и возвращает после тишины, предварительно выгрузив
кэш моделей ComfyUI (`/free`).

## Почему Gemma (A/B 31.10.2026, 17 мест × 20 входов + action-rank, эталон MiniMax-M3.1-Flash)

| | Gemma 4 26B-A4B | Qwen3.8-27B Q4 | Ornith-1.5-35B-A3B | Bonsai 2 27B (до 01.11) |
|---|---|---|---|---|
| Валидных ответов | **337/340** | 337/340 | 319/340 | 330/340 |
| Коммиты строго Conventional Commits | **15/20** | 15/20 | 0/20 | 6/20 |
| Решения памяти = MiniMax | **16/20** | 10/20 | 11/20 | 10/20 |
| Генерация / промпт, tok/s | 124 / **3534** | 49 / 1480 | 168 / 2015 | 85 / 1584 |
| Старт до `/health` | 11 с | 10 с | 38 с | 5,5–10 с |

Параллельность (16 слотов по 8K, промпт 1,5K, ответ 256): 1 запрос — 131 tok/s; 4 — 81 на запрос,
153 суммарно; 8 — 45 на запрос, 165 суммарно (потолок); 16 — суммарно падает до 111. Окно модели
262 144 токена целиком помещается в карту (≈ 20 ГБ); генерация на 200K контекста — ≈ 57 tok/s,
чтение промпта на глубине 108→203K — ≈ 1 300 tok/s.

## Раскладка на Video (`/home/an/local-llm`)

```
bin/cuda133/            релиз форка PrismML-Eng/llama.cpp, linux-cuda-13.3 (bin/cuda128 — запас)
cudalibs/cu13/          libcudart/libcublas/libcublasLt .so.13 (копия из venv ComfyUI)
models/                 gemma-4-26B-A4B-it-UD-IQ4_XS.gguf (+ прежние модели на откат)
gpu-arbiter.py          арбитр (этот каталог репозитория — источник)
logs/
```

Юниты (копии здесь): `/etc/systemd/system/local-llm.service` (+ drop-in `local-llm.service.d/wait-models.conf`
— ждёт SSD моделей) и `/etc/systemd/system/gpu-arbiter.service`. Установка:

```bash
sudo cp local-llm.service gpu-arbiter.service /etc/systemd/system/
sudo mkdir -p /etc/systemd/system/local-llm.service.d && sudo cp local-llm.service.d/*.conf /etc/systemd/system/local-llm.service.d/
cp gpu-arbiter.py /home/an/local-llm/
sudo systemctl daemon-reload && sudo systemctl enable --now local-llm gpu-arbiter
```

Откат: `sudo systemctl disable --now local-llm gpu-arbiter` — ComfyUI и остальное не затронуты.

## Арбитр (порт 18031)

Правила уступки: аренда от CCS (`POST /lease {"ttl_s", "reason"}` → `{"id"}`,
`DELETE /lease/{id}`), чужой процесс на GPU (всё, кроме ComfyUI и llama-server), задание в
очереди ComfyUI — в режиме `exclusive` любое, в `shared` только тяжёлый граф (маркеры
`h3, minimax, video, fasth3, whisper`). Возврат — после паузы без оснований:
`ARBITER_RETURN_QUIET_AFTER_COMFY_S` (180 с) после заданий ComfyUI, `ARBITER_RETURN_QUIET_S` (10 с)
после аренды или чужого процесса; в `exclusive` перед стартом `POST /free` ComfyUI и ожидание
`ARBITER_VRAM_NEEDED_MB` (22 000) свободных, иначе состояние `waiting-vram`.

`GET /ready` — 200, когда модель отвечает (это и спрашивает CCS перед вызовом места);
`GET /state` — `{"engine": "ready|yielded|starting|waiting-vram", "reasons", счётчики, quiet_left_s, leases}`.
До 01.11.2026 поле состояния называлось `bonsai` — CCS читает и его, если `engine` нет.

## CCS

Обе секции — в машинном `appsettings.Local.json` (в `.gitignore`); образец —
`backend/ClaudeHomeServer/appsettings.Local.example.json` (хост `<video-host>`), устройство —
`docs/architecture/llm-providers.md`, раздел «Именованные локальные движки».

- `LocalEngines:gemma` — `BaseUrl` (`:18030`), `Model` (`gemma-4-26b-a4b`), `Title` (подпись вкладки
  панели «Локальные модели», «Gemma 4»), `Actions`, `FallbackRoute` (`direct:MiniMax-M3.1-Flash`),
  `NumCtx` (32 768), `MetricsUrl` (`:18030/metrics`), `ReadyUrl` и `StateUrl` (`:18031`).
- `LlmProviders:local-video-gemma` — `AnthropicBaseUrl`, `IsLocal`, `MaxConcurrentTurns` 2 (два слота
  остаются местам), модель `gemma-4-26b-a4b` с `ContextWindow` 262 144 (пул KV сервера — 384K в q8_0). Пока модель уступила карту,
  проба видит её Down, и ход чата получает «локальная модель не запущена».
