# Документация проекта

Карта корпуса: что лежит в каждом разделе и куда класть новое. Вход в проект —
[README.md](../README.md) (что это и как запустить) и [CLAUDE.md](../CLAUDE.md)
(карта кода, инварианты, соглашения); отсюда начинаются детали.

Разделы отвечают на разные вопросы, поэтому папка выбирается по **роли документа**,
а не по теме: одна и та же подсистема может иметь и справочник в `architecture/`,
и инструкцию по эксплуатации в `operations/`.

## Разделы

| Раздел | Отвечает на вопрос | Что внутри |
|---|---|---|
| [architecture/](architecture/) | как устроено | справочники подсистем — обязательное чтение перед правками кода |
| [observability/](observability/) | что видно в проде | телеметрия: обзор, аудит поверхностей, дашборды, развёртывание SigNoz |
| [modules/](modules/) | как подключаются внешние модули | контракт «ядро ↔ модуль» и ТЗ на его части |
| [operations/](operations/) | как запускать и обслуживать | контейнер, удалённый доступ |
| [design/](design/) | как это выглядит | конвенция дизайна, аудит соответствия макетам, кликабельные прототипы |
| [adr/](adr/) | почему решили так | architecture decision records |
| [research/](research/) | что выяснили | исследования и срезы во времени — **не поддерживаются** после написания |
| [omo/](omo/) | чужие материалы | переводы промптов oh-my-openagent + правовая рамка |
| [assets/](assets/) | картинки и файлы | скриншоты README, тема OnlyOffice |

## Что где лежит

**[architecture/](architecture/)** — [api.md](architecture/api.md) (справочник REST),
[features.md](architecture/features.md) (детали реализованных фич),
[sandbox.md](architecture/sandbox.md) (среда исполнения local/container),
[llm-providers.md](architecture/llm-providers.md),
[mcp-servers.md](architecture/mcp-servers.md),
[knowledge.md](architecture/knowledge.md) (заметки и Dify),
[personas.md](architecture/personas.md),
[spend-analytics-api.md](architecture/spend-analytics-api.md).

**[observability/](observability/)** — [overview.md](observability/overview.md) — главный
документ раздела; остальные три подчинены ему.

**[modules/](modules/)** — [integration-contract.md](modules/integration-contract.md) —
источник правды; ТЗ (`core-requirements`, `design-kit`, `llm-channel`) ссылаются на него.

**[research/](research/)** — материалы с датой: паритет фич с Claude Code, исследование
мессенджеров (не реализовано), дорожная карта командной зоны. Читать как «так было тогда».

## Куда класть новое

- Описание подсистемы, которое надо прочитать перед правкой её кода → `architecture/`,
  плюс короткая выжимка со ссылкой в `CLAUDE.md`.
- Инструкция «как развернуть/настроить/починить» → `operations/`.
- Разбор вариантов, прототип, оценка чужого API → `research/` или `design/mockups/`.
- Принятое архитектурное решение с альтернативами и последствиями → `adr/`,
  имя `ADR-NNN-краткая-суть.md` латиницей.
- Картинка для документа → `assets/`; относительные ссылки на неё панель «Документация»
  и GitHub резолвят одинаково.

Правило про объём — в [CLAUDE.md](../CLAUDE.md): крупное описание живёт отдельным файлом
здесь, а в `CLAUDE.md` попадает выжимка со ссылкой.
