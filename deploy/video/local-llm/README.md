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

**Id модели не должен коллидировать.** Id `qwen3.8-27b` уже объявлен у `local-qwen` в базовом
`appsettings.json`, а `ResolveByModel` берёт первое совпадение — ход чата с таким id уйдёт на
основной стенд, а не на Video. Поэтому у vLLM на Video задаётся второй
`--served-model-name qwen3.8-27b-video`; этот id — в каталоге моделей провайдера
`local-video-gemma` и в `LocalEngines:gemma:Model`; у провайдера `StatsEngine=gemma`, у движка
`MetricsEngine=vllm`. Признак правильной настройки: в логе старта нет Warning о дубле id модели,
а в `turn-runs` ходы идут с `startProvider: local-video-gemma`.

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
`GET /state` — `{"engine": "ready|yielded|starting|waiting-vram|failed", "reasons", "comfy_alive", счётчики, quiet_left_s, leases}`.
До 01.11.2026 поле состояния называлось `bonsai` — CCS читает и его, если `engine` нет.

С 13.11.2026 (дефекты cd70e91e, 8bf952a5):
- работающую LLM арбитр не останавливает ради задания ComfyUI, если тот не отвечает на `/system_stats`
  (там CUDA-вызов; после TDR `/queue` жив, а `/system_stats` — 500). Проверка не чаще
  `ARBITER_COMFY_ALIVE_EVERY_S` (5 с), итог — поле `comfy_alive`;
- `systemctl stop` ждёт `ARBITER_STOP_TIMEOUT_S` (90 с), таймаут — запись в журнал, а не смерть потока;
  любая ошибка круга пишется в журнал, цикл продолжается;
- не дождались `/health` за `ARBITER_START_WAIT_S` — стоп и новая попытка; после `ARBITER_START_RETRIES` (3)
  неудач подряд — состояние `failed` на `ARBITER_START_BACKOFF_S` (600 с), потом снова.

## Сторож живости (`gpu-watchdog.timer`)

`gpu-watchdog.py` раз в минуту проверяет `comfyui-h3` (`:8188/system_stats` и строку
`CUDA error: unknown error` в журнале текущего процесса) и `hyperqwen` (`:18020/health`) и делает
`systemctl restart`, если юнит active, а CUDA в нём мёртв. Таймер, а не поток арбитра: отдельный домен
отказа (12.11.2026 умер именно поток арбитра), долгие пробы и перезапуск не тормозят арбитраж.

- Мёртв — две пробы подряд 5xx, или две без ответа при висящей или пустой очереди (занятый генерацией
  ComfyUI не трогаем), или строка в журнале. hyperqwen судим, только если в этой активации `/health`
  уже отвечал: старт ведёт арбитр (до 240–900 с).
- Не чаще раза в 5 мин; после 3 перезапусков за 30 мин — пауза и запись уровня crit, снять:
  `sudo python3 -I /home/an/local-llm/gpu-watchdog.py --clear comfyui-h3`.
- Счётчик и пауза — `/var/lib/gpu-watchdog/state.json`, журнал — `journalctl -u gpu-watchdog`.
- Установка: `gpu-watchdog.py` → `/home/an/local-llm/`, юниты → `/etc/systemd/system/`,
  `systemctl enable --now gpu-watchdog.timer`. Откат: `systemctl disable --now gpu-watchdog.timer`.
- Не ловит vLLM, который простаивает с мёртвым CUDA: его `/health` отвечает 200 (13.11.2026 так и было).
  Он падает на первом запросе, и юнит поднимает арбитр.

Для vLLM арбитру нужны две переменные окружения: `ARBITER_ENGINE_HEALTH=http://127.0.0.1:18020/health`
(дефолт в `gpu-arbiter.py:28` — прежний `:18030/health`) и `ARBITER_OWN_PROCS` с именем процесса vLLM
(иначе арбитр примет его за чужой процесс на карте и сам же уступит).

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
