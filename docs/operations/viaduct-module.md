# Viaduct Community: сборка, раздача, выкатка, откат

Тип: operations · Статус: действует с 2026-09-26
План встраивания и остальные задачи серии — [viaduct-embed-plan.md](../research/viaduct-embed-plan.md).
Устройство раздела и ограничения для пользователя — [architecture-section.md](../features/architecture-section.md);
решение и лицензионные рамки (BUSL-1.1, только для своих) — [ADR-016](../adr/ADR-016-viaduct-architecture-section.md).

C4-редактор [Viaduct Community](https://github.com/quietgridlabs/viaduct) (BUSL-1.1) раздел
«Архитектура» показывает в iframe-песочнице. **Код Viaduct в репу CCS не попадает**: в репе
живут только скрипт сборки и серверная раздача, сама сборка лежит в каталоге данных инстанса.

## Что где

| Что | Где |
|---|---|
| Скрипт установки/обновления | [scripts/build-viaduct.ps1](../../scripts/build-viaduct.ps1) |
| Источник сборки | машинный (переменные `VIADUCT_REPO_URL` + `VIADUCT_COMMIT`), иначе публичный апстрим — см. «Источник сборки» |
| Закреплённый коммит апстрима | `1af2d6e21e2ab66e8e62caf67063eac7fc1dfd57` (тег `v0.1.2`) — дефолт скрипта без машинного источника |
| Сборка | `{data}/modules/viaduct/dist` + маркер `.viaduct-build.json` (репа, коммит, base, хэш шима, версия Monaco) |
| Monaco | `monaco-editor` `0.56.0` (дефолт `-MonacoVersion`, совпадает с lock-файлом коммита) → `dist/monaco/vs` |
| Раздача | `GET /modules/viaduct/**` — [ViaductStaticHosting.cs](../../backend/ClaudeHomeServer.Architecture/Services/Architecture/ViaductStaticHosting.cs), в вертикали; Main ставит ветку циклом по Core-шову `IStaticBranchContributor` |
| MF-remote раздела | `frontend/modules/architecture` → `npm run build:architecture` (входит в `npm run build`) → `dist/architecture-remote`, раздаётся по `/architecture-remote/` (файлы лежат в `wwwroot/architecture-remote` и отдаются общей раздачей статики как обычные файлы — гейтом не закрыты; «только загруженные» — это список `/api/subsystem-modules`, по которому оболочка и грузит remote); дев — `npm run dev:architecture` |
| Исключение из бэкапа | `BackupPaths.ShouldInclude`: `modules/viaduct/**` (соседние `modules/{id}/module.json` едут) |

`{data}` — каталог `DataPath` инстанса: на бою `C:\deploy\claude\data`, на хостовом
дев-стенде через `dotnet run` — `backend\ClaudeHomeServer\bin\Debug\net10.0\data`
(или свой `DataPath` из `appsettings.Local.json`).

## Установка и обновление

```powershell
# дев-стенд
powershell -ExecutionPolicy Bypass -File scripts\build-viaduct.ps1 -DataDir backend\ClaudeHomeServer\bin\Debug\net10.0\data
# бой
powershell -ExecutionPolicy Bypass -File scripts\build-viaduct.ps1 -DataDir C:\deploy\claude\data
```

Требует `git`, Node 22+ и `npm`; ходит только на хостинг репы и в npm registry. Первый
прогон — около 5 минут (`npm ci` + `vite build`), повторный с тем же источником — мгновенный
выход «уже установлен» (идемпотентность по маркеру). `-Force` — пересобрать принудительно.

### Источник сборки

Какую репу и какой коммит собирать, скрипт решает так (первое найденное побеждает):

1. явные `-RepoUrl` / `-Commit` — хоть один задан, переменные окружения не читаются вовсе
   (поэтому для своей репы передавай оба; один `-Commit` — это коммит апстрима);
2. **машинные переменные окружения** `VIADUCT_REPO_URL` + `VIADUCT_COMMIT` — задаются парой
   (одна без другой — отказ), живут только на машине и в git не попадают. Так собирается
   сборка с нашими правками поверх апстрима: адрес такой репы в репозиторий CCS, доки и
   коммиты не пишем;
3. публичный апстрим на закреплённом коммите (таблица выше) — поведение по умолчанию.

**Правки самого Viaduct в CCS не делаются.** Всё, что внутри бандла (канвас, панели, локаль,
фиксы UI), дорабатывается только во внешней ветке владельца и приезжает сюда сменой
`VIADUCT_COMMIT`. На машине без этих переменных (чужой инстанс, сборка с апстрима) Viaduct не
трогаем совсем: не накладываем патчи из скрипта, не правим собранный `dist`, не лечим баги
бандла обходами в шиме — иначе инстансы разъедутся, а правка потеряется при следующей
сборке. В таком окружении меняется только обвязка CCS; понадобилась правка редактора —
сказать человеку, а не делать самому.

```powershell
# один раз на машине (пользовательские переменные; новый терминал их увидит)
[Environment]::SetEnvironmentVariable('VIADUCT_REPO_URL', '<адрес репы>', 'User')
[Environment]::SetEnvironmentVariable('VIADUCT_COMMIT', '<полный SHA>', 'User')
```

Доступ к закрытой репе — через Git Credential Manager машины: пароль/токен в адресе скрипт
отвергает, PAT никуда не кладём; в лог и маркер адрес идёт без логина (на его месте мог бы
оказаться токен). Коммит — только полный SHA
(40 hex): shallow-фетч по короткому SHA хостинг не отдаёт. Для репы, отличной от апстрима,
коммит обязателен — закреплённый коммит апстрима к ней не подходит.

Перевыбор сторон связей после переноса карточки в сборке с нашими правками включается
настройкой холста самого редактора («Расширения», по умолчанию включён) — шим в это не
вмешивается. Русский интерфейс в такой сборке — её собственный дефолт, шим язык не трогает.

Что делает скрипт:

1. Во временной папке `git fetch --depth 1` ровно выбранного SHA, сверка `HEAD`.
2. `npm ci --ignore-scripts` → `vendorRedoc` → `tsc -b` → `vite build --base=/modules/viaduct/`.
   `npm run build` не используется: хвост аргументов уехал бы в `prerender`, а снимки
   лендинга для SEO встраиванию не нужны. Метрики (`VITE_METRICS_DISABLED=1`) и Umami
   (пустой `VITE_UMAMI_WEBSITE_ID`) выключены на сборке.
3. Пост-обработка под префикс: Vite переписывает только то, что прошло его конвейер, а файлы
   `public/` копирует как есть — корневые `/fonts/`, `/vendor/`, иконки и `manifest.webmanifest`
   правятся скриптом. После правки — контроль: осталась хоть одна ссылка мимо префикса →
   скрипт падает, прежняя сборка не тронута.
4. Инжект шима [scripts/viaduct-shim.js](../../scripts/viaduct-shim.js) первым скриптом
   `<head>` и дезактивация module-скрипта бандла — **по умолчанию** (свой файл — `-ShimPath`,
   сборка без шима для отладки — `-NoShim`; без шима модель не переживает перезагрузку).
   Не нашёл ровно один module-скрипт → громкий отказ: значит, структура сборки Viaduct
   поменялась. Правка шима = пересборка скриптом (маркер хранит хэш шима).
5. Локальный Monaco (редакторы документов, sequence, контрактов). Viaduct штатно тянет
   `loader.js` с `cdn.jsdelivr.net`, CSP раздачи это режет — поэтому AMD-сборка
   `monaco-editor/min/vs` кладётся в `dist/monaco/vs`. Берётся из `node_modules` Viaduct
   (тот пакет, против которого собран бандл); если lock-файл коммита держит не
   `-MonacoVersion`, закреплённая версия ставится отдельным `npm install` мимо дерева Viaduct.
   В `<head>` после шима встают описание `<script type="application/json" id="viaduct-monaco">`
   (путь `vs` и адреса самодостаточных воркеров `editor.worker-*.js`/`json.worker-*.js`) и
   `monaco/vs/loader.js`. Шим поднимает `vs/editor/editor.main` до старта бандла —
   `@monaco-editor/loader` видит готовый `window.monaco` и на CDN не идёт; бандл стартует,
   когда есть и модель от хоста, и Monaco (провал Monaco бандл не держит: холст работает).
   Воркеры — из `blob:` по тексту файла (`fetch` → `Blob` → `new Worker`): из непрозрачного
   origin `new Worker(url)` бросает `SecurityError`. `MonacoEnvironment` шим ставит **после**
   загрузки `editor.main` — AMD-сборка 0.56 при загрузке перетирает его своим `getWorker`, и
   Monaco молча уводит воркеры в главный поток (грабля поймана на смоуке 2026-09-27).
   Не нашёлся ровно один воркер каждого вида или `loader.js` — громкий отказ.
   Смена версии Monaco = пересборка (версия в маркере). CSP при этом не ослаблен: всё с
   `'self'`, воркеры — `worker-src 'self' blob:`, как и было.
6. Атомарная подмена: копия в `dist.new` рядом с целью → `rename` старой в `dist.old` →
   `rename` новой в `dist`. Сервер резолвит файлы по пути на каждый запрос — **рестарт не
   нужен** ни после установки, ни после обновления.

Обновление Viaduct — только осознанное: новый полный SHA (в `VIADUCT_COMMIT` при машинном
источнике, иначе `-Commit`), прогон на деве, смоук раздела, потом на бою. Смена репы или
коммита меняет маркер — следующий прогон пересобирает сам.

## Раздача

Ветка `/modules/viaduct` терминальная и стоит до SPA-фолбэка фронта CCS: промах — честный
404, а не `index.html` CCS. Ветка сбрасывает выбранный роутингом endpoint (иначе
`MapFallbackToFile` перехватывает корень `/modules/viaduct/`, а StaticFiles при выбранном
endpoint молчат). Без авторизации — в бандле нет ни моделей, ни токенов, ни адресов API. **Ветка под тумблером подсистемы:** её ставит сама вертикаль (`ViaductStaticBranch` в `Register`), поэтому при `Subsystems:architecture:Enabled=false` или незагруженной dll ветки нет — корень `/modules/viaduct/` уходит в SPA-фолбэк CCS, ассеты с расширением получают 404. Место в конвейере прежнее: цикл по `IStaticBranchContributor` стоит после forwarded headers, HTTPS-редиректа и перехватчика превью-хоста, до SPA-фолбэка.

Заголовки:

- `Access-Control-Allow-Origin: *` на всём — ES-модули из iframe без `allow-same-origin`
  уходят CORS-запросом с `Origin: null`;
- `Content-Security-Policy` на `.html`: всё только `'self'`, внешних адресов нет;
  `'unsafe-inline'` у скриптов — ради загрузочного инлайн-скрипта `index.html` Viaduct
  (в песочнице opaque origin он не видит ни куки, ни хранилища CCS);
  `frame-ancestors 'self'` — встраивать может только сам CCS;
- кэш: `.html` — `no-store`, `/assets/**` — `immutable` (хэши в именах), прочее — `no-cache`;
- только `GET`/`HEAD`, остальное — 405. Маркер `.viaduct-build.json` не раздаётся.

## Модуль не установлен

Любой запрос под `/modules/viaduct/` отвечает `404` с телом
`{"error":"viaduct_not_installed", ...}` — по коду `viaduct_not_installed` раздел
«Архитектура» пишет «модуль не установлен» (проверка — `HEAD`/`GET /modules/viaduct/index.html`).
Инстанс без сборки полностью работоспособен.

## Выкатка на бой

Сборка живёт в `C:\deploy\claude\data`, а агент выкатки (`scripts/ops/deploy-agent.ps1`)
каталог `data` не трогает ни при выкатке, ни при откате — поэтому сборка переживает любые
выкатки продукта и ставится **один раз** прогоном скрипта с `-DataDir C:\deploy\claude\data`
на боевой машине. Код раздачи едет в `modules/architecture/ClaudeHomeServer.Architecture.dll`, MF-remote — в `wwwroot/architecture-remote`; оба приезжают обычной выкаткой продукта (publish-цель `CopyArchitectureModuleOnPublish`, remote — через `frontend/dist`).
В каком порядке — неважно: код без сборки отвечает «не установлен», сборка без кода просто
лежит.

После восстановления из бэкапа сборки нет (в архив она не едет) — прогнать скрипт заново.

## Откат

- **Убрать модуль:** удалить папку `{data}\modules\viaduct` — раздел покажет «модуль не
  установлен», рестарт не нужен.
- **Вернуть прежнюю версию:** прогнать скрипт с прежним источником (`-Commit` или прежнее
  значение `VIADUCT_COMMIT`); вернуться на апстрим — удалить обе переменные.

## Хранилище модели и мост

- Модель — `docs/architecture/model.viaduct.json` в корне проекта (под git), рядом
  `model.viaduct.meta.json` с «кто и когда» (`updatedAt`/`updatedBy`) и полями генератора.
  REST: `GET/PUT /api/projects/{id}/architecture/model` (владелец проекта; версия — SHA-256
  содержимого; PUT с устаревшей `baseVersion` → `409 version_conflict` с текущим состоянием).
- Мост postMessage (v=1): фрейм шлёт `ready`/`started`/`dirty`/`save`/`prefs`, хост —
  `init`/`focus`/`flush`. Хост принимает сообщения только от окна своего фрейма; токена и
  адресов API во фрейме нет. Протокол целиком — в шапке шима.
- Настройки вида Viaduct (`c4-*`) живут в localStorage страницы CCS под
  `viaduct-prefs:{projectId}`, в файл не пишутся.
- Раздел в UI (панель «Архитектура» после «Графа» + документ в центре) — без фич-флага: виден,
  когда модуль загружен; выключается `Subsystems:architecture:Enabled=false` или `Enabled: false`
  у записи `architecture` в `DynamicModules`.

## Сборка remote с чистого клона

`frontend/modules/architecture` и `frontend/modules/notes` держат свои `package.json`, но
отдельной установки не требуют: ни lock-файла, ни своих `node_modules` у них нет, а все
зависимости (`vite`, `@module-federation/vite`, `@vitejs/plugin-react`, `typescript`, React)
есть в `frontend/package.json` и резолвятся node вверх по дереву из `frontend/node_modules`.
Поэтому штатного шага агента выкатки (`npm ci` в `frontend/` + `npm run build:quiet`,
`scripts/ops/deploy-agent.ps1`) хватает: `build` сам зовёт `build:notes` и `build:architecture`
и публикует их в `dist/notes-remote` и `dist/architecture-remote`. Проверено 2026-09-26 на
копии без `node_modules` (`git worktree` + текущее состояние `frontend`): `npm ci` + `npm run
build` — оба remote собраны. Правило: новая зависимость remote добавляется в
`frontend/package.json`, а не в `package.json` модуля — иначе чистый клон её не увидит.

В логе сборки remote печатается `[ Module Federation DTS ] … TYPE-001` — не падение сборки
(exit 0) и не про установку: генератор `@mf-types` гоняет `tsc` по оболочке без типов
`vite/client` и спотыкается о `import.meta.env`/`glob` и `*.png` — к чистому клону отношения не
имеет, отдельная задача при желании.

## Известные ограничения (к задаче шима)

Ограничения самого раздела (потеря правки при быстром переходе в другой проект, старое имя
«(корень)», просмотр на телефоне) — в [architecture-section.md](../features/architecture-section.md#известные-ограничения).

- ~~Monaco грузится с CDN~~ — **вылечено 2026-09-27** локальной раздачей (шаг 5 установки).
  Проверено на дев-стенде: документ элемента, sequence-диаграмма и тело запроса endpoint
  правятся в Monaco и через Save доезжают до `model.viaduct.json`, воркеры идут из `blob:`,
  CSP-нарушений в консоли нет. Диагностика во фрейме — `window.__viaductHost`
  (`monaco`: встал ли, `workers`: сколько поднято). Правка, не меняющая разобранную модель
  (например, комментарий PlantUML), кнопку Save не включает — это поведение Viaduct.
- Роутер Viaduct (BrowserRouter без basename) на чужом пути `/modules/viaduct/` сам уводит
  на `/editor` — в iframe безвредно (история живёт внутри фрейма), но при прямом открытии
  во вкладке URL становится `/editor` CCS, и перезагрузка уйдёт в SPA CCS. Штатный путь —
  только iframe; `replaceState('/editor')` до старта бандла делает шим.
- Без шима редактор в песочнице стартует (смоук 2026-09-26), но модель не переживёт
  перезагрузку фрейма — хранение даёт только шим (он теперь инжектится по умолчанию).
- Родного `readOnly` у Viaduct в локальном режиме нет (он есть только у облачных проектов):
  «Только просмотр» в CCS значит «правки на холсте не сохраняются», холст при этом
  двигается. `focus(elementId)` работает через штатный параметр `?focus=` редактора.
- Service worker CCS (scope `/`) перехватывал навигацию на `/modules/viaduct/` и отдавал
  оболочку CCS — путь внесён в `denylist` `frontend/src/sw.ts`. У пользователя со старым SW
  исправление приедет после обновления PWA.
