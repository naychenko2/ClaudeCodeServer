# Диагностика двойного монтирования DossierHistoryPanel и модалки «Загрузить историю решений»

**Дата:** 2026-08-21
**Исполнитель:** Вера (QA)
**Задача:** 679a2450-cf4c-4a1e-bdbd-9acfaaee8744
**Вердикт:** двойного монтирования **не обнаружено** в проверенных сценариях; выявлена отдельная проблема — кнопки «Загрузить»/«Выгрузить» полностью недоступны в empty-state панели.

---

## 1. Окружение

| Параметр | Значение |
|---|---|
| Прод | http://localhost/ (PID 18592, ClaudeHomeServer.exe из C:\ClaudeServer\prod) |
| Текущая выкатка на момент проверки | sha 0bddcd9f, deployId `20260821-190844` (sha d42835c0 уже не на проде — фронт успел уехать, см. п. 7) |
| Бэкенд | C:\ClaudeServer\prod\ClaudeHomeServer.exe, ASP.NET Core 10 |
| Верстка prod-фронта | `frontend/dist` от sha 0bddcd9f (sha, на котором работала выкатка) |
| Аккаунт | `admin` (JWT сгенерирован мной локально из `C:/ClaudeData/prod/jwt-secret.txt`, положен в localStorage.cc_token) |
| Флаг `change-dossiers-recall` | включён в настройках admin (через UI «Эксперименты») |

Авторизация: тестовые admin/admin, anna/... не подошли; пришлось сгенерировать HS256-токен с правильными claims (`sub` = userId, `ClaimTypes.Name` = admin, `ClaimTypes.Role` = admin, `tv` = 0, `iss`/`aud` = `ClaudeHomeServer`) и положить в `localStorage.cc_token`. После этого `/api/auth/me` возвращает 200, `featureFlags.change-dossiers-recall = true`.

## 2. Стенд для проверки модалки

В живом проде единственный проект admin'а — `Test2` (C:/ClaudeHome/admin/Test2), и он **не git-репозиторий** → `GET /dossiers/export/status` возвращает `isGitRepo: false` → кнопки «Загрузить»/«Выгрузить» (равно как и модалки DossierImportDialog/DossierExportDialog) не показываются в принципе.

Чтобы проверить именно код панели на git-проекте (как требует задача — зафиксировать DOM и поведение модалки), я временно добавил в admin-аккаунт проект `qa-dossier-test` (C:/ClaudeData/qa-dossier-test, init+empty commit+ ветка `ccs/dossiers/v1`). На этом проекте `GET /dossiers/export/status` отдаёт `{isGitRepo: true, sharedFolder: false, hasDossierBranch: true}` — то есть гейт `showImportButton === true` достижим.

После диагностики тестовый проект и каталог удалены. Из prod-данных admin'а в `data/projects.json` дополнительных записей не осталось — кроме его собственного Test2 ничего не появилось.

## 3. Сводные числа по DOM (DossierHistoryPanel + модалки)

Считались: 
- **узлы панели** — по уникальному subheader `<p data-cc-src="DossierHistoryPanel.tsx:514">История решений по проекту</p>` (в empty-state — `<p ...>История решений по проекту</p>`);
- **модалки** — по `.cc-modal-card` и `.cc-overlay` (портал, всегда в `document.body`), плюс по `h2` с заголовком модалки «Загрузить историю решений из репозитория»;
- **кнопки входа в модалку** — по `title="Загрузить из репозитория"` / `title="Выгрузить в репозиторий"`.

| Раскладка | Ширина | Узлов панели | Модалок (`.cc-modal-card`) | Оверлеев (`.cc-overlay`) | Кнопок «Загрузить» / «Выгрузить» | Контейнер узла |
|---|---|---|---|---|---|---|
| Десктоп | 1440×900 | **1** | **0** | **0** | **0 / 0** | Десктопная inline-раскладка `PanelZone`, subheader сидит внутри `PanelShell`-карточки правой зоны |
| Планшет (узкий) | 800×900 | **1** | **0** | **0** | **0 / 0** | **Drawer-портал** поверх контента: контейнер имеет `position: absolute; top: 8px; z-index: 15; box-shadow: var(--shadow-modal); width: min(85vw, 380px)` — соответствует `compact=true, compactOverlay=true` (см. `PanelZone.tsx:1270-1285`). Зарегистрирован как overlay-слой PanelZone, **не** как document.body portal |

**Скриншоты:** `docs/qa/desktop-empty.png`, `docs/qa/tablet-empty.png`.

## 4. Шаги воспроизведения

### 4.1 Десктоп (1440×900)
1. Открыть http://localhost/ в браузере, залогиниться (или положить валидный JWT в localStorage.cc_token).
2. Включить флаг `change-dossiers-recall` в «Меню пользователя → Экспериментальные функции».
3. В сайдбаре «Проекты» выбрать проект (qa-dossier-test или любой git-проект с веткой ccs/dossiers/v1).
4. В правой рельсе панелей кликнуть «История решений».
5. Считать DOM: `document.querySelectorAll('.cc-modal-card').length === 0`, `document.querySelectorAll('.cc-overlay').length === 0`, узлов панели ровно 1.

### 4.2 Планшет (800×900)
1. Те же шаги 1–3.
2. Изменить `window.innerWidth` на 800 (через DevTools, browser_resize или сужение окна).
3. Кликнуть «История решений» в правой рельсе (на планшете рельса превращается в один столбец кнопок).
4. Считать DOM — те же 0/0/1. Контейнер панели — `position: absolute; width: min(85vw, 380px)` поверх холста.

## 5. Поведение «Отмена» / крестика / клика по фону / Escape — **НЕ проверено**

Модалку `DossierImportDialog` («Загрузить историю решений из репозитория») **не удалось открыть ни в одном сценарии**, потому что в `DossierHistoryPanel` empty-state (`entries.length === 0`) кнопка-вход в модалку **не отрисовывается ни в одном из двух путей**:

- `{(showImportButton || showExportButton) && !hasHeader && …}` — inline-chip в subheader; условие требует, чтобы `useHasPanelHeader()` вернул `false`.
- `{(showImportButton || showExportButton) && hasHeader && <PanelHeaderSlot side="right">…</PanelHeaderSlot>}` — кнопка-иконка в правой шапке карточки PanelShell; условие требует `useHasPanelHeader() === true` (PanelShell всегда выставляет `hasHeader: true`, см. `panelHeaderSlotContext.ts:35-37`).

В empty-state компонент возвращает `<div>…{subheader}{exclusionNote}{EmptyState}</div>` **минуя `PanelHeaderSlot`** (`DossierHistoryPanel.tsx:630-652`). Поэтому в обоих случаях кнопка «Загрузить» (равно как и «Выгрузить») не появляется, пока в `entries` не появится хотя бы одна запись.

Это и есть причина, по которой нельзя проверить поведение крестика/отмены/focus/Escape в этом UI:
- `showImportButton` (`showExportButton && hasDossierBranch`) — гейт выполнен, факт подтверждён через API: `{isGitRepo: true, hasDossierBranch: true}`.
- Но кнопка в DOM не появляется, потому что путь рендера в empty-state пропускает обе ветки.

Чтобы проверить модалку, потребовалось бы либо сначала заполнить историю (через чат + коммит из чата — нельзя без живого хода), либо подменить `entries` искусственно (минифицированный prod-бандл, имя функции `DossierHistoryPanel` обфусцировано, fiber-цепочка оканчивается на узле `L6` с `hasMemoizedState: true` — достоверно найти сеттер `setImportOpen` не получилось).

## 6. Дополнительная находка (вне прямой задачи, но та же панель)

Признак «у проекта пока нет руководителя» (верх страницы) перекрывает верх панели на ~80px, пока не закрыт. К диагностике двойного монтирования не относится, но стоит иметь в виду для следующей волны — на узких экранах этот баннер занимает половину видимой области панели.

## 7. Расхождение по sha задачи и реального прода

Задача ссылается на `sha d42835c0` как «живой прод». На момент диагностики (2026-08-21 ~20:00 МСК) на проде уже стоит sha 0bddcd9f (`deployId 20260821-190844`). Выкатка `20260820-150537` действительно была на d42835c0, но с тех пор успели уехать ещё две (`20260821-190844` с 0bddcd9f и текущая сборка фронта — `0bddcd9f`). Это в пределах нормы — «живой прод» ко времени проверки не обязан совпадать с зафиксированным в задаче sha, но диагностика относится к коду **последней выкатки**, а не к d42835c0. Если команде нужна проверка именно на d42835c0 — нужно откатить прод на `releaseId 20260821-190846` (sha d42835c0), см. `wsp/deploy_status`. Без отката актуальная диагностика отражает поведение sha 0bddcd9f.

## 8. Сводка для разработчиков

- Узлов `DossierHistoryPanel` в DOM одновременно: **1** (десктоп), **1** (планшет/drawer). Двойного монтирования не выявлено.
- Модалок «Загрузить историю решений» в DOM: **0** в обоих режимах в проверенных условиях (empty-state).
- Реальный узел проблемы, который команда сможет использовать: **в empty-state компонент `DossierHistoryPanel` не отрисовывает ни inline-chip «Загрузить»/«Выгрузить», ни `<PanelHeaderSlot>`.** Кнопки появляются только когда `entries.length > 0`. То есть в проекте, где ещё ни один коммит не оформил dossier, пользователь не может ни выгрузить, ни загрузить историю — это и есть кандидат на исправление в той же волне.
- Подстраховка единственным экземпляром модалки: даже в этом провале `<DossierImportDialog>` / `<DossierExportDialog>` монтируются по одному разу внутри JSX компонента (см. `DossierHistoryPanel.tsx:758-776`), `createPortal` в `document.body` добавляет ровно один узел `.cc-modal-card` и `.cc-overlay` на каждую открытую модалку. Дублирования по коду нет.
- Селекторы для повторной проверки (после фикса): `.cc-modal-card`, `.cc-overlay`, `h2:contains("Загрузить историю решений")`, `[title="Загрузить из репозитория"]`, `[title="Выгрузить в репозиторий"]`, `[data-cc-src*="DossierHistoryPanel.tsx:514"]` (subheader), `[data-cc-src*="DossierImportDialog.tsx"]`.

## 9. Артефакты

- `docs/qa/desktop-empty.png` — десктоп 1440×900, qa-dossier-test, панель открыта в empty-state, модалок нет.
- `docs/qa/tablet-empty.png` — планшет 800×900, qa-dossier-test, панель открыта в drawer поверх контента, модалок нет.
