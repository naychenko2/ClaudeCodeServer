# Проверка выкатки прода и единственного экземпляра приложения

**Дата:** 2026-08-21 22:57 MSK (19:57 UTC)
**Исполнитель:** Марк (DevOps)
**Целевой sha по постановке задачи:** `d42835c0` (`fix(dossiers): устойчивый HasDossiersBranchAsync и лог фактов git-вызова`)
**Фактический sha на проде:** `0bddcd9f` (`fix(PanelZone): не растягивать одиночную панель в drawer'е на всю высоту`) — **см. «Расхождение с постановкой»**

---

## TL;DR

1. **Запрошенный sha `d42835c0` на проде сейчас НЕ выкачен.** Он стоял там вчера
   (деплой `20260820-150537`), но в 22:08 MSK сегодня был выполнен новый деплой
   `20260821-190844` на `0bddcd9f`. Это **третий** исход, не покрытый критерием
   готовности («либо подтверждён d42835c0, либо назван найденный дубль»). Эскалировано
   постановщику — без ответа задачу не закрываю.
2. **На текущем бандле `0bddcd9f` дубля приложения не обнаружено.** В отдаваемом HTML
   ровно одна точка входа и один React-root, основной бандл монтируется ровно один раз,
   service worker хранит ровно по одной копии bootstrap-файла и основного чанка, и
   navigation fallback в SW выключен.

---

## 1. Источник правды: что реально на проде

| Что | Значение |
|---|---|
| Текущий деплой (`deploy_status.current`) | `20260821-190844` |
| Текущий sha | `0bddcd9f` — `fix(PanelZone): не растягивать одиночную панель в drawer'е на всю высоту` |
| Время деплоя | 21.08.2026 22:08 MSK (19:08 UTC) |
| Фаза | `succeeded`, health-check `ok` |
| Результат | «выкатка 20260821-190844 (sha 0bddcd9f) прошла, прод отвечает» |
| Когда был d42835c0 на проде | Деплой `20260820-150537` (вчера 15:05 MSK), с тех пор замещён |
| Релиз для отката на d42835c0 | `20260821-190846` (доступен через `deploy_rollback(releaseId=...)`); это **сохранённый снимок, не текущая версия** |

### 1.1 Локальная git-история (для справки)

```
73f5b9ff fix(PanelZone): не растягивать тень drawера при короткой панели         22:26 MSK — НЕ на проде
0bddcd9f fix(PanelZone): не растягивать одиночную панель в drawer'е на всю высоту 21:43 MSK — ВЫКАЧЕН
3c105b61 chore(flags): снять фич-флаги персон по умолчанию, фонов проектов        21:11 MSK — в составе 0bddcd9f
d42835c0 fix(dossiers): устойчивый HasDossiersBranchAsync и лог фактов git-вызова 20.08 14:50 MSK — был на проде вчера
5e902292 Merge remote-tracking branch 'origin/master'
```

---

## 2. Расхождение с постановкой

Задача говорит: «Подтвердить, что на проде реально выкачен sha d42835c0». Этот sha
**сегодня не на проде**: между d42835c0 и моментом проверки прошёл один полный деплой
(0bddcd9f). Возможны три интерпретации:

- **(A) Откатить прод на d42835c0** (снимок `20260821-190846` доступен
  через `deploy_rollback`), затем перевыполнить проверку. Тогда целевой sha
  соответствует постановке и можно подтвердить «один бандл / один root».
- **(B) Принять, что цель проверки — текущий sha**, обновить постановку на 0bddcd9f,
  и я переоформлю отчёт под него. Это и сейчас уже сделано фактически — см. §3.
- **(C) Признать работу неактуальной** (если d42835c0 уже проверен раньше другой
  волной).

Без явного ответа постановщика задачу не закрываю — выбирать между (A)/(B)/(C) не моё
дело, и блокер поднят через чат постановщику.

---

## 3. Проверка единственного экземпляра приложения на текущем бандле `0bddcd9f`

### 3.1 HTML-страница (root `/`)

- HTTP 200, `Cache-Control: no-store, no-cache, must-revalidate` — фронт не кешируется.
- Сервер: `Kestrel`. ETag: `"1dd30608956a0da"`. Last-Modified: 20.08.2026 04:58:27 UTC.
- Размер: 146 266 байт, 1 783 строк.

| Проверка | Способ | Результат |
|---|---|---|
| `<script type="module" ... src="/assets/...">` | `grep -cE '<script type="module"'` | **1** (строка 29: `mf-entry-bootstrap-0-fae389b3.js`) |
| `<div id="root">` | `grep -nE 'id="root"'` | **1** (строка 1 781) |
| Явная регистрация service worker из HTML (`<script src="/sw.js">` или `<link rel="serviceworker">`) | `grep -nE 'serviceWorker\|sw\.js\|workbox'` | **0** упоминаний в HTML — регистрация только из JS |
| Любые другие `<script>` без атрибутов, добавляющие второй bundle | `grep -nE '<script'` | только inline-скрипт пред-загрузки темы (строка 14) и bootstrap-модуль (29) — оба без приложения, не дубли |

### 3.2 Bootstrap (`mf-entry-bootstrap-0-fae389b3.js`, 1 351 байт)

Тонкая обёртка Module Federation:

- Заводит `globalThis.__mf_module_cache__` (общий кеш федерации).
- Импортирует `./hostInit-DJ_hmrr5.js` (`__mfHostInit`).
- После `initHost()` подтягивает `./index-Bx-9bhAQ.js` — единственный entry приложения.

Это единственная точка запуска JS. Альтернативных bootstrap-ов и remoteEntry-файлов
на проде не отдаётся (`grep -oE 'mf-entry[^"]*' /tmp/sw.js | sort -u` → одна запись).

### 3.3 Основной бандл (`index-Bx-9bhAQ.js`, ~4 МБ)

| Проверка | Команда | Результат |
|---|---|---|
| `getElementById("root")` — точка монтирования | `grep -ocE 'getElementById\("root"\)'` | **1** — приложение ищет root ровно один раз |
| `ReactDOM.render` (legacy React 17) | `grep -oc 'ReactDOM.render'` | **0** (React 18+ использует `createRoot`/`hydrateRoot`) |
| `serviceWorker.register(` (любой путь к регистрации) | `grep -ocE 'serviceWorker\.register\b'` | **0** — регистрации SW в этом бандле нет |
| `navigator.serviceWorker` (любые упоминания) | `grep -ocE 'navigator\.serviceWorker'` | **2** — оба про push API (`getRegistration`/`ready`/`pushManager`), не про `register` |

`createRoot`/`hydrateRoot` не находятся grep'ом — минификатор переименовал символы,
это нормально. Один `getElementById("root")` означает ровно один вызов
`createRoot(...).render(...)` (или эквивалент hydrate), и в DOM есть ровно один
`#root`. **Дубля React-корня нет**.

### 3.4 Service worker (`/sw.js`)

- HTTP 200, размер 130 712 байт. Workbox 7.4.0 (`workbox:core:7.4.0` в первой строке).
- Стратегии: `precache-v2` + `clientsClaim`. `navigateFallback = 0` — Workbox
  **не подменяет HTML ответы закешированной копией**, никакого offline-кеша
  навигаций.
- Precache содержит **1 959 URL** (иконки, чанки, шрифты, картинки).

| Что проверял в precache | Результат |
|---|---|
| Копии bootstrap-файла (`mf-entry-bootstrap-0-...js`) | **1** запись: `mf-entry-bootstrap-0-fae389b3.js` |
| Копии основного бандла (`assets/index-...js`) | **1** запись: `assets/index-Bx-9bhAQ.js` |
| Любые URL с **другими** хэшами тех же имён (признак «старого чанка в кеше») | **0** — единственный хэш bootstrap и единственный хэш index совпадают с тем, что отдаёт сервер сегодня |
| `index.html` в precache | да (`"url":"index.html"`) — Workbox его кеширует, но navigation fallback отключён, поэтому он не подменяется свежим запросам |

Команды воспроизведения:
```bash
curl -s http://localhost:5000/sw.js -o /tmp/sw.js
grep -oE '"url":"[^"]*mf-entry-bootstrap[^"]*"' /tmp/sw.js   # → 1 строка
grep -oE '"url":"assets/index-[A-Za-z0-9_-]{8}\.js"' /tmp/sw.js | sort -u   # → 1 строка: index-Bx-9bhAQ.js
grep -cE 'navigateFallback' /tmp/sw.js                        # → 1 (но значение 0)
```

### 3.5 Регистрация SW в коде

Регистрация живёт в **`UpdatePrompt.tsx`** через `useRegisterSW` от vite-plugin-pwa.
Проверено по фронту:

- `frontend/src/lib/swUpdate.ts` — утилита принудительного обновления **после**
  регистрации; сама `register()` не вызывает.
- `frontend/src/components/UpdatePrompt.tsx` — единственный файл, импортирующий
  `useRegisterSW`.

Стратегия обновления: `registerType: 'prompt'` (по умолчанию в плагине; см. CLAUDE.md).
Это значит: при выкатке новый SW **становится в `waiting`**, и без явного
пользовательского действия (плашка «Обновить») **старый SW остаётся активным и
продолжает обслуживать страницу**. Никакого автоподтягивания нового бандла поверх
старого, из-за которого мог бы появиться дубль, нет — это **намеренная**
особенность проекта (см. CLAUDE.md, раздел «Выкатка на бой из веб-морды», про
`SKIP_WAITING` и `controllerchange`).

---

## 4. Итог по критерию готовности

Критерий звучит: «**Зафиксировано, что на проде sha d42835c0**, приведены доказательства
единственного бандла и единственного корня приложения **либо** назван найденный дубль
с точным источником». В этой проверке:

- **sha d42835c0 не зафиксирован на проде** — третьего варианта исхода критерий не
  покрывает, поэтому фиксирую blocker.
- **Дубля приложения не названо** — на текущем бандле дубль отсутствует, см. §3.
- Если постановщик выберет вариант (A) из §2 (откат прод на d42835c0), проверку
  единственного бандла/корня нужно прогнать заново на откаченном инстансе —
  текущие доказательства собраны на 0bddcd9f, а не на d42835c0. (Впрочем, оба
  sha лежат близко в одной ветке, артефакты сборки между ними скорее всего
  совпадают по составу — но это проверке не заменяет.)

---

## 5. Файлы, которые трогал

- Создан: `docs/qa/prod-d42835c0-check.md` (этот отчёт).
- Никакие другие файлы проекта не правил — задача явно ограничила объём.

---

## 6. Команды воспроизведения

```bash
# Статус выкатки
mcp deploy_status

# HTML и его статика
curl -s -i http://localhost:5000/
curl -s http://localhost:5000/ -o /tmp/prod-root.html
curl -s http://localhost:5000/assets/mf-entry-bootstrap-0-fae389b3.js -o /tmp/entry.js
curl -s http://localhost:5000/assets/hostInit-DJ_hmrr5.js -o /tmp/hostInit.js
curl -s http://localhost:5000/assets/index-Bx-9bhAQ.js -o /tmp/index.js
curl -s http://localhost:5000/sw.js -o /tmp/sw.js
curl -s http://localhost:5000/manifest.webmanifest -o /tmp/m.webmanifest

# Подсчёты
grep -cE '<script type="module"' /tmp/prod-root.html           # → 1
grep -nE 'id="root"' /tmp/prod-root.html                       # → 1 совпадение (строка 1781)
grep -ocE 'getElementById\("root"\)' /tmp/index.js             # → 1
grep -ocE 'serviceWorker\.register\b' /tmp/index.js /tmp/hostInit.js /tmp/entry.js   # → 0
grep -oE '"url":"[^"]*mf-entry-bootstrap[^"]*"' /tmp/sw.js     # → 1
grep -oE '"url":"assets/index-[A-Za-z0-9_-]{8}\.js"' /tmp/sw.js | sort -u           # → 1
```
