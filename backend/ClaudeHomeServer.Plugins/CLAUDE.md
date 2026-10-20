# Вертикаль плагинов (ClaudeHomeServer.Plugins)

Пакеты владельца AI Home (ADR-031): навык и slash-команда — нативный формат плагина CLI,
доставка флагом `--plugin-dir` из каталога владельца; панель — sandbox-iframe без
`allow-same-origin` с мостом v1; логика с внешним миром — серверный прокси `net:{host}`.
**Серверного кода плагина в процессе бэкенда нет** — пакет лишь раскрывается CLI.

## Раскладка (per-owner)

`data/plugins/{owner}/`: `packages/{id}/{version}/` (неизменяемые пакеты),
`installed.json` (записи установки `PluginInstall`: id+version+sha256, выданные права
`granted` отдельно от запрошенных `requested`, `enabled`, `source` — строка, а не enum),
`_drafts/{slug}/` (черновики build-it-yourself). Все пути — только через `SafePath.Join`.

## MCP-тулсет `plugins`: черновики build-it-yourself (шаг 4.1, ADR-031)

Агент собирает плагин ТОЛЬКО через MCP-тулсет `plugins` (маршрут `POST /mcp/plugins/{sessionId}`,
хвост = id чата), который пишет в черновик владельца `data/plugins/{owner}/_drafts/{slug}/`.
Встроенные Write/Edit не годятся: в контейнере каталог не смонтирован на запись, у local-владельцев
Write пишет куда угодно. Инструментов установки/согласия/выдачи прав у агента нет по построению —
принять и выдать права может только человек кнопкой (шаг 4.3).

Инструменты (5, имена не однокоренные, чтобы модель их не путала): `draft_create` (шаблон:
`ccs-plugin.json` + `.claude-plugin/plugin.json` с `name = slug`, version 0.1.0, contributes пуст;
повторный create того же slug → `already-exists`, а не перезапись), `draft_write_file` (путь
относительный, только через `SafePath.Join`; запись в запрещённые места — deny-list/symlink/вне
allow-list — и превышение лимитов → отказ с тем же кодом проблемы, что у валидатора; перезапись
существующего файла допустима — так агент правит), `draft_validate` (валидатор пакета, тот же код,
что при установке), `draft_preview` (сводка «что будет после установки»: вклады, запрошенные права,
секреты — только имена и признак обязательного, дерево файлов, результат последней проверки; без
содержимого секретов и без хостовых путей), `draft_delete_file`.
Slug — грамматика `PluginId` (`[a-z0-9-]+`) с потолком длины `PluginId.MaxSlugLength` (64,
выровнен с клиентской проверкой `isDraftSlug`): сервер не создаёт черновик со slug длиннее 64.

**Запись черновика в ленте чата (шаг 4.5a, ADR-019 §2).** Успешный `draft_create` дописывает в
ленту чата-вызывателя (хвост маршрута) запись `module_record` — якорь карточки черновика:
`module: "plugins"`, `recordType: "plugin_draft"` (якорь `plugin_draft:{slug}`), `data` — только
`{ "slug": "<slug>" }` (без содержимого файлов, прав и хостовых путей — карточка сама загрузит
данные по REST), `fallback` — строка «Плагин: {slug}». Запись идемпотентна: не больше одной на
(сессию, slug) — повторный `draft_create` (после reject/restore) дубль не дописывает;
`draft_validate`/`draft_write_file` записей не пишут, дальнейшие изменения карточка получает по
событию `plugin_draft_changed`. Вызов без сессии-вызывателя — записи нет и ошибки нет; сбой
ленты (Core-шов `IChatFeed`, адаптер `ChatFeed` в Main) не роняет создание — черновик уже на диске.

**Идемпотентность — без отдельного индекса (решение, доработка 15).** `HasRecordAsync` читает
историю сессии целиком, но зовётся только на успешном `draft_create` (редкая операция), поэтому
отдельный индекс не заводим; если станет узким местом — индекс ключей `(module, recordType, ключ)`
рядом с историей на стороне ядра. Гарантия «не больше одной записи на (сессию, slug)» держится
замком **в процессе** (статический `RecordLocks` в `PluginsToolset`, ключ = `(sessionId, slug)`):
два параллельных `draft_create` одной пары сериализуются, дубль не дописывается; между процессами
замок не держит (один серверный процесс — одна лента).

Инварианты (нарушение = дефект):
1. **Состав `tools/list` (5) не зависит от флага, хода и сессии** (инвариант
   `McpToolsetStabilityTests`): иначе перезапуск процесса CLI со всеми MCP-серверами. Флаг
   `plugins-build-yourself`, выключенная подсистема и локальный проект (`ProjectCapabilities`)
   проверяет бэкенд на ВЫЗОВЕ — честный отказ с причиной, не исключение.
2. **Владелец — из claim `sub` сервисного JWT**, не из аргументов; черновики владельца A
   недоступны владельцу B (изоляция по OwnerId).
3. **Валидатор пакета — единственный арбитр.** Тулсет применяет те же коды проблем (allow-list,
   deny-list, лимиты, проверки содержимого) на записи, но не ослабляет правил: атака через
   недоверенное содержимое (агент записывает прочитанный текст) отбивается тем же, что при установке.
   Проверки содержимого (M10 allowed-tools/backtick-exec, поля манифеста) — общий метод
   `PluginPackageValidator.CheckContent`, вызываемый и валидатором, и записью черновика.

## Наблюдатель черновиков: живая перезагрузка (шаг 4.2, ADR-031)

`PluginDraftWatcher` (hosted-сервис, `BackgroundService`) следит за `data/plugins/{owner}/_drafts/`:
когда агент пишет файлы в черновик, открытая у человека карточка с предпросмотром обновляется сама.
Наблюдение — через Core-примитив `RecursiveDirectoryWatcher` (один inotify-экземпляр на процесс,
слежка по перечню каталогов, не `FileSystemWatcher` с `IncludeSubdirectories` —
[docs/architecture/file-watching.md](../../docs/architecture/file-watching.md)).

Инварианты (нарушение = дефект):
1. **Один наблюдатель на процесс, а не на владельца.** Корень — `data/plugins` (НЕ `packages/`
   и не всё дерево): служебные каталоги `packages`/`_uploads` исключены по именем, подписываются
   только `_drafts`. Число наблюдателей = 1 (потолок inotify-ЭКЗЕМПЛЯРОВ жёстче, чем слежек).
2. **Событие — только владельцу черновика.** `plugin_draft_changed` (`slug` + монотонная ревизия,
   БЕЗ содержимого файлов и хостовых путей) уходит через `ISessionBroadcaster.ToOwner` ТОЛЬКО
   владельцу; чужой владелец событие не получает никогда.
3. **Дебаунс 300 мс per (owner, slug).** Запись десяти файлов подряд даёт одно-два события,
   а не десять (каждое событие сбрасывает таймер).
4. **Гейт флага.** События — только тем, у кого включён `plugins-build-yourself`; подсистема
   выключена → `Register` не вызывается → наблюдатель не стартует, приложение поднимается.
5. **Нет `_drafts` → не падает.** Корня `data/plugins` нет при старте — `Start()` молчит;
   создание каталогов подхватывает сам примитив (авто-подписка на новые каталоги).

## REST черновиков: список, предпросмотр, сравнение, «Принять» (шаги 4.3a/4.3b, ADR-031)

`PluginDraftsController` (маршрут `/api/plugins/drafts`, `[Authorize]`): человек просматривает
черновик агента, сравнивает его с установленной версией и «Принимает». Гейты — как у
`PluginsController` (подсистема выключена → 503, `plugins-mvp` off → 403) **плюс**
`plugins-build-yourself` off → 403. На ВСЕХ ручках: сервисный JWT (`typ=svc`) → 403 (черновик
собирает агент через MCP, принимает/сравнивает человек), чужой/несуществующий черновик → 404.

- **`GET /api/plugins/drafts`** — список черновиков владельца: активные (`_drafts`) **и**
  отклонённые (`_rejected`, шаг 4.3c). Массив сводок (`PluginDraftSummary`; ключи PascalCase —
  тип без `[JsonPropertyName]`):
  ```json
  [{ "Slug": "pkg", "Id": "local-a/pkg", "Version": "1.0.0",
     "UpdatedAt": "2026-10-19T05:24:56Z", "Problems": 0, "InstalledSameVersion": false,
     "Rejected": false, "ExpiresAt": null }]
  ```
  `Problems` — число проблем последней проверки (валидатор пакета, тот же, что при установке);
  `InstalledSameVersion` — есть ли запись `installed.json` с тем же `Id`+`Version`.
  `Rejected` — `true` у отклонённых черновиков (`_rejected/{slug}`); `ExpiresAt` — дата, когда
  отклонённый исчезнет (`rejectedAt + 30 дней`, UTC); у активных `Rejected: false`, `ExpiresAt: null`.
  Просроченные отклонённые (> 30 дней) удаляются лениво при этом обращении, без фонового сервиса.

- **`GET /api/plugins/drafts/{slug}/preview`** — предпросмотр; ответ = `PluginDraftService.Preview`
  (тот же, что у `draft_preview` для агента; `PluginDraftPreview`, PascalCase):
  ```json
  { "Slug": "pkg", "Name": "pkg", "Version": "1.0.0",
    "Panels": [], "Commands": [], "Skills": ["skills/a"],
    "RequestedPermissions": ["notes:read"],
    "Secrets": [{ "Name": "LINEAR_TOKEN", "Required": true }],
    "Files": [{ "Path": "ccs-plugin.json", "IsDir": false, "Size": 217 }],
    "Validation": { "IsValid": true, "Problems": [] } }
  ```

- **`GET /api/plugins/drafts/{slug}/panel`** — предпросмотр ПАНЕЛИ черновика (шаг 4.3d):
  **сырой файл** панели (ADR-031 §3.6: сервер HTML не разбирает; документ панели собирает фронт).
  Файл берётся по `entry` из манифеста черновика (тот же контракт, что у установленной панели:
  `entry` — относительный путь `panel/…`, валидируется `IsValidPanelEntry`); без манифеста/панели
  → fallback `panel/index.html`. Гейты те же, что у остальных ручек черновиков:
  сервисный JWT → 403, чужой/несуществующий → 404, нет панели → 404,
  файл > 256 КиБ (`PluginPackageValidator.MaxPanelFileBytes`) → 413.
  Заголовки: `Content-Security-Policy: sandbox; default-src 'none'` + `X-Content-Type-Options: nosniff`.
  Путь только через `SafePath.Join` (без выхода из каталога черновика); symlink не следовать.
  Предпросмотр работает БЕЗ выданных прав: у черновика нет записи установки,
  значит прав нет — `call` от такой панели даёт отказ, а не данные.

- **`GET /api/plugins/drafts/{slug}/diff`** — сравнение черновика с ПОСЛЕДНЕЙ установленной
  версией того же `id` (`PluginDraftDiff`, PascalCase): черновик vs `packages/{id}/{version}/`.
  - нет установленной версии → `Status: "new"`, `InstalledVersion: null`, `Manifest: null`,
    `Added` = список файлов черновика (всё «новое»);
  - иначе → `Status: "updated"`, `InstalledVersion` = версия пакета:
    - `Added`/`Removed`/`Modified` — файлы (путь относительный внутри пакета, `Size` в байтах);
    - изменённый **текстовый** файл — `Lines` (построчный diff: `Kind` = `ctx`/`del`/`add`,
      `OldLine`/`NewLine`); потолок **200 строк на файл** (превышение → `Truncated: true` у файла)
      и **1000 суммарно** (превышение → `Truncated: true` в корне);
    - **бинарный** (NUL-байт) или файл **> 256 КБ** — `Binary: true`, `Lines: null`
      («изменён, N байт», без содержимого);
    - `Manifest` — права (`PermissionsAdded`/`PermissionsRemoved`) и секреты
      (`SecretsAdded`/`SecretsRemoved`/`SecretsRequiredChanged` — сменился признак «обязательный»).
  В ответе нет хостовых путей, только относительные внутри пакета.
  ```json
  { "Status": "updated", "Slug": "pkg", "Id": "local-a/pkg",
    "Version": "2.0.0", "InstalledVersion": "1.0.0", "Truncated": false,
    "Added": [{ "Path": "D.txt", "Size": 5, "Binary": false, "Truncated": false, "Lines": null }],
    "Removed": [{ "Path": "B.txt", "Size": 2, "Binary": false, "Truncated": false, "Lines": null }],
    "Modified": [{ "Path": "A.txt", "Size": 8, "Binary": false, "Truncated": false,
      "Lines": [
        { "OldLine": 1, "NewLine": 1, "Kind": "ctx", "Text": "a1" },
        { "OldLine": 2, "NewLine": 2, "Kind": "ctx", "Text": "a2" },
        { "OldLine": 0, "NewLine": 3, "Kind": "add", "Text": "a3" } ] }],
    "Manifest": {
      "PermissionsAdded": ["net:api.linear.app"], "PermissionsRemoved": ["tasks:read"],
      "SecretsAdded": [{ "Name": "NEW_TOKEN", "Required": true }],
      "SecretsRemoved": [{ "Name": "OLD_TOKEN", "Required": true }],
      "SecretsRequiredChanged": [{ "Name": "LINEAR_TOKEN", "Required": false }] } }
  ```

- **`POST /api/plugins/drafts/{slug}/accept`** — принять: установка по метке `draft:<slug>`
  (`PluginInstaller` — единственный арбитр: валидация, no-op по совпавшему sha, 409 при смене
  пакета). Права НЕ выдаёт сам: `granted = []` (первая установка) или `granted = старые ∩ новые`
  (обновление). Статусы: 400 (проблемы валидатора, `problems` — список),
  409 (та же `id`+`version`, другой sha — «поднимите версию»), 200 (установлено / no-op).
  **`consentRequired` + `newPermissions`** (дефект 86dec98d, ADR-031 §4):
  - **первая установка** (`oldById == null`): `newPermissions = Requested` (ВСЕ запрошенные —
    «новые» для человека), `consentRequired = Requested непусто`. Фронт открывает диалог согласия;
    человек выдаёт права через `grant`.
  - **обновление** (`oldById != null`): `newPermissions = Requested \ старые Requested`,
    `consentRequired = newPermissions непусто`.
  - **no-op** (совпал sha): `newPermissions = []`, `consentRequired = false`.
  **`isUpdate` + `previousVersion`** (Глеб MAJOR 4): признак, что это обновление уже
  установленного плагина, а не первая установка.
  - `isUpdate = true`, когда запись этого `id` уже существовала (обновление/повторный accept);
    `isUpdate = false` — первая установка.
  - `previousVersion` — версия, на которую плагин был установлен ДО этого accept
    (`null` при первой установке; при no-op совпадает с текущей).
  Фронт по `isUpdate` открывает диалог согласия в режиме `modify` (новые права НЕ отмечены,
  выданные остаются отмеченными) при обновлении и в режиме `install` (все запрошенные отмечены)
  при первой установке — иначе новое право при обновлении приходило бы уже отмеченным.
  Ответ (анонимный объект, ключи camelCase):
  ```json
  { "id": "local-a/pkg", "version": "2.0.0", "sha256": "…", "source": "draft:pkg",
    "enabled": true, "requested": ["notes:read", "net:api.linear.app"],
    "granted": ["notes:read"], "consentRequired": true, "newPermissions": ["net:api.linear.app"],
    "isUpdate": true, "previousVersion": "1.0.0",
    "secrets": [{ "name": "LINEAR_TOKEN", "set": true, "updatedAt": "2026-10-19T05:24:56Z" }] }
  ```
  Заданные секреты прошлой версии сохраняются; черновик после успеха НЕ удаляется (агент может
  править дальше).

- **`POST /api/plugins/drafts/{slug}/reject`** — отклонить черновик (шаг 4.3c): переносит
  каталог `_drafts/{slug}` в служебный `_rejected/{slug}` (вне `_drafts` и вне наблюдения
  наблюдателя) атомарным `Directory.Move` и записывает время отклонения
  (`_rejected/{slug}/_rejected.json`). Гейты те же: сервисный JWT → 403, чужой/несуществующий → 404.
  Ответ (анонимный объект, ключи camelCase):
  ```json
  { "slug": "pkg", "rejectedAt": "2026-10-19T05:24:56Z",
    "expiresAt": "2026-11-18T05:24:56Z" }
  ```
  `expiresAt = rejectedAt + 30 дней` (после неё черновик удаляется лениво при `GET /drafts`).

- **`POST /api/plugins/drafts/{slug}/restore`** — вернуть отклонённый черновик (шаг 4.3c):
  переносит `_rejected/{slug}` обратно в `_drafts/{slug}`; событие наблюдателя
  `plugin_draft_changed` приходит как обычно (черновик снова в `_drafts`). Если `_drafts/{slug}`
  уже занят новым черновиком → 409. Гейты те же: сервисный JWT → 403, не отклонён/чужой → 404.
  Ответ: `{ "slug": "pkg", "restored": true }`.

## Инварианты

1. **Per-owner изоляция.** Пакеты, установки, черновики, права и секреты ключуются
   владельцем; чужое — 404. Пакеты НЕ попадают в общие каталоги CLI
   (`~/.claude/skills`, `data/claude-profiles/*/plugins`) — только `--plugin-dir`.
2. **Набор `--plugin-dir` зависит от сессии, не от хода.** Установка/удаление/смена
   версии применяется со следующего хода: CLI перезапускается при изменении состава
   `--plugin-dir`, иначе переиспользует существующий процесс (инвариант 4 ADR-031).
3. **Гейт персоны на весь плагин.** Плагин персоне открывает Skill-биндинг
   `plugin:{id}`; фильтровать навыки внутри пакета — значит переписывать пакет на лету.
4. **Локальный проект (MVP) — без плагинов** через `ProjectCapabilities` (каталог и
   секреты на устройство не едут), без инлайнового `if (project.IsLocal)`.
5. **Граница вертикалей.** Plugins не ссылается на Notes/Tasks; обработчики прав
   (`notes:read`/`tasks:read`) приходят через Core-шов `IPluginCapability`, их
   регистрируют сами вертикали-владельцы: `notes:read` — вертикаль Notes
   (`NotesPluginCapability`, операции `notes.search`/`notes.read`), `tasks:read` —
   вертикаль Tasks (`TasksPluginCapability`, операция `tasks.list`). Граница доступа:
   только данные владельца из `PluginCallContext.OwnerId`, проектные — в рамках проекта
   слота (`PluginCallContext.ProjectId`); чужой владелец/`..`/чужой `id` → `not_found`.
   Лимиты ответа (100 записей, 200 KiB) с усечением `truncated`; служебных полей нет.
6. **Отключаемость (ADR-014).** Тумблер `Subsystems:plugins:Enabled`; при выключенной
   подсистеме `Register` не вызывается и обязательного параметра конструктора от Plugins
   вне вертикали нет (потребители берут `IPluginDirSource` опциональной зависимостью).
7. **Два манифеста (R10-1).** Нативный манифест (`.claude-plugin/plugin.json`) обязателен —
   из него CLI берёт имя пакета (валидатор отбивает отсутствие как `deny-native-manifest-missing`,
   пустой/отсутствующий `name` — как `deny-name-missing`, `name` обязан равняться slug).
   `ccs-plugin.json` — источник `id`/прав/метаданных; без него `id` берётся из нативного.
   Корневой `plugin.json` — НЕ нативный манифест (из allow-list убран).
8. **id/version — единая точка (QA-фикс L4).** `id`/`version` разрешает и проверяет
   `PluginIdentity.Resolve` (Core-модель `ClaudeHomeServer.Models`-совместимая, живёт в
   Services.Plugins) — ЕЁ зовут и `ValidateFull` (проверка, которую видит человек), и
   `PluginInstaller.Install` («Принять»): коды 1:1, поэтому «проверка пройдена» ⇒ «Принять»
   не упадёт по id/version/именам/путям. Агент **ни издателя, ни id не выбирает** (QA-фикс N):
   `id` выводит ТОЛЬКО сервер — `id` не задан → выводится `local-{ownerShort}/{slug}`
   (ownerShort = 12 hex-символов SHA-256 владельца, slug — из метки источника `draft:{slug}`);
   `id` задан → обязан СОПADAТЬСЯ с выведенным `DeriveId(ownerId, slug)`, иначе `bad-id`
   (агент не может записать id уже установленного плагина/чужого издателя и унаследовать
   его granted-права без диалога согласия). `version` обязателен (нет → `missing-version`,
   не-semver → `bad-version`).

## Статика панели: `Assets` (ADR-031 §3.1, §3.6, шаг 3.1)

`GET /api/plugins/{id}/{ver}/assets/{**path}` — раздача файлов панели установленного пакета.
**Контракт пути** (дефект a537ea41): `{path}` — относительно **корня версии пакета**
(совпадает с `contributes.panels[0].entry` из манифеста, например `panel/index.html`),
а не относительно `panel/`. Отдаются **только** файлы под `panel/**`: всё остальное
внутри пакета (`ccs-plugin.json`, `skills/`, `commands/`, `.claude-plugin/`) → 404.
Путь строится через `SafePath.Join`; `../`, абсолютные пути, обратные слеши,
symlink/ReparsePoint → 404.

**Сырой файл** (ADR-031 §3.6, QA-фикс H): сервер HTML панели **не разбирает**
(нет `BuildSrcdoc`, `SplitPanelDocument`, `PanelSrcdoc`) — отдаёт байты файла как есть.
Документ панели (CSP-meta, шим моста, head/body) собирает **фронт** (`buildPanelSrcdoc`).
Потолок размера файла — **256 КиБ** (`PluginPackageValidator.MaxPanelFileBytes`; превышение → 413).
Заголовки: `Content-Security-Policy: sandbox; default-src 'none'` + `X-Content-Type-Options: nosniff`
(документ инертен даже при открытии верхним окном через `?access_token=`; `fetch().text()`
с фронта на заголовки не реагирует).

## Мост v1: пределы стороны хоста (ADR-031 §3.4, шаг 3.2)

Хост (frontend `PluginFrame`/`bridge.ts`) ограничивает панель:
- **вызовы в полёте от одного фрейма — не более 8** (лишний — `result {code: 'limit'}`);
- **размер `args` — не более 64 KiB** (длина JSON; превышение — `limit`);
- **таймаут `ready` — 3 с** (затем состояние `bridge_timeout` и рабочая «Перезагрузить»);
- **высота панели — кламп 48–800 px (десктоп) / 48–560 px (мобильный)**: панель сообщает
  желаемую высоту сообщением `resize`, хост зажимает её в пределы слота.

Увеличение пределов — ломающее изменение для панелей, рассчитанных на текущие (меняется,
сколько данных панель может выкачать за ход), поэтому править только с осознанием.

## REST `call`: тело запроса (ADR-031 §3.4, QA-фикс L1)

`POST /api/plugins/{id}/call` принимает тело (camelCase, `JsonSerializerDefaults.Web`):
```json
{ "capability": "notes.search", "args": { "query": "a", "limit": 3 }, "projectId": "<uuid проекта слота или null>" }
```
- `capability` — **обязательное**, строка из реестра операций `{область}.{глагол}`
  (notes.search, tasks.list, net.fetch, …); пусто → 400 «Не задана capability».
- `args` — объект, по умолчанию `{}` (сырой JSON, M8).
- `projectId` — **необязательное**, строка или null: проект слота, в котором открыта панель.
  **Хост (доверенный) подставляет** `projectId` из контекста слота; **панель (недоверенная)
  сама тело не формирует** (шим моста шлёт `{capability, args, projectId}`).
- Старое `op` и вложенный `context` **не принимаются** (никакой двойной поддержки): фронт,
  шлющий `{op, context}`, получает 400 (capability пустое).

Правила `projectId` (контроллер `PluginBridgeController.Call`):
- задан и проект **существует и принадлежит владельцу** запроса (`IProjectManager.GetById` не
  фильтрует по владельцу — проверка `project.OwnerId == sub` в контроллере) → вызов идёт,
  `tasks.list`/`notes.*` ограничены проектом слота (`PluginCallContext.ProjectId`);
- чужой/несуществующий проект → **404** (not_found), а не данные;
- локальный проект (ADR-016) → **409** (`ProjectCapabilities.PluginsFor`), как и раньше;
- `projectId` не задан (null) → поведение прежнее: только данные владельца (личные).

Фронт: `frontend/src/lib/api.ts` `plugins.call`; контрактные тесты с двух сторон
(бэк `PluginCallContractTests`, фронт `frontend/src/lib/__tests__/pluginCallContract.test.ts`).

## Прокси `net:{host}` (шаг 3.3, ADR-031 §3.4)

Панель (sandbox-iframe, `connect-src 'none'`) не ходит в сеть сама — весь выход наружу делает
сервер: операция моста `net.fetch` → обработчик `PluginNetProxy` (право `net:{host}`).

Инварианты (нарушение = дефект):
1. **Точный хост.** URL обязан совпадать с выданным правом `net:{host}` (сверение по хосту,
   порт из права не несёт). Чужой хост → `denied`. Подстрока/префикс — НЕ точное совпадение.
2. **SSRF.** `SsrfGuard` на резолв адреса: loopback/private/link-local/metadata → `upstream`.
   Проверка — на **каждом шаге редиректа** (редирект на приватный адрес — тот же SSRF).
3. **Закрепление адреса (TOCTOU/DNS rebinding).** Боевой транспорт — `SocketsHttpHandler`
   с `ConnectCallback`: резолвит хост прямо перед TCP-connect и подключается к первому
   публичному адресу. Имя хоста в URI остаётся исходным (Host и TLS SNI не ломаются,
   в отличие от подмены URI на голый IP).
4. **Редиректы** — только на тот же хост, иначе `upstream`; потолок числа → `limit`.
5. **Лимиты.** Таймаут → `timeout`; размер ответа и число запросов на плагин в минуту → `limit`.
   Окно лимита чистится при удалении плагина (`PluginUninstall` → `RemoveRateWindow`).
6. **Белые списки.** Методы GET/POST; исходящие заголовки — по белому списку; в ответ уходят
   только статус + белый список заголовков + тело.
7. **Секрет не в ответе.** Значение секрета (подставленное в исходящий заголовок) никогда не
   возвращается во фрейм: вычищается из тела и заголовков ответа (эхо → `[REDACTED]`).
   Тело — на уровне **байт** (не-текстовый контент не декодируется как UTF-8).
   Префикс `auth` нормализован: ровно один пробел до значения, ноль при пустом префиксе.

## Секреты: REST и хранилище (шаг 3.3, для UI 3.4)

- **Хранилище** — `PluginSecretsStore` (`plugin-secrets.json`, per-owner, в `InstanceSecretFiles`):
  значение живёт только здесь, не в `installed.json` (M7). Метод `Resolve` подставляет
  `{{secret:NAME}}` в заголовки, только если право `secret:NAME` выдано.
- **REST** (всё под `[Authorize]`, гейты как в остальных ручках):
  - `PUT /api/plugins/{id}/secrets/{name}` — тело `{value}`; **user JWT** (сервисный → 403);
    выключенный плагин → 409 (как у call/assets/panel); пустое значение → 400;
    имя обязано быть заявлено в манифесте (`requires.secrets`) **и покрыто ВЫДАННЫМ правом**
    `secret:{name}` (сверка по `granted`, не `requested`; иначе 400). Записывает значение
    в store + `set=true`/`updatedAt` в запись установки.
  - `DELETE /api/plugins/{id}/secrets/{name}` — user JWT (сервисный → 403); выключенный
    плагин → 409; убирает значение, `set=false`.
  - `GET /api/plugins` — по каждому секрету только `{name, set, updatedAt}`; **значение с
    сервера не уходит никогда**.
  - `GET /api/plugins` — поле `contributes` (QA-C): вклады пакета, нужные UI, **только**
    `panels: [{id, title, entry}]` из манифеста установленной версии (`ccs-plugin.json` →
    `.claude-plugin/plugin.json`, `contributes.panels`). `entry` — путь относительно **корня
    пакета** (как в `contributes.panels[].entry`, например `panel/index.html`) — тот же контракт
    пути, что у `GET /api/plugins/{id}/{ver}/assets/{entry}` (ADR-031 §3.1): фронт по `entry`
    забирает статику панели. Без панели → `panels: []` (пустой массив, не null). Значения
    секретов и хостовые пути (`data/plugins/…`) в `contributes` не попадают; `slot` не отдаётся
    (UI не использует).
- **`updatedAt`** живёт в `PluginInstall.Secrets` (`installed.json`) — не секрет, поэтому в
  облачный архив едет; значение — в `plugin-secrets.json` (зашифрованный архив).

Устройство, модель прав и мост — [ADR-031](../../docs/adr/ADR-031-plugins.md), карта фичи —
[plugins.md](../../docs/architecture/plugins.md).
