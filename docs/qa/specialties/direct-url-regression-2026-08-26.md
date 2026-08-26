# QA-прогон: прямой URL ролей и /edit (26.08.2026, Вера)

Задача: `84bca1d8-c2bc-493f-bd97-c943ccf9e0c5`.
Стенд: `master` HEAD `74d238c5` (merge «долить остаток волны 4 — форма правки роли и находки ревью»). Vite dev на :5173, бэкенд :5000.

## TL;DR

**Обе исходные карточки дефектов остаются открытыми.** Через прямой URL формы
правки и визитки ролей не открываются — это первопричина **дефект `8344b598`**
(`PersonasPage` стартует с `specialtiesMode=false`, потому что `App.tsx:490`
делает `navReplace(seed)`, а в `seed` нет `personaView`). Дефект `b74ff206`
воспроизводится только как следствие: сама форма `SpecialtyEditView` работает
(проверено через pushState — все поля, точка-индикатор dirty, кнопки
«Сохранить»/«Отмена»), но через F5 на `#/personas/specialties/executor/edit`
URL сбрасывается на `#/personas` ещё до проверки прав.

## Проверка по 4 пунктам задачи

### 1. Прямой URL роли `#/personas/specialties/executor` — НЕ РАБОТАЕТ

| Шаг | Ожидание | Факт |
|-----|----------|------|
| Чистая вкладка `goto("#/personas/specialties/executor")` | Визитка роли «Исполнитель (универсальный)» | Через ~2 с URL сбрасывается на `#/personas`, рисуется PersonasHub + список персон |
| `F5` на этом адресе | Визитка роли остаётся | URL → `#/personas`, PersonasHub |
| Десктоп 1440×900 | Визитка | Сброс на `#/personas` |
| Мобильный 360 CSS | Карточный режим specialties | Сброс на `#/personas`, отрисовался список персон в мобильной раскладке |

Скриншоты: `.cc-attachments/specialties-direct-url-fail.png`,
`.cc-attachments/specialties-direct-url-after-f5.png`,
`.cc-attachments/specialties-direct-url-fail-mobile.png`.

### 2. Экран правки `#/personas/specialties/executor/edit`

**Через прямой URL + F5**: URL сбрасывается на `#/personas` (см. п. 1).
Скриншот: `.cc-attachments/edit-fail.png`.

**Через pushState без перезагрузки (обход бага 8344b598)**: SpecialtyEditView
отрисовывается, все поля на месте — Доступ (Полный / Только чтение / Свой
список), Инструменты (Задачи / Заметки / Веб), Секции промпта (История
решений / Процессы роли / Правила роли), Пресеты для роли с переключателями
«Типовой текст» / «Задать свой текст» и счётчиком символов, Привязки по
умолчанию, Модели по уровням, Уровень по умолчанию. В шапке кнопки
«Отмена» и «Сохранить» (disabled, пока правок нет).
Скриншот: `.cc-attachments/edit-via-click.png`.

**Dirty-индикатор**: после клика по «Только чтение» в секции Доступ кнопка
«Сохранить» стала активной, и в шапке между «Отмена» и «Сохранить» появилась
тёмная точка (`●`).
Скриншот: `.cc-attachments/specialties-edit-dirty.png`.

### 3. Права под неадмином (sandboxer/12345, роль `user`)

**На визитке роли через клик** `#/personas/specialties/coordinator`:
- Визитка роли «Координатор» рисуется в read-only
- Кнопки «Редактировать», «Сохранить», «Отмена» отсутствуют
Скриншот: `.cc-attachments/specialties-noadmin-noedit.png`.

**Через pushState на /edit** `#/personas/specialties/coordinator/edit`:
- Визитка остаётся в read-only (даунгрейд effectiveViewMode в PersonasSpecialties.tsx:95-96)
- Кнопки «Редактировать» / «Сохранить» / «Отмена» отсутствуют
- Никаких React-ошибок / краша нет

**Через прямой URL `/edit`**: проверить невозможно — URL сбрасывается на
`#/personas` из-за 8344b598 ещё до проверки прав.

### 4. Регресс формы на холодном старте

- Задержка загрузки `/api/specialties/catalog` через перехват fetch на 5 с
  + переход pushState на `#/personas/specialties/executor/edit` →
  SpecialtyEditView отрисовывается с `catalog ?? []`, кнопка «Сохранить» в
  шапке, поля на месте, **`hasReactError: false`** (нет
  «Rendered more hooks»)
- F5 на `/edit` под admin: URL сбрасывается на `#/personas`, нет падения
  React. Через pushState SpecialtyEditView устойчив к пустому каталогу.

## Корень дефекта 8344b598

Файл `frontend/src/App.tsx`, строка 490: `if (!initialHash?.history) navReplace(seed)`.
В seed-объекте (строка 476) есть `screen`, `persona`, `note`, `chatId`, но
**нет `personaView`**. Когда URL `#/personas/specialties/executor` парсится
через `parseHash` (lib/nav.ts:109), он ставит `target.personaView = 'specialties'`,
но при первом монтировании App.tsx пересобирает seed без `personaView` и
вызывает `navReplace(seed)` — URL схлопывается в `#/personas`, а history.state
становится `{ screen: 'personas', persona: null }`.

`PersonasPage.tsx:106` инициализирует `specialtiesMode` через
`parseSpecialtiesHash() !== null`, но `parseSpecialtiesHash()` смотрит на
`location.hash`, который уже сброшен к моменту первого рендера React.

Рекомендация для починки: в `App.tsx:476-490` добавить
`if (initialHash?.screen === 'personas' && initialHash.personaView) seed.personaView = initialHash.personaView;`
(по образцу уже существующих `seed.persona` / `seed.chatId`).

## Что НЕ баг (проверено, работает)

- `parseSpecialtiesHash()` корректно разбирает `#/personas/specialties/executor/edit` →
  `{ roleKey: 'executor', viewMode: 'edit' }`
- `PersonasSpecialties.tsx:95-96` даунгрейдит `edit → role` под неадмином
- `PersonasSpecialties.tsx:168-176` рендерит `<SpecialtyEditView>` под админом
- `SpecialtyEditView.tsx:128` резолвит роль через `catalog ?? []`, не падает
- Через клик в нормальном flow весь раздел работает (витрина → визитка →
  форма)

## Что нужно разработчикам

1. Добавить в `seed` поле `personaView`, чтобы `App.tsx:490` не терял под-адрес
   specialties при F5.
2. После починки — обе карточки `8344b598` и `b74ff206` закроются
   автоматически (форма SpecialtyEditView и даунгрейд прав работают, как
   показано выше).

## Подтверждающий прогон (27.08.2026, после коммита `6870579d`, Вера)

Задача `b713add9-bd1d-4af6-9317-b82d5def109a`. Стенд: `master` HEAD
`6870579d`, Vite dev `:5173`, бэкенд `:5000`. Прогон через Playwright
(`mcp__plugin_playwright_playwright__*`) в браузере на дев-стенде.

### TL;DR

Шесть из семи пунктов пройдены, починка specialties работает. **Пункт 6
(часть «`#/personas/{id}/automation` — её проактивность») — регресс, который
не покрыт коммитом `6870579d`.** Карточки `8344b598` и `b74ff206` не закрываю,
описываю шаги воспроизведения ниже.

### 1. Прямой URL `#/personas/specialties/executor` (admin) — ✅

| Шаг | Ожидание | Факт |
|-----|----------|------|
| `goto("#/personas/specialties/executor")` под admin | Визитка «Исполнитель (универсальный)» | Визитка с настройками, секциями промпта, привязками, кнопкой «Редактировать» |
| `location.reload()` на этом адресе | Визитка остаётся, URL сохраняется | URL → `#/personas/specialties/executor`, визитка |

Скриншоты: `.cc-attachments/specialties-recheck/01-direct-executor.png`,
`.cc-attachments/specialties-recheck/01b-f5-executor.png`.

### 2. Прямой URL `#/personas/specialties/coordinator` (admin) — ✅

`location.reload()` после navigate → визитка «Координатор», URL сохранился.
Скриншот: `.cc-attachments/specialties-recheck/02-coordinator.png`.

### 3. Прямой URL `#/personas/specialties/executor/edit` (admin) — ✅

Полная форма SpecialtyEditView с кнопками «Отмена»/«Сохранить» (disabled,
пока правок нет), секции Доступ / Инструменты / Секции промпта / Привязки /
Модели по уровням / Уровень по умолчанию / Пресеты. URL сохранился.
Скриншот: `.cc-attachments/specialties-recheck/03-admin-edit.png`.

### 4. Прямой URL `#/personas/specialties/executor/edit` (sandboxer, role=user) — ✅

Даунгрейд viewMode `'edit' → 'role'` сработал (`PersonasSpecialties.tsx:95-96`).
Визитка в read-only, кнопок «Редактировать»/«Сохранить»/«Отмена» нет,
URL `#/personas/specialties/executor/edit` сохранён, падения нет.
Скриншот: `.cc-attachments/specialties-recheck/04-nonadmin-edit.png`.

Также проверила под sandboxer прямой URL `#/personas/specialties/executor` —
визитка «Исполнитель» в read-only без кнопки редактирования, список персон
ограничен одной записью («Личный помощник Ассистент»). Скриншот:
`.cc-attachments/specialties-recheck/04a-nonadmin-executor.png`.

### 5. Прямой URL `#/personas/specialties` без роли — ✅

Под sandboxer: список ролей с переключателем «Показать все роли каталога»,
виден «Координатор» с пометкой «1 персона работает по общим настройкам».
URL сохранился. Скриншот: `.cc-attachments/specialties-recheck/05-list.png`.

### 6. Соседние диплинки — ❌ частично

**`#/personas/{id}` (admin)** — ✅. Карточка персоны «Тестировщик» открылась,
все секции на месте, URL сохранился.
Скриншот: `.cc-attachments/specialties-recheck/06a-persona-card.png`.

**`#/personas/{id}/automation` (admin)** — ❌ **регресс**.

Шаги воспроизведения:
1. Залогиниться под admin (admin/12345).
2. Перейти напрямую на `http://localhost:5173/#/personas/b40886f0-c732-438e-8b05-fd53e6c6556e/automation`
   (id — Тестировщик Тестовый).
3. Выполнить полную перезагрузку страницы (`location.reload()`).
4. Подождать 5 с.

Ожидание: визитка персоны «Тестировщик» с активной вкладкой «Проактивность»
(под-адрес `/automation` → `pendingView === 'automation'`).

Факт: **URL сбрасывается на `#/personas/b40886f0-c732-438e-8b05-fd53e6c6556e`
(без `/automation`)**. Карточка персоны «Тестировщик» открыта, но ни одна из
вкладок (Профиль / Умения / Проактивность / Память / Задачи) не подсвечена
как активная — контент вкладки «Профиль» отображается по умолчанию.

Корень: `lib/nav.ts:25-53`, функция `toHash` для `screen === 'personas'`
обрабатывает только `personaView === 'specialties'` (строка 33), для
`'automation'` отдельной ветки нет:

```
case 'personas': {
  if (s.personaView === 'specialties') return '#/personas/specialties';
  return s.persona ? `#/personas/${encodeURIComponent(s.persona)}` : '#/personas';
}
```

App.tsx:485 пробрасывает `initialHash.personaView` в seed
(`if (... && initialHash.personaView) seed.personaView = initialHash.personaView`),
то есть `'automation'` тоже попадает в seed — но `navReplace(seed)` зовёт
`toHash(seed)`, который обрезает URL до `#/personas/{id}` без `/automation`.
На следующем рендере `parseHash` уже не видит `personaView`, и
`PersonasPage.consume()` (через `t.personaView === 'automation'`) не
выставляет `pendingView`.

Скриншоты: `.cc-attachments/specialties-recheck/06b-automation-FAIL.png`,
`.cc-attachments/specialties-recheck/06c-automation-regression.png`.

### 7. Мобильная раскладка 360 CSS — ✅

| Адрес | Результат |
|-------|-----------|
| `#/personas/specialties/executor` | Визитка с кнопкой «Редактировать», все секции на месте, URL сохранился |
| `#/personas/specialties/executor/edit` (admin) | SpecialtyEditView с «Отмена»/«Сохранить», Доступ / Инструменты / Секции промпта / Привязки / Модели по уровням / Уровень по умолчанию / Пресеты |

Скриншоты: `.cc-attachments/specialties-recheck/07a-mobile-executor.png`,
`.cc-attachments/specialties-recheck/07b-mobile-edit.png`.

### Заключение

- Шесть пунктов (1–5, 7) подтверждают: коммит `6870579d fix(specialties):
  не терять personaView при первом монтировании` закрывает
  первопричину `8344b598` (сброс URL на общий раздел персон при F5 на
  `#/personas/specialties[/...]`). SpecialtyEditView и даунгрейд прав под
  неадмином работают как в прошлом прогоне.
- Пункт 6 (automation) открывает **отдельный регресс в `toHash`**, который
  существовал до коммита `6870579d` и не покрыт им.
- Карточки `8344b598-e6f6-47a9-93dc-95d7d1697acc` и
  `b74ff206-6188-4bda-9bec-edca5a9624de` **не закрываю**: в задаче явное
  требование «все семь пунктов пройдены», а automation проваливается.
  Рекомендация для фикса: расширить `toHash` в `lib/nav.ts:32-35`, чтобы
  `personaView === 'automation'` возвращал `#/personas/{id}/automation`,
  и зеркально — `parseHash` уже это умеет.

## Подтверждающий прогон v2 (27.08.2026, после коммита `95f5f5a7`, Вера)

Задача `f4cd77ef-7c82-40ab-ae32-a4666811c030` (волна 2 из 2). Стенд:
`master` HEAD `95f5f5a7` (`fix(nav): добавить ветку automation в toHash`),
Vite dev `:5173`, бэкенд `:5000` (PID 32088, не гасить). Прогон через
`mcp__plugin_playwright_playwright__*` в чистой вкладке и после `F5`.

### TL;DR

Все семь пунктов прошли. Карточки `8344b598-e6f6-47a9-93dc-95d7d1697acc`
(«Прямой URL ролей специальностей сбрасывает на общий раздел персон») и
`b74ff206-6188-4bda-9bec-edca5a9624de` («Экран настройки роли /edit не
показывает форму редактирования») закрываю: коммиты `6870579d`
(`fix(specialties): не терять personaView при первом монтировании`,
задача 1) и `95f5f5a7` (`fix(nav): добавить ветку automation в toHash`,
задача 2) покрывают обе первопричины.

### 1. `#/personas/specialties/executor` (admin) — ✅

| Шаг | Ожидание | Факт |
|-----|----------|------|
| Чистая вкладка `goto(...)` | Визитка «Исполнитель (универсальный)» | URL сохранился, визитка с настройками, секциями промпта, привязками, кнопкой «Редактировать», 2 персоны в блоке «Кто работает по этой роли» |
| `location.reload()` | URL + визитка | URL `#/personas/specialties/executor`, state `{screen:'personas', personaView:'specialties'}` сохранился |

Скриншоты: `.cc-attachments/specialties-recheck-v2/01-direct-executor.png`,
`.cc-attachments/specialties-recheck-v2/01b-f5-executor.png`.

### 2. `#/personas/specialties/coordinator` (admin) — ✅

| Шаг | Ожидание | Факт |
|-----|----------|------|
| Чистая вкладка `goto(...)` | Визитка «Координатор» | URL сохранился, визитка |
| `location.reload()` | URL + визитка | URL сохранился |

Скриншоты: `.cc-attachments/specialties-recheck-v2/02-coordinator.png`,
`.cc-attachments/specialties-recheck-v2/02b-f5-coordinator.png`.

### 3. `#/personas/specialties/executor/edit` (admin) — ✅

Полная форма SpecialtyEditView с кнопками «Отмена»/«Сохранить» (disabled,
пока правок нет), секции Доступ / Инструменты / Секции промпта / Привязки /
Модели по уровням / Уровень по умолчанию / Пресеты. URL сохранился и после
`location.reload()`.
Скриншоты: `.cc-attachments/specialties-recheck-v2/03-admin-edit.png`,
`.cc-attachments/specialties-recheck-v2/03b-f5-admin-edit.png`.

### 4. `#/personas/specialties/executor/edit` (sandboxer, role=user) — ✅

Даунгрейд viewMode `'edit' → 'role'` сработал (`PersonasSpecialties.tsx:95-96`).
Визитка «Исполнитель (универсальный)» в read-only, кнопок
«Редактировать»/«Сохранить»/«Отмена» нет, URL
`#/personas/specialties/executor/edit` сохранился и после F5, падения нет.
Блок «Кто работает по этой роли» пуст — у sandboxer нет доступа к тем же
персонам, что и у admin.
Скриншот: `.cc-attachments/specialties-recheck-v2/04-nonadmin-edit.png`.

Дополнительно проверила:
- `#/personas/specialties/executor` под sandboxer → визитка «Исполнитель»
  в read-only (скриншот `04a-nonadmin-executor.png`).
- `#/personas/specialties/coordinator/edit` под sandboxer → визитка
  «Координатор» в read-only (скриншот `04b-nonadmin-coordinator-edit.png`).

### 5. `#/personas/specialties` без роли — ✅

Под sandboxer: список ролей с переключателем «Показать все роли каталога»,
видны роли «Исполнитель» и «Координатор». URL сохранился и после F5.
Скриншоты: `.cc-attachments/specialties-recheck-v2/05-list-roles.png`,
`.cc-attachments/specialties-recheck-v2/05b-f5-list-roles.png`.

### 6. `#/personas/{id}/automation` (admin) — ✅

| Шаг | Ожидание | Факт |
|-----|----------|------|
| Чистая вкладка `goto("#/personas/b40886f0-c732-438e-8b05-fd53e6c6556e/automation")` | Визитка персоны «Тестировщик» с активной вкладкой «Проактивность» | URL сохранился, в шапке вкладок видна «Проактивность», контент: «нет правил» + «Подключи первое правило» + кнопки «Создать правило»/«✨ Подобрать автоматически»/«✨ Создать по промпту» |
| `location.reload()` | URL + вкладка «Проактивность» активна | URL `#/personas/b40886f0-.../automation`, state `{screen:'personas', persona:'b40886f0-...', personaView:'automation'}` сохранился, вкладка «Проактивность» активна, контент тот же |

Скриншоты: `.cc-attachments/specialties-recheck-v2/06-automation-clean.png`,
`.cc-attachments/specialties-recheck-v2/06b-final-f5-automation.png`.

Также `#/personas/{id}` без `/automation` под admin → визитка персоны
«Тестировщик» со вкладкой «Профиль» (скриншот
`specialties-recheck-v2/06a-persona-card.png`).

### 7. Мобильная раскладка 360 CSS — ✅

| Адрес | Результат |
|-------|-----------|
| `#/personas/specialties/executor` (admin) | Визитка с кнопкой «Редактировать», все секции на месте, URL сохранился |
| `#/personas/specialties/executor/edit` (admin) | SpecialtyEditView с «Отмена»/«Сохранить», Доступ / Инструменты / Секции промпта / Привязки / Модели по уровням / Уровень по умолчанию / Пресеты, URL сохранился |

Скриншоты: `.cc-attachments/specialties-recheck-v2/07a-mobile-executor.png`,
`.cc-attachments/specialties-recheck-v2/07a-f5-mobile-executor.png`,
`.cc-attachments/specialties-recheck-v2/07b-mobile-edit.png`,
`.cc-attachments/specialties-recheck-v2/07b-f5-mobile-edit.png`.

### Состояние среды (известное)

`dotnet build` в основной копии падает не по коду, а по блокировке
`ClaudeHomeServer.dll` живым инстансом продукта (PID 32088, запущен из
`backend\ClaudeHomeServer\bin\Debug\net10.0\`). Процесс не гасить —
Vite dev на :5173 и бэкенд :5000 обслуживают страницу с актуальным
HEAD `95f5f5a7`. Это блокирует запуск `dotnet build`/`dotnet test`,
но не влияет на UI-прогон через Playwright.

### Заключение

- Семь из семи пунктов прошли, регресс автоматизации (пункт 6) закрыт
  коммитом `95f5f5a7`.
- Карточки `8344b598-e6f6-47a9-93dc-95d7d1697acc` и
  `b74ff206-6188-4bda-9bec-edca5a9624de` **закрываю**: оба дефекта
  описаны коммитами `6870579d` и `95f5f5a7`.