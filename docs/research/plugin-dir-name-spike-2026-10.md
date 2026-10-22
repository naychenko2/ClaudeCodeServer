# Спайк: откуда CLI берёт имя пакета `--plugin-dir` (2026-10-18)

- **Дата:** 2026-10-18
- **Эпик:** `802bb754` (Плагины и build-it-yourself UX), rework пакета 9 (M3b)
- **ADR:** [ADR-031-plugins.md](../adr/ADR-031-plugins.md)
- **CLI:** local `2.1.301` (`/home/an/.local/bin/claude`); container `cc-sandbox` `/opt/claude-host/versions/2.1.301`
- **Метод:** живой прогон `claude --plugin-dir <dir> --print "hi" --output-format stream-json --verbose` на трёх тестовых каталогах; разбор `system/init` → `plugins[]` (поле `name`/`source`) и `slash_commands[]`.
- **Продолжение:** [plugin-dir-spike-2026-10.md](plugin-dir-spike-2026-10.md) (механизм `--plugin-dir`, allow-list).

## Вопрос

Код M3 (`PluginPackageValidator.CheckNameEqualsSlug`) сверяет `name` манифеста со slug пакета.
Но **какой** манифест читает CLI, чтобы назвать пакет? Если CLI берёт `name` из `ccs-plugin.json`,
то пакет с одним `ccs-plugin.json` (без `.claude-plugin/plugin.json`) получит имя из него.
Если же CLI берёт имя из `.claude-plugin/plugin.json` (или из имени каталога) — пакет с
`ccs-plugin.json {name: pkg}` назовут **не** `pkg`, а чем-то другим. Каталог пакета в проде —
`packages/{id}/{version}/`, т.е. имя каталога = `{version}` (например `0.1.0`) → навык `0.1.0:skill`.

## Тестовые каталоги

| # | Состав | `ccs-plugin.json` | `.claude-plugin/plugin.json` |
|---|---|---|---|
| 1 | оба манифеста, `name=pkg` | `{id: local-x/pkg, name: pkg}` | `{name: pkg}` |
| 2 | только ccs | `{id: local-x/pkg, name: pkg}` | — (нет) |
| 3 | без манифестов | — (нет) | — (нет) |
| D | дискриминация | `{id: local-x/pkg, name: AAA}` | `{name: BBB}` |

Все каталоги несут `skills/a/SKILL.md` (`user-invocable: true` → slash-команда).

## Результаты

### Local (хост, CLI 2.1.301)

| # | `plugins[].name` | `plugins[].source` | slash-команда |
|---|---|---|---|
| 1 | `pkg` | `pkg@inline` | `pkg:a` |
| 2 | **`pkg-ccs-only`** (имя каталога) | `pkg-ccs-only@inline` | `pkg-ccs-only:a` |
| 3 | **`pkg-none`** (имя каталога) | `pkg-none@inline` | `pkg-none:a` |
| D | **`BBB`** (из `.claude-plugin/plugin.json`) | `BBB@inline` | `BBB:a` |

### Container (`cc-sandbox`, CLI 2.1.301)

Сценарий 2 (только `ccs-plugin.json {name: pkg}`, каталог `/projects/m3b-ccs-only`):
`plugins[]` → `{"name":"m3b-ccs-only","path":"/projects/m3b-ccs-only","source":"m3b-ccs-only@inline"}`;
slash-команда `m3b-ccs-only:a`. **То же поведение, что local: имя = имя каталога.**

### Дискриминация (сценарий D)

`ccs-plugin.json {name: AAA}` + `.claude-plugin/plugin.json {name: BBB}` → CLI отчитал **`BBB`**.
Т.е. при наличии нативного манифеста CLI берёт `name` именно из `.claude-plugin/plugin.json`,
а `name` из `ccs-plugin.json` игнорирует.

## Вердикт

**CLI берёт имя пакета ТОЛЬКО из `.claude-plugin/plugin.json`.**

- Если `.claude-plugin/plugin.json` **есть** → `name` = его поле `name` (сценарии 1, D).
- Если `.claude-plugin/plugin.json` **нет** → `name` = **имя каталога** `--plugin-dir` (сценарии 2, 3).
- Поле `name` в `ccs-plugin.json` CLI **не читает** (сценарии 2, D).

В проде (`packages/{id}/{version}/`) это значит: пакет с **одним** `ccs-plugin.json` (без
`.claude-plugin/plugin.json`) CLI назовёт **`{version}`** (например `0.1.0`) → навык
`0.1.0:skill`, а **не** `pkg:skill`. Подозрение из постановки подтверждено.

## Выводы для фикса M3

1. **M3a (проверка `name` во всех манифестах) — необходим, но не достаточен.** Он ловит
   расхождение `name` между `ccs-plugin.json` и `.claude-plugin/plugin.json` (тест
   `M3a_РасхождениеManifestов`). Но пакет с **одним** `ccs-plugin.json` (name = slug, корректный)
   проверку M3a **проходит**, а CLI всё равно назовёт его именем каталога → привязка ломается.
2. **Главный риск** — пакет без `.claude-plugin/plugin.json`. Для MVP это либо:
   - (а) требовать `.claude-plugin/plugin.json` (или `plugin.json`) в валидаторе, если пакет
     несёт навык/команду, которые адресуются по slug; либо
   - (б) задокументировать, что `ccs-plugin.json` — служебный (id/version/permissions), а
     «публичное» имя пакета — только из нативного манифеста.
   Решение — открытое (см. ниже), не блокирует M3a.

## Открытые вопросы

- **Q1.** Требуется ли `.claude-plugin/plugin.json` (или `plugin.json`) для валидности пакета,
  несущего skill/команду? Сейчас нет — валидатор пропускает пакет с одним `ccs-plugin.json`.
  Если да — это новое правило валидатора (не M3a).
- **Q2.** Если пакет адресует навык по slug, а CLI назовёт его `{version}` — нужно ли
  нормализовать slug → имя каталога (например, класть пакет в каталог `{slug}` вместо `{version}`)?
  Это меняет раскладку `packages/{id}/{version}/`.
