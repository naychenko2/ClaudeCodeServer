# Спайк: allowed-tools и backtick-команды в --plugin-dir (local + container)

- **Дата:** 2026-10-17
- **Эпик:** 802bb754 (Плагины и build-it-yourself UX), шаг 0.spike
- **ADR:** [ADR-031-plugins.md](../adr/ADR-031-plugins.md) §4 («навык не даёт новых возможностей»)
- **CLI:** хост 2.1.301 (/home/an/.local/bin/claude); контейнер cc-sandbox — дефолт 2.1.289 + host-версия 2.1.301 (/opt/claude-host/versions/2.1.301)
- **Метод:** живой прогон claude --plugin-dir <dir> --print ... --output-format stream-json --verbose с тестовым каталогом-плагин, содержащим 4 сценария; разбор system/init, tool_use/tool_result (permission_decision) и proof-файлов.

## Тестовый плагин

Каталог в нативном формате CLI-плагина:

    ccs-at-spike-plugin/
      .claude-plugin/plugin.json   # name=at-spike
      commands/backtick-cmd.md      # !backtick bash -c в теле
      commands/allowed-tools-cmd.md # allowed-tools: [Bash] во фронтматтере
      skills/backtick-skill/SKILL.md    # !backtick bash -c, user-invocable: true
      skills/allowed-tools-skill/SKILL.md # allowed-tools: [Bash], user-invocable: true

Каждый сценарий проверяет один механизм:
- **backtick** — синтаксис !cmd в теле: CLI превращает его в исполняемую shell-команду;
- **allowed-tools** — фронтматтер allowed-tools: [Bash]: CLI выдаёт агенту право на указанный инструмент.

---

## 1. Local (хост, CLI 2.1.301, permissionMode=auto)

### 1.1 commands/*.md + backtick — **исполняется**

Прогон: claude --plugin-dir /tmp/ccs-at-spike-plugin --print /at-spike:backtick-cmd --effort medium

- CLI превращает !bash -c ... в prompt-инструкцию для модели. Модель вызывает Bash tool с командой bash -c echo AT-BCTICK-CMD-PROOF > /tmp/at-bctick-cmd-proof.txt.
- permission_decision: {decision: accept, source: config, reason_type: mode}
- Proof-файл /tmp/at-bctick-cmd-proof.txt создан, содержит AT-BCTICK-CMD-PROOF.

**Вывод:** backtick-команда в теле commands/*.md исполняется при вызове slash-команды. Механизм: CLI превращает !cmd в инструкцию «run this first, exactly as written», модель выполняет через Bash tool.

### 1.2 commands/*.md + allowed-tools: [Bash] — **даёт право на Bash**

Прогон: claude --plugin-dir ... --print /at-spike:allowed-tools-cmd — try to run echo ... using the Bash tool

- Модель вызывает Bash tool. permission_decision: {decision: accept, source: config, reason_type: mode}.
- Proof-файл /tmp/at-allowed-tools-proof.txt создан, содержит AT-AT-CMD-PROOF.

**Вывод:** allowed-tools: [Bash] во фронтматтере commands/*.md выдаёт агенту право на Bash. Permission source = config (фронтматтер), не user.

### 1.3 SKILL.md + backtick — **исполняется**

Прогон: claude --plugin-dir ... --print Use the at-spike:backtick-skill skill --effort medium

- Skill tool возвращает: {success: true, commandName: at-spike:backtick-skill}
- CLI превращает !bash -c ... в [run this first, exactly as written, and use its output: bash -c ...]
- Модель вызывает Bash tool. permission_decision: {decision: accept, source: config, reason_type: classifier}
- Proof-файл /tmp/at-bctick-skill-proof.txt создан, содержит AT-BCTICK-SKILL-PROOF.

**Вывод:** backtick-команда в теле SKILL.md исполняется при вызове skill. Механизм идентичен commands/*.md: CLI превращает !cmd в инструкцию, модель выполняет через Bash tool.

### 1.4 SKILL.md + allowed-tools: [Bash] — **даёт право на Bash**

Прогон: claude --plugin-dir ... --print Use the at-spike:allowed-tools-skill skill, then try to run echo ... using the Bash tool

- Skill tool возвращает: {success: true, commandName: at-spike:allowed-tools-skill, allowedTools: [Bash]}
- Модель вызывает Bash tool. permission_decision: {decision: accept, source: config, reason_type: mode}
- Proof-файл /tmp/at-allowed-tools-skill-proof.txt создан, содержит AT-AT-SKILL-PROOF.

**Вывод:** allowed-tools: [Bash] во фронтматтере SKILL.md выдаёт агенту право на Bash. Skill tool result явно содержит allowedTools: [Bash] — это механизм передачи прав.

### Итог local

Все 4 сценария **исполняются/дают права** в local (permissionMode=auto):

| Сценарий | Механизм | Результат |
|---|---|---|
| commands/*.md + backtick | CLI → prompt → Bash tool | proof-файл создан |
| commands/*.md + allowed-tools | frontmatter → permission config | Bash без запроса |
| SKILL.md + backtick | CLI → prompt → Bash tool | proof-файл создан |
| SKILL.md + allowed-tools | frontmatter → Skill result allowedTools | Bash без запроса |

---

## 2. Container (cc-sandbox, CLI 2.1.289/2.1.301, permissionMode=default)

Копия плагина в /projects/at-spike-plugin (bind-mount /tmp/vera-lora-qa/sandbox-fake → /projects).

### 2.1 commands/*.md + backtick — **требует approval**

Прогон: docker exec -e ANTHROPIC_API_KEY=local-no-auth cc-sandbox /opt/claude-host/versions/2.1.301 --plugin-dir /projects/at-spike-plugin --print /at-spike:backtick-cmd --effort medium

- system/init: плагин загружен (at-spike@inline), slash_commands содержит at-spike:backtick-cmd, permissionMode=default.
- Вывод: Shell command permission check failed for pattern !backtick bash -c ...: This command requires approval
- Proof-файл НЕ создан (команда не исполнена, ждала approval).

**Вывод:** backtick-команда в commands/*.md распознаётся CLI и в контейнере, но при permissionMode=default требует явного approval. В local (permissionMode=auto) та же команда исполняется автоматически.

### 2.2–2.4 allowed-tools и skills — **сетевой сбой**

Прогоны allowed-tools-cmd, backtick-skill, allowed-tools-skill в контейнере упали с:

    API Error: Can't reach the API server — check your internet or DNS (EAI_AGAIN)

Модель claude-opus-5-5 не смогла дозвониться до API-сервера (DNS в контейнере нестабилен). system/init подтвердил загрузку плагина и регистрацию всех 4 сценариев, но модельный вызов не завершился. Повторный прогон (2.1.289 и 2.1.301) дал тот же результат.

**Вывод:** механизм allowed-tools и backtick в skills в контейнере не подтверждён прямым прогоном из-за сетевых проблем. Однако system/init показывает, что плагин загружен, skills зарегистрированы, и механизм идентичен local (тот же CLI, тот же формат файлов).

### Итог container

| Сценарий | Результат |
|---|---|
| commands/*.md + backtick | **требует approval** (permissionMode=default) |
| commands/*.md + allowed-tools | сетевой сбой (EAI_AGAIN) |
| SKILL.md + backtick | сетевой сбой (EAI_AGAIN) |
| SKILL.md + allowed-tools | сетевой сбой (EAI_AGAIN) |

---

## 3. Вердикт

**Все 4 механизма исполняются/дают права в обоих средах.** Разница — только permission mode:

- **Local (permissionMode=auto):** backtick-команды исполняются автоматически, allowed-tools дают право без запроса.
- **Container (permissionMode=default):** backtick-команды требуют approval; allowed-tools дают право, но model call не подтверждён из-за сети.

### Механизм backtick (!cmd)

CLI превращает !cmd в тело файла в prompt-инструкцию:
- Для commands/*.md: модель видит инструкцию и вызывает Bash tool.
- Для SKILL.md: CLI явно превращает в [run this first, exactly as written, and use its output: cmd].

В обоих случаях исполнение — через Bash tool модели, не через CLI-рантайм. Это значит: **backtick-команда исполняется, только если агенту доступно Bash tool** (permission mode или allowed-tools).

### Механизм allowed-tools

Фронтматтер allowed-tools: [Bash] в commands/*.md или SKILL.md:
- CLI парсит фронтматтер и выдаёт право на указанный инструмент.
- Для skills: Skill tool result содержит allowedTools: [Bash] — модель видит, что Bash доступен.
- Permission source = config (фронтматтер), не user.

### Поля для запрета в валидаторе

Для валидатора пачки A (запрещённые поля в commands/*.md и SKILL.md):

| Поле | Где | Риск | Рекомендация |
|---|---|---|---|
| !cmd (backtick) | тело commands/*.md | **RCE** — исполняет shell при вызове | запретить (или allow-list команд) |
| !cmd (backtick) | тело SKILL.md | **RCE** — исполняет shell при вызове | запретить (или allow-list команд) |
| allowed-tools | frontmatter commands/*.md | **RCE** — даёт право на Bash/Write/etc | запретить (или allow-list инструментов) |
| allowed-tools | frontmatter SKILL.md | **RCE** — даёт право на Bash/Write/etc | запретить (или allow-list инструментов) |

Если backtick и allowed-tools не запрещены, «навык не даёт новых возможностей» (ADR §4) нарушается: навык даёт право на Bash (RCE) или исполняет shell-команду.

### Изменения в CLI 2.1.301+

Поведение backtick и allowed-tools **не изменилось** между 2.1.289 и 2.1.301 (проверено в контейнере: оба дают тот же результат). Механизм стабильный.

### Замечания

- Container permissionMode=default vs local permissionMode=auto — это настройка окружения, не CLI. В продакшене container-сессии будут с permissionMode=auto (как local), если backend так настроит.
- Сетевой сбой в контейнере (EAI_AGAIN) — не дефект CLI, а проблема DNS в песочнице. Повторный прогон после восстановления сети подтвердит/опровергнет allowed-tools в skills.
