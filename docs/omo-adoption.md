# Использование материалов oh-my-openagent

Документ-соответствие для согласования с авторами [oh-my-openagent](https://github.com/code-yeongyu/oh-my-openagent) (далее — OmO).

## Правовая рамка

- Лицензия OmO — **Sustainable Use License v1.0**: по умолчанию она не разрешает
  использование материалов в стороннем коммерческом продукте.
- С разработчиками OmO достигнута договорённость: **тексты промптов можно использовать
  при условии перевода на русский язык**. Настоящий документ — материал для финального
  согласования этой договорённости; до её письменного оформления фича остаётся
  за выключенными по умолчанию фич-флагами и не публикуется.
- Ранее (до договорённости) в продукт переносились только идеи и структура — собственными
  текстами; они в этот документ не входят.

## Что и как заимствовано

Полные переводы с построчным соответствием — в [docs/omo/translations/](omo/translations/):
у каждого файла frontmatter `source` (путь в репо OmO), `sourceCommit` (17104e1f),
`usage` (место в нашем продукте) и секция **«Адаптации»** — исчерпывающий список
отступлений от дословного перевода (замены инструментов OpenCode/Codex на аналоги
Claude Code, опущенные среда-специфичные куски и т.п.).

### Промпты пантеона агентов → шаблоны персон

Полный перевод промпта попадает в слот «Инструкция» (`contract.instructions`) шаблона
персоны; короткие слоты контракта (характер/тон/правила) — наша выжимка из того же промпта.
Модуль шаблонов: `frontend/src/features/personas/omoPantheonTemplates.ts` (lazy-чанк),
raw-тексты — `frontend/src/features/personas/omo/*.md`.

| Агент OmO | Наш шаблон | Источник (репо OmO) | Перевод |
|---|---|---|---|
| Sisyphus | Оркестратор (Сизиф) | `packages/omo-opencode/src/agents/sisyphus/default.ts` | [sisyphus.md](omo/translations/sisyphus.md) |
| Hephaestus | Мастер (Гефест) | `packages/omo-opencode/src/agents/hephaestus/gpt.ts` | [hephaestus.md](omo/translations/hephaestus.md) |
| Prometheus | Планировщик (Прометей) | `packages/prompts-core/prompts/prometheus/default.md` + skill `ulw-plan` + plan-prepend из `delegate-task/constants.ts` | [prometheus.md](omo/translations/prometheus.md) |
| Atlas | Координатор (Атлант) | `packages/prompts-core/prompts/atlas/default.md` | [atlas.md](omo/translations/atlas.md) |
| Metis | Аналитик (Метида) | `packages/omo-opencode/src/agents/metis.ts` | [metis.md](omo/translations/metis.md) |
| Momus | Ревьюер (Мом) | `packages/omo-opencode/src/agents/momus.ts` | [momus.md](omo/translations/momus.md) |
| Oracle | Консультант (Оракул) | `packages/omo-opencode/src/agents/oracle.ts` | [oracle.md](omo/translations/oracle.md) |
| Librarian | Библиотекарь (Клио) | `packages/omo-opencode/src/agents/librarian.ts` | [librarian.md](omo/translations/librarian.md) |

Ограничения инструментов агентов OmO переданы нашими профилями доступа персон
(readOnly у Прометея/Метиды/Мома/Оракула/Клио) и набором возможностей (tasks/notes/web).

### Оркестрационные тексты → механики продукта

| Материал OmO | Источник | Перевод | Где используется у нас |
|---|---|---|---|
| Режим ultrawork | `packages/prompts-core/prompts/ultrawork/default.md` | [ultrawork.md](omo/translations/ultrawork.md) | Магическое слово ultrawork/ulw/«ультра» в сообщении → инжект блока в ход (флаг `ultrawork-keyword`); константа `OmoPrompts.Ultrawork` (генерируется из перевода) |
| Циклы ralph-loop / ulw-loop | `builtin-commands/templates/ralph-loop.ts`, `hooks/ralph-loop/continuation-prompt-builder.ts` | [loops.md](omo/translations/loops.md) | Цикл «до готово» (флаг `work-loop`): протокол хода, continuation-сообщения, верификационный ход (`OmoPrompts.WorkLoop*`). Верификацию Оракулом заменили самопроверкой со свидетельствами / ревьюером-субагентом |
| Hyperplan (адверсариальное планирование) | `.opencode/skills/hyperplan/SKILL.md` | [hyperplan.md](omo/translations/hyperplan.md) | Фазовые промпты совещания персон (`PersonaMeetingService`: перекрёстная атака, ЗАЩИТА/УТОЧНЕНИЕ/УСТУПКА, дистилляция) и обвязка дискуссии «Обсудить с командой» (`DiscussTeamDialog`) |
| Категории делегирования (8 шт.) | `src/tools/delegate-task/*-categories.ts` | [categories.md](omo/translations/categories.md) | Справочник «ДЕЛЕГИРОВАНИЕ» в промпте персоны-исполнителя задач (`TaskExecutionService`); константа `OmoPrompts.DelegationCategories` |
| Model-специфичные дисциплинарные блоки | `src/agents/sisyphus/{claude-opus-4-7,gpt-5-5,glm-5-2,gemini}.ts` | [model-discipline.md](omo/translations/model-discipline.md) | Дисциплинарные секции `PersonaPromptBuilder` по провайдерам: Claude ← ветка Claude («наименьшее правильное изменение»), DeepSeek ← ветка GPT (самопроверка, намерение хода), GLM ← ветка GLM (пять сбоев, outcome-first) |
| Верификационная дисциплина исполнителя | Hephaestus/Sisyphus-Junior (Termination, «no evidence = not complete») | [hephaestus.md](omo/translations/hephaestus.md), [sisyphus.md](omo/translations/sisyphus.md) | Правила «НЕТ СВИДЕТЕЛЬСТВ = НЕ ГОТОВО» и «остановись после первой успешной верификации» в контракте персоны-исполнителя задач |

### Не заимствовано (бэклог, отдельное согласование при необходимости)

Team-mode (tmux-команды), boulder-state resume субагентов, hashline-правки, IntentGate,
comment-checker, skills (`programming`, `debugging`, `frontend` и др. — у части свои
апстрим-лицензии), security-research.

## Принципы адаптации

1. **Дословный перевод по возможности**; каждое отступление зафиксировано в секции
   «Адаптации» соответствующего файла перевода.
2. **Инструментарий среды**: вызовы OpenCode/Codex (`task()`, `call_omo_agent`,
   `background_output`, `todowrite`, LSP/AstGrep/hashline) заменены аналогами среды
   Claude Code (Task/Agent, todo-инструменты) или нейтральными формулировками.
3. **Рабочие протоколы не переводились**: теги `<promise>`, `<plan>`, литералы
   `DONE`/`VERIFIED`/`OKAY`/`REJECT`, плейсхолдеры `{...}`/`{{...}}` сохранены дословно.
4. **Атрибуция**: файлы с переносами содержат ссылку на OmO и этот документ.

## Обновление переводов

Рантайм-константы генерируются из переводов скриптом [gen-omo-prompts.ps1](omo/gen-omo-prompts.ps1);
фронтовые raw-тексты `frontend/src/features/personas/omo/*.md` — копия тел переводов без
frontmatter и секции «Адаптации». Правишь перевод → перегенерируй/перекопируй.
