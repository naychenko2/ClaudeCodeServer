# local-llm-measure — протокол замеров локальной модели

Инструменты замера для работ по цене автосжатия и прунинга контекста локальной модели
(Qwen3.8-27B на vLLM `:18020`). Лежат **не** в `tools/thinking-strip-proxy/`: прунинг — лишь
одна из мер, а мерка одна на все (порог автосжатия к прокси отношения не имеет).

| Скрипт | Что делает |
|---|---|
| `measure_turns.py` | разбор транскрипта CLI (`*.jsonl`): паузы шага p50/p90/макс, сжатия и их `preTokens/postTokens`, длительность стоп-мира, остаток после сжатия, сторож формулы порога, сверка мерки «символы ÷ 4» с токенизатором |
| `measure_speed.py` | кривая `prefill`/`decode` по ЗАНЯТОСТИ контекста (60k / 140k / 230k) — проверка гипотезы «модель тормозит на большом контексте» |

Замер прунинга по воспроизведению транскрипта — соседний
[`tools/thinking-strip-proxy/measure_prune.py`](../thinking-strip-proxy/measure_prune.py):
он про prefix cache движка, а не про ход агента.

## measure_turns.py

```bash
# один прогон исполнителя
python3 tools/local-llm-measure/measure_turns.py \
    /srv/ccs/data/claude-profiles/local-qwen/projects/<проект>/<sessionId>.jsonl

# режим замера: свой порог и сверка мерки контекста через /tokenize стенда
python3 tools/local-llm-measure/measure_turns.py --auto-compact-window 161192 \
    --tokenize --json /tmp/run-B.json <транскрипт.jsonl>
```

Что важно знать про цифры:

- **Wall-clock прогона критерием приёмки быть не может.** Сжатия занимают ≈ 10 % стены
  (замеры 22.09: 7.3 / 32 / 40 / 48 % — разброс между прогонами больше эффекта любой меры).
  Сравнивать надо суммарное время сжатий, их число и p90/макс паузы.
- **Стоп-мир** считается от последнего ответа модели до записи границы. Сверка с
  `compactMetadata.durationMs` самого CLI сходится до секунды (354.1 против 354.0; 453.5
  против 450.6) — она нужна, потому что `durationMs` живёт не во всех версиях формата.
- **Сторож формулы порога.** Порог автосжатия CLI 2.1.276 недокументирован:
  `min(окно, CLAUDE_CODE_AUTO_COMPACT_WINDOW) − min(CLAUDE_CODE_MAX_OUTPUT_TOKENS, 20000) −
  13000`. Скрипт печатает расчётный порог против фактического `preTokens` и бьёт тревогу в
  двух случаях: сжатие **раньше** расчёта или **пропущенная возможность** сжать (предпоследний
  ответ уже был выше порога). Просто «перелёт» тревогой не считается: порог проверяется между
  шагами и по копии истории CLI, поэтому `preTokens` законно выше расчёта на 25–30k (невидимый
  в `usage` вывод последнего инструмента), а однажды был выше на 161k — один жирный `Read`.
  Обновление CLI может сменить формулу молча, и тогда все меры, завязанные на порог, тихо
  перестают работать — этот сторож и есть дешёвый детектор расхождения.

## measure_speed.py

```bash
python3 tools/local-llm-measure/measure_speed.py --repeat 2 --json /tmp/speed.json \
    60000 140000 230000
```

Каждый уровень меряется двумя запросами подряд: холодный (соль в промпте) с `max_tokens=1` —
это `prefill`; тот же промпт повторно — префикс уже в prefix cache, поэтому вся длительность
сверх `ttft` есть генерация ПРИ занятом контексте, то есть `decode`.

**Стенд общий.** Соседний ход агента занижает обе цифры, поэтому скрипт ждёт тишины и всё
равно помечает прогон признаком `[ЧУЖАЯ НАГРУЗКА]`, если во время запроса движок крутил больше
одного запроса. Занятость определяется по `num_requests_running` **и** движению счётчиков
токенов сразу: счётчики токенов движок обновляет по завершении запроса, так что чужой долгий
ход по ним выглядит тишиной при 99 % занятости карт.

## Изоляция замеров и откат

Все ручки этого хозяйства — уровня **провайдера и systemd-сервиса, а не сессии**: включённый
режим действует на ВСЕ чаты `local-qwen`, а рестарт прокси рвёт ходы в полёте. Поэтому у
каждого режима заранее есть способ изолированного прогона и команда отката.

### Прокси (`PRUNE_*`, `THINKING_*`)

Живой экземпляр не трогаем — поднимаем второй рядом на своём порту:

```bash
cd tools/thinking-strip-proxy
PRUNE_TOOL_RESULTS=on PRUNE_STEP_TOKENS=40000 LISTEN_PORT=18031 LISTEN_HOST=127.0.0.1 \
    python3 proxy.py &                    # экземпляр только для замера
python3 measure_prune.py <транскрипт.jsonl> "метка" <соль> 10 18031
curl -s localhost:18031/__proxy/stats     # что он обрезал
kill %1                                   # откат: просто погасить экземпляр
```

Замер **живого** хода агента через второй экземпляр требует, чтобы туда смотрел провайдер
(`LlmProviders:local-qwen:AnthropicBaseUrl` в `appsettings.Local.json` боевого инстанса), то
есть перезапуска бэкенда — это уже не изоляция, а смена боевого режима. Откат тот же: вернуть
`http://127.0.0.1:18021` и перезапустить.

Если режим всё-таки меняется на живом прокси:

```bash
sudoedit /etc/systemd/system/thinking-strip-proxy.service.d/mode.conf   # правка Environment=
sudo systemctl daemon-reload && sudo systemctl restart thinking-strip-proxy
systemctl show thinking-strip-proxy -p Environment                      # что реально стоит
```

**Откат** — вернуть `mode.conf` к версии из репозитория и перезапустить:

```bash
sudo cp tools/thinking-strip-proxy/thinking-strip-proxy.service.d/mode.conf \
        /etc/systemd/system/thinking-strip-proxy.service.d/
sudo systemctl daemon-reload && sudo systemctl restart thinking-strip-proxy
```

Рестарт рвёт ходы в полёте — перед ним стоит убедиться, что на модели никто не работает:
`curl -s localhost:18020/metrics | grep num_requests_running`.

### Порог автосжатия и окно контекста

`CLAUDE_CODE_AUTO_COMPACT_WINDOW`, `CLAUDE_CODE_MAX_OUTPUT_TOKENS` и объявленное окно живут в
`LlmProviders:local-qwen:ExtraEnv` (`appsettings.Local.json` боевого инстанса, у dev-стенда
свой файл). Действуют на все чаты провайдера **со следующего хода**, начатые чаты не
защищены: чат, доросший до 200k, при новом пороге сожмётся на первом же ходу.

```jsonc
// appsettings.Local.json — режим замера
"LlmProviders": { "local-qwen": { "ExtraEnv": {
    "CLAUDE_CODE_AUTO_COMPACT_WINDOW": "161192"   // порог = 161192 − 8192 − 13000 ≈ 140k
} } }
```

Откат — убрать ключ из `ExtraEnv` и перезапустить бэкенд: значение с машины CLI не подхватит,
переменная стоит в `ProviderEnvKeys` и вычищается из унаследованного окружения на каждом
запуске. Границы значения зашиты в CLI: меньше 100 000 подтягивается вверх до 100 000, больше
1 000 000 — обрезается, мусор игнорируется (порог остаётся штатным).

**Изолированный прогон без правки конфига** — запустить CLI руками с тем же окружением, что
строит `BuildCliEnv`, и своим профилем:

```bash
env CLAUDE_CONFIG_DIR=/tmp/acw-test/profile \
    ANTHROPIC_BASE_URL=http://127.0.0.1:18021 \
    ANTHROPIC_AUTH_TOKEN=local-no-auth ANTHROPIC_API_KEY=local-no-auth \
    ANTHROPIC_MODEL=qwen3.8-27b ANTHROPIC_DEFAULT_OPUS_MODEL=qwen3.8-27b \
    CLAUDE_CODE_MAX_CONTEXT_TOKENS=253440 CLAUDE_CODE_MAX_OUTPUT_TOKENS=8192 \
    CLAUDE_CODE_AUTO_COMPACT_WINDOW=100000 CLAUDE_CODE_DISABLE_CLAUDE_MDS=1 \
    claude --print --output-format stream-json --verbose --model qwen3.8-27b \
           --allowedTools Bash --permission-mode acceptEdits < /tmp/acw-test/prompt.txt
python3 tools/local-llm-measure/measure_turns.py --auto-compact-window 100000 \
    /tmp/acw-test/profile/projects/*/*.jsonl
```

Свой `CLAUDE_CONFIG_DIR` обязателен: транскрипты и настройки не попадут в боевой профиль
провайдера, а удалять их потом не придётся. Откат — удалить временный профиль.

### Стенд vLLM (режим окна)

Окно стенда (`DFLASH_MAX_LEN`, режимы `CTX=fast/long/huge`) меняется только перезапуском
контейнера модели и роняет ВСЕ ходы. Для замеров его не трогаем: живое окно бэкенд берёт
пробой `/v1/models`, и все абсолютные пороги в токенах обязаны переживать его смену.
Проверить, что сейчас поднято: `curl -s localhost:18020/v1/models | jq '.data[0].max_model_len'`.
