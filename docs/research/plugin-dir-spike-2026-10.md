# Спайк: `--plugin-dir` в CLI 2.1.301 (local + container)

- **Дата:** 2026-10-17
- **Эпик:** `802bb754` (Плагины и build-it-yourself UX), шаг `0.spike`
- **ADR:** [ADR-031-plugins.md](../adr/ADR-031-plugins.md)
- **CLI:** хост `2.1.301` (`/home/an/.local/bin/claude`); контейнер дефолт `2.1.289` + host-версия `2.1.301` (`/opt/claude-host/versions/2.1.301`)
- **Метод:** живой прогон `claude --plugin-dir <dir> --print ... --output-format stream-json --verbose` с тестовым каталогом-плагин; разбор `system/init` (список `plugins`/`slash_commands`/`skills`/`agents`/`mcp_servers`) и proof-файлов для `hooks/`/`bin/`.

## Тестовый плагин

Каталог в нативном формате CLI-плагина (`.claude-plugin/plugin.json` + компоненты):

```
ccs-spike-plugin/
  .claude-plugin/plugin.json   # name=spike-test
  skills/my-command/SKILL.md   # user-invocable: true  → команда
  skills/plain-skill/SKILL.md  # user-invocable: false → agent-only skill
  hooks/hooks.json             # SessionStart → command: echo ... >> proof.txt
  .mcp.json                    # stdio MCP-сервер (npx @modelcontextprotocol/server-everything)
  bin/spike-tool.sh            # исполняемый скрипт
  settings.json                # {"model":"haiku"}
```

Второй плагин (`spike2`) добавлял `agents/my-agent.md` для проверки `agents/`.

---

## 1. Local (хост, CLI 2.1.301)

### `system/init` с установленным skill и командой

`claude --plugin-dir /tmp/ccs-spike-plugin --print "say hi" --output-format stream-json --verbose`:

- `plugins[]` → `{"name":"spike-test","path":"/tmp/ccs-spike-plugin","source":"spike-test@inline","version":"0.1.0"}` — **плагин загружен**, `source` = `<name>@inline` (метка «из `--plugin-dir`», не из marketplaces/skills-dir).
- `slash_commands[]` → **`spike-test:my-command`** (именуется `plugin:skill`).
- `skills[]` → **`spike-test:my-command`** (та же запись дублируется в `skills`).
- `skills[]` → **`spike-test:plain-skill` НЕ в списке** (см. ниже).
- `mcp_servers[]` → `{"name":"plugin:spike-test:spike-mcp","status":"pending","source":"plugin"}` — **`.mcp.json` зарегистрировал сервер**.

### Вызов skill и команды из хода

- **Команда** (`user-invocable: true`): прогон `claude --plugin-dir ... --print "/spike-test:my-command hello-spike"` → ответ `COMMAND-ACTIVE` + эхо аргументов `hello-spike`. **Команда вызывается как slash `/plugin:skill` и получает `$ARGUMENTS`.**
- **Skill** (`user-invocable: false`): прогон с промптом «use the plain-skill skill» → агент сам вызвал инструмент `Skill` с `{"skill":"spike-test:plain-skill"}` → ответ `PLAIN-SKILL-ACTIVE`. **Agent-only skill НЕ в `slash_commands`, но доступен агенту через `Skill`-тул** (загружается в системный промпт, а не в init-список).

### `hooks/` — исполняются (RCE)

`hooks/hooks.json` с `SessionStart → command: echo SPIKE-HOOK-RAN >> /tmp/ccs-spike-hook-proof.txt`. В выводе — события `system/hook_started` + `system/hook_response` (`hook_name: SessionStart:startup`, `exit_code: 0`), proof-файл **создан** (`SPIKE-HOOK-RAN`). **Хук плагина исполняет произвольную shell-команду при старте сессии.**

### Прочие компоненты

- **`agents/`** (плагин `spike2`): `agents[]` в init получил **`spike2:my-agent`** — плагин добавляет сабагента (со своими `tools`/`model`) → ещё один вектор исполнения.
- **`bin/`**: `bin/spike-tool.sh` **не исполнен** автоматически (proof-файла нет) — это просто каталог скриптов; исполняется только если skill/команда/hook на него ссылаются.
- **`settings.json`**: `{"model":"haiku"}` **не переопределил** модель (init показал модель окружения) — область действия plugin-`settings.json` узкая, не глобальный override.

**Итог local:** `--plugin-dir` работает; команда и skill видны и вызываются; `hooks/`/`.mcp.json`/`agents/` исполняются/регистрируются (RCE-векторы); `bin/`/`settings.json` — низкий риск.

---

## 2. Container (docker-песочница)

Контейнер `cc-sandbox` (общий sandbox, процессы через `docker exec`). Маунты: `…/sandbox-fake → /projects`, `…/data/sandbox-profiles → /sandbox-profiles`, `…/data/sandbox-tmp → /turn-tmp`, `…/SystemPrompts → /app/SystemPrompts`, `~/.local/share/claude/versions → /opt/claude-host/versions`, `…/mcp → /app/mcp`.

### Прогон

Копия плагина положена в хост-путь, маппящийся на `/projects` (`/tmp/vera-lora-qa/sandbox-fake/spike-plugin` → `/projects/spike-plugin` в контейнере).

`docker exec cc-sandbox claude --plugin-dir /projects/spike-plugin --print "hi" ...` (дефолтный CLI **2.1.289**) и отдельно `/opt/claude-host/versions/2.1.301`:

- **Обе версии** (`2.1.289` и `2.1.301`): `plugins[]` → `{"name":"spike-test","path":"/projects/spike-plugin","source":"spike-test@inline"}` — **плагин загружен, путь — контейнерный**.
- `slash_commands[]`/`skills[]` → `spike-test:my-command`; `mcp_servers[]` → `plugin:spike-test:spike-mcp` (pending).
- **`hooks/` исполняются и в песочнице**: `hook_response` `exit_code: 0`, proof-файл **создан внутри контейнера** (`docker exec … cat /tmp/ccs-spike-hook-proof.txt` → `SPIKE-HOOK-RAN`). **RCE-дыра действует и в container.**

### `docker exec` про каталог

`docker exec cc-sandbox ls -la /projects/spike-plugin/` → все файлы на месте (`.claude-plugin/`, `.mcp.json`, `bin/`, `hooks/`, `settings.json`, `skills/{my-command,plain-skill}`). Каталог **видим** через существующий bind-mount `/projects`.

### Нужен ли bind-mount

**Для существующих маунтов — нет:** плагин под `/projects` (или `/sandbox-profiles`, `/turn-tmp`) виден и `--plugin-dir` на контейнерный путь работает.

**Для реального места пакетов — ДА, нужен новый bind-mount.** ADR-031 кладёт пакеты в `data/plugins/{owner}/`. Каталог `data/plugins` **среди маунтов контейнера отсутствует** (проверено `docker inspect`: маунты — только `/projects`, `/sandbox-profiles`, `/turn-tmp`, `/app/SystemPrompts`, `/opt/claude-host/versions`, `/app/mcp`). Значит, чтобы container-владелец видел свои пакеты, `data/plugins` (или конкретный `data/plugins/{owner}`) надо **до-маунтить** и добавить правило в `DockerPathMapper`.

---

## 3. Вердикт

**Порог пройден.** Оба пункта (`--plugin-dir` + skill/команда) работают в local и container; поведение `hooks/`/`bin/`/`.mcp.json`/`agents/`/`settings.json` задокументировано. Ни одно из «стоп»-условий не сработало:

- skill/команда **видны** (в обеих средах);
- `hooks/` исполняются, но **отключаются** на этапе валидатора (allow-list) — не «без возможности отключить»;
- container требует bind-mount, но он **описуем в `DockerPathMapper`** (одно правило `Add` + один `-v`), не «не описать».

### Список запрещённых файлов пакета (факт, для валидатора 1.3)

| Путь | Поведение (проверено) | Риск | В allow-list? |
|---|---|---|---|
| `.claude-plugin/plugin.json` | манифест | — | **да** |
| `ccs-plugin.json` | наш манифест | — | **да** |
| `skills/**/SKILL.md` (+ вспомог. `.md`) | skill/команда (text для модели) | prompt-инъекция (допустимо) | **да** |
| `commands/*.md` | slash-команда (legacy-формат, грузится идентично skill) | prompt-инъекция (допустимо) | **да** |
| `panel/**` | статика панели (iframe) | — | **да** |
| `hooks/` (`hooks.json` + хендлеры) | **исполняют shell** (SessionStart) | **RCE** | **нет** |
| `.mcp.json` | **регистрирует MCP-сервер** (stdio/http) | **RCE / обход MCP-allow-list** | **нет** |
| `agents/*.md` | **добавляет сабагента** (свои tools/model) | **RCE** | **нет** |
| `bin/**` | скрипты, **не auto-run** (только по ссылке) | низкий (код) | **нет** (консервативно) |
| `settings.json` | узкая область (не глобальный override) | низкий-средний | **нет** (консервативно) |

Правило валидатора: **allow-list** = `plugin.json`, `ccs-plugin.json`, `skills/**/SKILL.md` + вспомогательные `.md`, `commands/*.md`, `panel/**`; всё остальное (прежде всего `hooks/`, `.mcp.json`, `agents/`, `bin/`, `settings.json`) — отказ. Плюс: без symlink, без выхода за корень, лимит размера/числа файлов.

### Правило для `DockerPathMapper`

Чтобы container-владельцы видели свои пакеты, добавить **одну** пару (инвариант ADR-031 §5.5 — mount ↔ mapper правится одной правкой):

- `SandboxManager.BuildRunArgs`: `-v {dataPluginsHost}:/plugins` (read-only, `dataPluginsHost = {dataDir}/plugins`);
- `DockerPathMapper` конструктор: `Add(Path.Combine(dataDir, "plugins"), "/plugins")`.

Тогда `--plugin-dir` в container получает контейнерный путь `/plugins/{owner}/packages/{id}/{version}`, а `ToRuntime`/`ToHost` round-trip стабильны. Альтернатива без нового маунта — класть пакеты под уже смонтированный `sandbox-profiles`, но ADR-031 §2.3/§5.3 этого не хочет (пакеты ≠ профили CLI).

### Замечания (не блокеры)

- `source` плагина из `--plugin-dir` = `<name>@inline` — для UI «два вида плагинов» (ADR-031 §7) это готовая метка отличия от `@marketplace`/`@skills-dir`.
- `settings.json` плагина не даёт глобального override модели — если в будущем нужен plugin-level `model`, это отдельный вопрос, не спайка.
- Дефолтный CLI контейнера `2.1.289` (host-версии до `2.1.301`); `--plugin-dir` работает в обеих — дрейф версии не ломает механизм, но для строгости MVP зафиксировать `UseHostCli` на `2.1.301`.
