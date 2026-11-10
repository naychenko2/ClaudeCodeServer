# Дежурная LLM на «Video» — vLLM

Машина «Video» (192.168.7.208, WSL2, 1×RTX 3090) держит ComfyUI (картинки, видео H3, WhisperX)
и LoRA-тренер. Рядом с ними на карте живёт **vLLM** — дежурная модель CCS в двух ролях:

- **движок мест** CCS (`LocalEngines:{имя}`): одношаговые места — заголовки, теги, классификация,
  нормализация задач, сводки, сообщения коммитов, решения памяти;
- **локальный провайдер чатов** (`LlmProviders:{ключ}`, `IsLocal`): лёгкие разговорные роли через
  Claude CLI; ходы такого провайдера попадают на вкладку этой модели в панели «Локальные модели»,
  если у него задан `StatsEngine` (имя дорожки без префикса `LocalEngines:`).

Порты на Video:

| Порт | Что |
|---|---|
| **:18020** | vLLM (OpenAI/Anthropic-совместимый API и `/metrics`) |
| **:18022** | прокси `thinking-strip-proxy-video` перед vLLM (срезает размышления) |
| **:18031** | gpu-arbiter (`/ready`, `/state`, аренда) |

`MetricsUrl` именованного движка — **сам vLLM** (`:18020/metrics`), а не прокси `:18022`. Формат
метрик — vLLM, поэтому `LocalEngines:{имя}:MetricsEngine = "vllm"`. Имя модели в API задаёт
`--served-model-name` на Video (прод-конфиг — у Андрея); `Model` в `LocalEngines` и id модели
в каталоге провайдера обязаны с ним совпадать.

Карту не делит: **gpu-arbiter** в режиме `exclusive` останавливает модель при любом задании
ComfyUI, аренде от CCS или чужом GPU-процессе и возвращает после тишины, предварительно выгрузив
кэш моделей ComfyUI (`/free`).

> Файлы `local-llm.service`, `local-llm.service.d/` и сопутствующие замеры A/B относятся к прежней
> дежурной модели (Gemma 4 26B-A4B на llama-server) и в этом каталоге остаются только
> как история и образец юнита; текущим продом они не являются. Юнит vLLM здесь не версионируется.

## Арбитр (порт 18031)

Правила уступки: аренда от CCS (`POST /lease {"ttl_s", "reason"}` → `{"id"}`,
`DELETE /lease/{id}`), чужой процесс на GPU (всё, кроме «своих» процессов — список `ARBITER_OWN_PROCS`, по умолчанию `ComfyUI-h3,llama-server`; для vLLM на Video имя его процесса задаётся там же), задание в
очереди ComfyUI — в режиме `exclusive` любое, в `shared` только тяжёлый граф (маркеры
`h3, minimax, video, fasth3, whisper`). Возврат — после паузы без оснований:
`ARBITER_RETURN_QUIET_AFTER_COMFY_S` (180 с) после заданий ComfyUI, `ARBITER_RETURN_QUIET_S` (10 с)
после аренды или чужого процесса; в `exclusive` перед стартом `POST /free` ComfyUI и ожидание
`ARBITER_VRAM_NEEDED_MB` (22 000) свободных, иначе состояние `waiting-vram`.

`GET /ready` — 200, когда модель отвечает (это и спрашивает CCS перед вызовом места);
`GET /state` — `{"engine": "ready|yielded|starting|waiting-vram", "reasons", счётчики, quiet_left_s, leases}`.
До 01.11.2026 поле состояния называлось `bonsai` — CCS читает и его, если `engine` нет.

## CCS

Секции — в машинном `appsettings.Local.json` (в `.gitignore`); образец —
`backend/ClaudeHomeServer/appsettings.Local.example.json` (хост `<video-host>`), устройство —
`docs/architecture/llm-providers.md`, раздел «Именованные локальные движки», статистика —
[ADR-029](../../../docs/adr/ADR-029-local-model-stats.md).

- `LocalEngines:{имя}` — `BaseUrl` (`:18020` или прокси `:18022`), `Model` (= `--served-model-name`),
  `Title`, `Actions`, `FallbackRoute`, `NumCtx`, `MetricsUrl` (`:18020/metrics`), `MetricsEngine`
  (`vllm`), `ReadyUrl` и `StateUrl` (`:18031`), при необходимости `EngineLabel`/`ModelLabel`.
- `LlmProviders:{ключ}` — `AnthropicBaseUrl`, `IsLocal`, `MaxConcurrentTurns` (часть слотов
  остаётся местам), модель с `ContextWindow`, `StatsEngine` = имя движка. **Один физический движок —
  одна дорожка:** не заводи для тех же :18020/:18022 вторую запись `LocalEngines`.
  Пока модель уступила карту, проба видит её Down, и ход чата получает «локальная модель не запущена».
