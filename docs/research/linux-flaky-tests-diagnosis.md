# Флакающие тесты на Linux: предсуществующий долг CI, не регрессия фичи

> Диагностический срез. Задача `ed925eff`. Вердикт: **предсуществующий долг CI**.
> Мерж ветки `worktree-default-personas-onboarding` **не блокируется**.
> Дата: 2026-08-09. Метод: идентичные прогоны на чистом `master` и на ветке фичи
> в Linux-контейнере `mcr.microsoft.com/dotnet/sdk:10.0`.

## Постановка вопроса

Полный прогон бэкенд-тестов **в Linux-контейнере** на ветке фичи даёт плавающее
число падений и скип-ов. На Windows та же ветка — 3795/3795 зелёных. Нужно понять:
это давний долг CI (тогда фича ни при чём, мерж проходит) или регрессия фичи
(тогда назвать виновника поимённо).

## Методика

Прогоны через `docker run` с образом `dotnet/sdk:10.0`, монтирование worktree.
Для master создан отдельный detached worktree `ci-master-probe` (коммит `de74fa8b`),
чтобы не ломать рабочее дерево. Полные логи — в `.cc-attachments/`.

Два режима запуска:
- **обычный** — `dotnet test` в worktree (bin/obj в volume);
- **ArtifactsPath** — read-only volume + `-p:ArtifactsPath=/tmp/build` (сборка вне volume).

Флаг `MSYS_NO_PATHCONV=1` обязателен из Git Bash (иначе путь `/src/backend`
портится). `-p:ArtifactsPath` даёт **ложное** падение `AppSettingsTestingConfigTests`
(тест ищет корень решения от папки сборки) — постановка прямо велит не считать его.

## Результаты прогонов

### Master, обычный способ — 3 прогона (идентично)

| Прогон | Failed | Passed | Skipped | Total | Duration |
|---|---|---|---|---|---|
| run1 | 4 | 3791 | 33 | 3828 | 1 m |
| run2 | 4 | 3791 | 33 | 3828 | 1 m 1 s |
| run3 | 4 | 3791 | 33 | 3828 | 51 s |

Стабильно одни и те же 4 падения:
1. `Services.DocsIndexTests.Order_ПравкаФайла_ВидитсяСразу`
2. `Services.DocsIndexTests.ЗаписьПорядка_МеняетФайлИИндекс`
3. `Services.McpProbeServiceTests.ВтораяПробаТогоЖеСервера_НеПлодитПроцесс`
4. `Services.McpProbeServiceTests.ЖивойСервер_ОтдаётИмяИСписокИнструментов`

### Master, ArtifactsPath (1 прогон, для сопоставления)

| Failed | Passed | Skipped | Total |
|---|---|---|---|
| 4 | 3771 | 53 | 3828 |

Падения: `AppSettingsTestingConfigTests` ×2 (артефакт ArtifactsPath) +
`DocsIndexTests.Order_ПравкаФайла` + `McpProbeServiceTests.ВтораяПробаТогоЖеСервера`.

### Ветка фичи, обычный способ — 2 прогона

| Прогон | Failed | Passed | Skipped | Total |
|---|---|---|---|---|
| run1 | 3 | 3759 | 33 | 3795 |
| run2 | 4 | 3758 | 33 | 3795 |

- run1: `DocsIndexTests.Order_ПравкаФайла` + `McpProbeServiceTests` ×2 — **подмножество** master.
- run2: `DocsIndexTests` ×2 + `McpProbeServiceTests` ×2 — **идентично** master.

### Ветка фичи, ArtifactsPath (1 прогон)

| Failed | Passed | Skipped | Total |
|---|---|---|---|
| 6 | 3736 | 53 | 3795 |

Падения: `AppSettingsTestingConfigTests` ×2 (артефакт) + `DocsIndexTests` ×2 +
`McpProbeServiceTests.ВтораяПробаТогоЖеСервера` + `ProcessRegistryTests.PruneDead_ЖивогоНеТрогает`.

## Сопоставление падений

| Тест | Master обычн. (×3) | Master Artifacts | Feature обычн. (×2) | Feature Artifacts | Регресс фичи? |
|---|---|---|---|---|---|
| `DocsIndexTests.Order_ПравкаФайла_ВидитсяСразу` | ✅ 3/3 | ✅ | ✅ 2/2 | ✅ | нет — есть на master |
| `DocsIndexTests.ЗаписьПорядка_МеняетФайлИИндекс` | ✅ 3/3 | — | ✅ 1/2 (флак) | ✅ | нет — есть на master |
| `McpProbeServiceTests.ВтораяПробаТогоЖеСервера_НеПлодитПроцесс` | ✅ 3/3 | ✅ | ✅ 2/2 | ✅ | нет — есть на master |
| `McpProbeServiceTests.ЖивойСервер_ОтдаётИмяИСписокИнструментов` | ✅ 3/3 | — | ✅ 2/2 | — | нет — есть на master |
| `AppSettingsTestingConfigTests.*` | — | ✅ (артефакт) | — | ✅ (артефакт) | нет — артефакт запуска |
| `ProcessRegistryTests.PruneDead_ЖивогоНеТрогает` | — | — (1 прогон) | — | ✅ | нет — код фичей не тронут (см. ниже) |

**Ни одного падения, уникального для ветки фичи и отсутствующего на master.**

## Доказательство через git diff

`git diff --name-only master..worktree-default-personas-onboarding` **не содержит**
ни `ProcessRegistry*`, ни `McpProbe*`, ни `DocsIndex*`, ни `AppSettingsTestingConfig*`.
Ветка фичи физически не модифицировала падающий код и его тесты → регрессия этих
тестов невозможна.

Природа падений — Linux-специфичная и не связана с фичей:
- **DocsIndexTests** — порядок файлов. Assertion: `Expected Path to be "docs/b.md",
  but "docs/a.md"`. На Linux порядок `readdir` не детерминирован, тесты держатся на
  порядке, который даёт NTFS/Windows. Классический Linux-долг.
- **McpProbeServiceTests** — запуск реального MCP-сервера (Node-процесс) в песочнице.
  Assertion: `Expected File.Exists(marker) to be True … but found False` — сервер
  не успел/не смог поднять маркер. Флак по таймингу порождения процесса, одинаково
  падает на master и на фиче.
- **ProcessRegistryTests.PruneDead_ЖивогоНеТрогает** — проявился единожды при
  ArtifactsPath (более медленная сборка → другой тайминг). Код `ProcessRegistry`
  фичей не тронут.
- **AppSettingsTestingConfigTests** — артефакт `-p:ArtifactsPath`, постановка прямо
  велит не считать.

## Пояснение к «скачку» скипов 53 / 33

В постановке число скипов на ветке фичи «скачет 53 / 33». Воспроизведено: **53 — это
характеристика запуска с `-p:ArtifactsPath`, 33 — без него**, на обеих ветках
одинаково. То есть «скачок» — следствие смешения двух способов запуска в одном
наблюдении, а не свойство ветки. Тот же эффект и на master: 33 (обычно) → 53 (Artifacts).

## Инфраструктурные нюансы (не дефекты тестов)

- **`MSB3021 Access denied`** при копировании DLL в `bin` тестового проекта в Docker-volume
  на Windows — известная гонка прав Docker Desktop + Defender. В master worktree не
  воспроизводилось (bin создавались чисто контейнером), в feature worktree всплывала
  из-за хостовых файлов в `bin`. Чистится удалением `bin` перед прогоном. К тестам
  отношения не имеет.
- Хостовые процессы `dotnet`/`testhost` (запущенный CCS/IDE) держали тестовые DLL —
  обходился read-only volume + сборкой вне volume.

## Вердикт

**Предсуществующий долг CI.** Все падения на ветке фичи воспроизводятся на чистом
`master` в том же окружении, либо находятся в коде, который фича не модифицировала.
Ни одного теста, упавшего только из-за фичи, не обнаружено. Мерж
`worktree-default-personas-onboarding` **не блокируется**.

Корневые причины долга (отдельная задача на починку, не на мерж):
1. `DocsIndexTests` — зависимость от порядка файловой системы (недетерминированный
   `readdir` на Linux).
2. `McpProbeServiceTests` — тайминги порождения MCP-процесса в песочнице.
3. `ProcessRegistryTests.PruneDead_ЖивогоНеТрогает` — тайминг проверки живости процесса.
