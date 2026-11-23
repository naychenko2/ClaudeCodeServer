# Панель «Локальные модели»: живая идентификация модели вместо статичных подписей

Дата: 2026-11-23. Запрос Андрея: «новая локальная модель — пусть панель показывает данные этой
модели, если она поднята вместо Страта, и правильно показывает название модели и все остальные
параметры её работы».

## Что сегодня в панели и откуда

Панель «Локальные модели» → вкладка «Текст» → `GET /api/local-model/stats?engine=main|<движок>`
(`backend/ClaudeHomeServer/Controllers/LocalModelStatsController.cs`).

Идентичность движка в ответе — **целиком из статичного конфига**, живой машины там нет:

| Поле DTO | Основной стенд (`main`) | Именованный движок |
|---|---|---|
| `model` | `LlmProviders:local-qwen:Stats:Model`, иначе id из каталога провайдера | `LocalEngines:<имя>:ModelLabel`, иначе id из каталога |
| `engineLabel` | `Stats:EngineLabel` | `LocalEngines:<имя>:EngineLabel` |
| `topology` | `Stats:Topology` | — (null) |
| `tensorParallel` | `Stats:TensorParallel` | — |
| `engine` | формат метрик (`vllm`/`llama-server`) из `Stats:Engine` | `LocalEngineSpec.MetricsEngine` |
| заголовок вкладки | `Stats:Title` | `LocalEngines:<имя>:Title` |
| `status`, скорости, KV, GPU, история | **живые** — из `/metrics` поллера (`LocalModelStatsPoller`) | то же |

Живое окно модели уже умеет читаться: `LocalEndpointProbe` (`backend/ClaudeHomeServer.Llm/LocalEndpointProbe.cs`)
берёт `max_model_len` из `GET {base}/v1/models` и `engine.max_context` из `/metrics` — но это
внутренний кэш для CLI (`TryGetKnownContextWindow`), в панель он **не отдаётся**. Имя модели,
которое стенд реально отдаёт (`served-model-name`), в панель не попадает вообще.

## Корень проблемы

Подписи в панели — это «заявленное», а не «действительное». Смена модели на стенде (Страта →
прямой vLLM или другая модель) не ломает панель: она продолжает показывать `Strata 0.2.7`,
`Qwen3.8-Flash-Next IQ3_XXS`, `разрез слоёв 26/22`, пока кто-то руками не поправит конфиг.
Расхождение «конфиг ↔ живая машина» не видно нигде.

Сейчас на бою (`/opt/ccs/app/appsettings.Local.json`):
- `local-qwen`: `Stats.MetricsUrl = http://192.168.7.83:18025/metrics` (адаптер Страты, формат vllm),
  `EngineLabel = "Strata 0.2.7"`, `Model = "Qwen3.8-Flash-Next IQ3_XXS"`, `Topology = "разрез слоёв 26/22"`.
- `LocalEngines:gemma` (узел Video): `BaseUrl = http://192.168.7.208:18020`, `Model = qwen3.8-27b-video`,
  `MetricsUrl = .../18020/metrics`, `Title = "Qwen 27B (пилот)"`; `ModelLabel`/`EngineLabel` не заданы.

## Решение (два слоя)

### 1. Живой слой в коде (панель перестаёт врать сама)

- Новая проба идентичности движка: `GET {BaseUrl}/v1/models` → `id` (served-model-name) и
  `max_model_len`; для Страты/адаптера — `engine.max_context` из `/metrics` (уже умеем читать).
  Опрос — раз в N секунд на каждую дорожку (`main` + каждый `LocalEngines:*` с `MetricsUrl`),
  не чаще поллера; кэш — как у `LocalEndpointProbe` (5 с), но для панели свой, медленный.
- Новые поля DTO `LocalModelStatsResponse` (nullable, `WhenWritingNull` — старый ответ побайтно прежний):
  - `modelLive` — что стенд отдаёт как id;
  - `contextWindowLive` — живое окно (из `max_model_len`/`engine.max_context`);
  - `engineLive` — версия/формат движка, если движок её отдаёт;
  - `drift` — расхождение конфига и живого (модель/окно/подпись): фронт показывает пометку
    «конфиг устарел», а не молчаливую подмену.
- Источник проб для именованных движков: `LocalEngineSpec.BaseUrl` (у `gemma` это
  `http://192.168.7.208:18020`), а не `AnthropicBaseUrl` провайдера — сейчас проба живёт только
  по провайдерам, поэтому у второй дорожки живого имени модели нет в принципе.
- Фронт (`frontend/src/features/localModel/**`): карточка движка показывает **живое** имя и окно,
  рядом — заявленные из конфига, если расходятся. Новые поля опциональны ⇒ старый бэкенд рисуется как раньше.

### 2. Операционный слой (подписи под новую модель)

Когда модель на стенде сменилась, в боевом конфиге меняются: `Stats.MetricsUrl` (адрес нового
движка), `Stats.Engine`/`EngineLabel` (новый движок вместо Страты), `Stats.Model` (новая модель
и квант), `Stats.Topology` (новый разрез/TP), `Stats.Title` (короткое имя для вкладки) — и, если
это отдельный именованный движок, `LocalEngines:<имя>` (BaseUrl/Model/MetricsUrl/Title/ModelLabel).
Правка — в `appsettings.Local.json` на бою (не в отслеживаемом `appsettings.json`), с рестартом `ccs.service`.
Пункт 1 делает так, что забытая правка конфига больше не приводит к тихой лжи в панели.

## Что нужно от человека/с фактов

1. Точное имя модели и её параметры (что поднято вместо Страта: `served-model-name`, окно, TP,
   квант, порт метрик). В этом ходе сеть и команды недоступны (см. «Ограничения хода»).
2. Решение по формату: показывать живое рядом с заявленным (вариант «драфт») или только живое.

## Ограничения хода (2026-11-23)

В этом ходе недоступны Bash, субагенты и MCP-трекер (сработала блокировка «auto mode unavailable:
its safety check does not run for this session»). Поэтому: сеть/стенд не опрошены, задачи в трекере
не заведены, сборка и тесты не гонялись. Разбор сделан чтением кода и боевого конфига.

## Замер живого Inkling на ai (2026-11-23, шаг S1)

Стартовый адрес из конфига `http://192.168.7.83:18025` (адаптер Страты, `socat` → `127.0.0.1:18025`, `strata-metrics.py`):

- `GET /metrics` → **HTTP 503**, `Content-Type: text/plain; version=0.0.4`, `Server: BaseHTTP/0.6 Python/3.14.4`, тело `# strata unreachable: <urlopen error [Errno 111] Connection refused>` (источник 18020 не отвечает).
- `GET /v1/models` → **HTTP 404**, `Server: BaseHTTP/0.6`.

Это НЕ живой Inkling: `:18025` обслуживает адаптер Страты, и тот падает. Согласно постановке — не доверяем сырому 200, сверяем с активной конфигурацией на машине.

### Активная конфигурация на ai (read-only SSH, `ssh ai`)

`ss -ltnp`: порт **8000** (`0.0.0.0:8000`) — активный движок; `:18025` — `socat` (pid 3839) → `127.0.0.1:18025`, python3 (pid 7284) `/home/an/llm/strata-night/prod/strata-metrics.py` (адаптер, не Inkling).

Процесс: `/usr/bin/python3 /usr/local/bin/vllm serve --model thinkingmachines/Inkling-2-Mini --served-model-name inkling-2-mini --tensor-parallel-size 2 --max-model-len 256000 --max-num-batched-tokens 2048 --max-num-seqs 8 --gpu-memory-utilization 0.92 --reasoning-parser inkling --tool-call-parser inkling --enable-auto-tool-choice --speculative-config {"method":"dspark","num_speculative_tokens":7,"draft_sample_method":"probabilistic","num_speculative_tokens_per_batch_size":[[1,1,7],[2,16,3]]}` (pid 993047, `VLLM::EngineCore` pid 993554, `VLLM::Worker_TP0`/`TP1` pid 993662/993663).

### Конечный адрес замера: `http://192.168.7.83:8000`

- `GET /v1/models` → **HTTP 200**, `Content-Type: application/json`, `server: uvicorn`. Тело: `id = "inkling-2-mini"`, `root = "thinkingmachines/Inkling-2-Mini"`, `object = "model"`, `owned_by = "vllm"`, `created = 1795389700/1795389712`, `max_model_len = 256000`.
- `GET /metrics` → **HTTP 200**, `Content-Type: text/plain; version=1.0.0; charset=utf-8`, `server: uvicorn`, ~74 КБ, 801 строка.
- `engine.max_context` в /metrics НЕ отдаётся (нет такого ключа; метрика `vllm:cache_config_info` с `kv_cache_size_tokens="256848"`, `num_gpu_blocks="7568"`, `block_size="8"`, `gpu_memory_utilization="0.92"`, `enable_prefix_caching="True"`, `kv_offloading_backend="native"`, `_extensible_kv_cache_resolved="True"`).

### Таблица 17 "AllMetrics" (backend/ClaudeHomeServer.Llm/LocalStats/VllmMetricsSource.cs:36-56)

| Метрика | Статус |
|---|---|
| vllm:num_requests_running | есть — 0.0 |
| vllm:num_requests_waiting | есть — 0.0 |
| vllm:kv_cache_usage_perc | есть — 0.0 |
| vllm:kv_offload_cpu_cache_usage_perc | нет |
| vllm:prefix_cache_hits_total | есть — 1.443136e+07 |
| vllm:prefix_cache_queries_total | есть — 1.4833014e+07 |
| vllm:num_preemptions_total | есть — 0.0 |
| vllm:generation_tokens_total | есть — 56178.0 |
| vllm:prompt_tokens_total | есть — 1.4833014e+07 |
| vllm:request_decode_time_seconds_sum | есть — 254.96143503198982 |
| vllm:request_prefill_time_seconds_sum | есть — 62.18167002101836 |
| vllm:request_prefill_kv_computed_tokens_sum | есть — 401654.0 |
| vllm:kv_offload_tiering_chunk_hits_total | нет |
| vllm:kv_offload_tiering_chunk_queries_total | нет |
| vllm:inter_token_latency_seconds_sum | есть — 254.96143503198982 |
| vllm:inter_token_latency_seconds_count | есть — 15661.0 |
| vllm:request_queue_time_seconds_sum | есть — 1.3537959870009217 |

### Дополнительные метрики Inkling (покрывают существующие блоки панели)

Наблюдаются и покрывают блоки панели: `vllm:num_requests_waiting_by_reason` (capacity/deferred),
`vllm:request_queue_time_seconds_count`, `vllm:time_to_first_token_seconds_*` (bucket/count/sum),
`vllm:e2e_request_latency_seconds_*`, `vllm:request_inference_time_seconds_*`,
`vllm:request_prompt_tokens_*`, `vllm:request_generation_tokens_*`,
`vllm:request_time_per_output_token_seconds_*`, `vllm:iteration_tokens_total_*`,
`vllm:spec_decode_num_accepted_tokens_total` и `_per_pos_total`, `vllm:spec_decode_num_drafts_total`,
`vllm:spec_decode_num_verified_tokens_total`, `vllm:prompt_tokens_by_source_*`,
`vllm:prompt_tokens_cached_*`, `vllm:mm_cache_hits_total`, `vllm:mm_cache_queries_total`,
`vllm:external_prefix_cache_hits_total`, `vllm:external_prefix_cache_queries_total`,
`vllm:admission_rejections_total`, `vllm:request_success_total`,
`vllm:tool_call_parser_invocations_total`, `vllm:engine_sleep_state` (awake=1.0),
`vllm:cache_config_info` (kv_cache_size_tokens=256848, num_gpu_blocks=7568, block_size=8, prefix_caching=True).

**Вывод: формат тот же — `vllm:*`, но НЕ адаптер Страты**. Никакого специального маппинга имён
для 17 AllMetrics не требуется (14 из 17 есть); отсутствуют только RAM/tiering метрики
(`kv_offload_cpu_cache_usage_perc`, `kv_offload_tiering_chunk_*` и попутно `ram_tier_hits`) —
это Strata-специфика, у чистого vLLM не отдаётся; панель их даст как null/`Unsupported` (как ожидалось).
Семантики/единицы не выдумываем: всё отдаётся в unix-секундах (`*_seconds_*`), токенах и 0..1-долях.
Версии движка в ответе нет (нет `vllm:version`-подобной метрики); версия косвенно — по тегам
`_created` (created=1795389700), и по факту `vllm serve` с `--reasoning-parser inkling --tool-call-parser inkling`
и speculative-config `dspark`.

### Sanitised fixtures

- `backend/ClaudeHomeServer.LocalStats.Tests/Fixtures/LocalStats/inkling-metrics.txt` (801 строка, 74 КБ)
- `backend/ClaudeHomeServer.LocalStats.Tests/Fixtures/LocalStats/inkling-models.json` (`jq empty` OK)

Секретов/пользовательских данных не содержится; `modelperm-*`-id сгенерированы движком, удалять нечего.
