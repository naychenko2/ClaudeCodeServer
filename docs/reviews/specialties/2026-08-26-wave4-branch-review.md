# Код-ревью ветки feature/specialties-personas-parity (волна 4)

**Дата:** 26.08.2026
**Ревьюер:** Глеб
**Объект:** весь диф ветки `feature/specialties-personas-parity` в worktree `../aihome-specialties`
(незакоммиченное рабочее дерево: 33 файла, +2539/−4714, плюс untracked: `TestSpecialtyStore.cs`,
`ADR-012-specialty-settings-single-layer.md`, `RoleAvatar.tsx`, аватарки, мокапы).
**Решение, против которого не спорим:** [ADR-012](../../adr/ADR-012-specialty-settings-single-layer.md) —
один глобальный слой, admin-only запись, миграция FormatVersion 4→5.

## Что проверено

- `SpecialtySettingsStore.cs` целиком: миграция v≤4→v5 (`MigrateToV5`, `MergeIntoGlobal`,
  `MergeRecord`, `MergeSections`, `BackupSourceFile`), резолвы, сброс, валидация, персистентность.
- `SpecialtiesController.cs` целиком: состав маршрутов, admin-гейты, reset/owner.
- `PresetStore.cs`, `ModelsController` (гейты rename/delete пресетов), `GlmModelAliasMigration`
  (единственный оставшийся писатель стора вне контроллера).
- Фронт: `lib/specialties.ts`, `lib/presets.ts`, `lib/api.ts`, `types/index.ts`,
  `PersonasSpecialties/SpecialtyListView/SpecialtyRoleView/SpecialtyEditView`,
  `RoleAvatar/RolePresetsBlock/RolePeopleSlice`, `PersonasPage` (диплинки), `useSpecialtiesCoverage`,
  остатки `features/specialties/specialRules/`, живые вызовы легаси-путей в ModelsSpend.
- Тесты миграции (`SpecialtySettingsStoreTests`, 5 сценариев v4→v5), `TestSpecialtyStore`.
- Прогоны (см. «Верификация»).

## Вердикт по постановке

| Пункт постановки | Статус |
|---|---|
| Корректность миграции 4→5, необратимость потери Owners/Users | ✅ реализовано по ADR, тесты покрывают; нюансы m7/m10 |
| Полнота admin-only на записи (бэк обязателен) | ✅ все точки записи закрыты ролью или удалены |
| Остатки снятой механики слоёв на фронте | ❌ в файлах вне волны остались живые вызовы удалённых эндпоинтов (M2) |
| Сырой hex в .tsx | ✅ нет (lint:design зелёный, ручная проверка чиста) |
| Безопасность чтения глобального слоя | ✅ секретов нет, ADR обосновал; reset/owner ограничен своими персонами |
| Качество удаления мёртвого кода | ⚠️ висящих импортов нет, но остались мёртвые ветки и устаревшие комментарии (m4–m6, m8) |
| Соглашения проекта | ⚠️ в основном да; самодельные контролы (m3) |

## Critical

### C1. SpecialtyEditView: useMemo после условного return — краш React при догрузке каталога
`frontend/src/features/personas/SpecialtyEditView.tsx:267` — ранний `return` при `!role`;
`frontend/src/features/personas/SpecialtyEditView.tsx:290` — `useMemo(editLayerForBlock)` ниже него.

Сценарий: экран правки монтируется раньше каталога специальностей (`useSpecialtyCatalog` ещё
грузится — холодный вход/диплинк `#/personas/specialties/{key}/edit`). Первый рендер: `role = null`
→ ранний return, хук `useMemo` не вызывается. Каталог приезжает → рендер вызывает его →
«Rendered more hooks than during the previous render» → падение React-дерева раздела.
Сейчас сценарий маскирован дефектом M5 (диплинк вообще не включает режим), при быстрой навигации
изнутри приложения тоже достижимо.

Рекомендация: перенести `editLayerForBlock` (и всё, что между return и JSX-логикой) выше раннего
return — все данные для useMemo уже есть до него.

## Major

### M1. Аватарки ролей не попадают в git — `.gitignore:81` (`*.jpg`) игнорирует все 14 файлов
`frontend/src/assets/specialties/*.jpg` существуют в worktree, но `git status` их не видит:
`git check-ignore -v` → `.gitignore:81 *.jpg`. `import.meta.glob` в `lib/specialties.ts` собирает
их на машине автора, но коммит/CI/прод/любой чистый чекаут получат пустой `ROLE_AVATAR_BY_KEY` →
`hasRoleAvatar()` = false у всех ролей → раздел молча покажет lucide-фолбэк. Цель волны «комплект
аватарок ролей» не доедет никуда, кроме машины исполнителя.

Рекомендация: `!frontend/src/assets/specialties/*.jpg` в `.gitignore` и закоммитить файлы
(или перевести в `.webp`/`.png`, если `*.jpg` игнор намеренно широкый — тогда осознанно
задокументировать).

### M2. Бэкенд ветки удалил эндпоинты, которыми продолжают пользоваться живые экраны «Поставщиков моделей»
Бэкенд (по ADR) удалил `PUT /settings`, `PUT /settings/owner`, `GET/PUT /settings/user/{id}`,
`PUT /settings/fallback/owner`, `reset/user`. Фронт волны их выпилил только у себя; ModelsSpend
(вне дифа) остался на старых вызовах — в собранной ветке это сломанные экраны:

- `frontend/src/features/modelsSpend/ChainsTab.tsx:147` — не-админ двигает ползунок бюджета →
  `PUT /specialties/settings/fallback/owner` → **404**, catch молча откатывает ползунок.
- `ChainsTab.tsx:66`, `SlotsTab.tsx:122` — `loadUserLayer(contextUserId)` → `GET /settings/user/{id}`
  → **404** при каждом выборе пользователя админом в модалке.
- `frontend/src/components/PresetOptions.tsx:79` (`scope ?? 'owner'`) и `ChainsTab.tsx:182`
  (у не-админа черновик цепочки создаётся в scope `'owner'`) → `doSave` шлёт `PUT /settings/global`
  → **403**; `PresetOptions.savePreset` глотает ошибку пустым `catch(() => {})` — кнопка «не работает»
  без единого сообщения.
- `lib/api.ts` `reset('user')` / `getUserLayer` — указатели на несуществующие маршруты.

ADR-012 описывает потерю личных пресетов/бюджета как осознанную, но «осознанно убрать» ≠
«продавать в UI действие, гарантированно падающее». Пункт постановки «отсутствие остатков снятой
механики слоёв на фронте» для ModelsSpend не выполнен.

Рекомендация: раз модели правки ModelsSpend вынесены «отдельной волной», эта волна обязана ехать
СРАЗУ за текущей (до выката на прод), либо в текущей ветке закрыть хотя бы 404-пути: убрать
радиокнопку «Только для мне»/ползунок личного бюджета у не-админа и loadUserLayer-эффекты.
Минимум — заменить молчаливые отказы на видимые сообщения.

### M3. lib/presets.ts: doSave пишет всегда в global, а applySaved/rollbackLocal — по исходному scope
`frontend/src/lib/presets.ts:282-307`: оптимистичная правка применяется в `global`
(`applyLocal('global', …)`), PUT идёт в `settings/global`, но затем:

- `applySaved(scope, …)` при `scope='owner'` (админ собирает цепочку через RoutePicker/PresetOptions,
  где дефолтный scope — `'owner'`) кладёт ответ в `_settings.owner`, а `mergeSavedLayer`
  (`presets.ts:257-266`) пересобирает `_settings.presets` из `owner.presets + global.presets` —
  **каждый пресет задаивается в UI до перезагрузки**;
- `rollbackLocal('user')` (`presets.ts:337-349`) дёргает удалённый `GET /settings/user/{id}` и не
  откатывает оптимистично применённый global-слой.

Сценарий M3a: админ в «Моделях» → RoutePicker → «Собрать цепочку…» → сохранить → список пресетов
удваивается. Рекомендация: в `doSave` фиксировать коллапс — `applySaved('global', …)` и
`rollbackLocal('global', …)` независимо от входного scope (или честно сузить WriteScope до 'global').

### M4. Визитка роли помечает общие секции промпта как «Ваше»
`frontend/src/features/personas/SpecialtyRoleView.tsx:298-299` передаёт
`editLayer={layerSettings} globalLayer={layerSettings}` — один и тот же слой в owner- и
global-позиции `effectivePromptSection` (`lib/specialties.ts:271-316`). Любая секция, заданная в
глобальном слое, резолвится с источником `'owner'` → бейдж **«Ваше»** и подпись
**«Сейчас пойдёт: ваш текст»** (`RolePresetsBlock.tsx:45-58`).

Сценарий: не-админ (визитка доступна любому аутентифицированному) открывает роль — тексты,
настроенные администратором, представлены как его личные. В форме правки бейдж «Ваше» тоже
неверен по смыслу: админ правит общий слой.

Рекомендация: в view-режиме передавать `editLayer={null}`. Тексты бейджей/подписей
«Ваше»/«ваш текст» для однослойной модели — из лексики снятых слоёв; ADR разрешает снять бейджи
отдельной задачей, но врать они не должны уже сейчас.

### M5. Диплинк `#/personas/specialties…` при перезагрузке не включает режим «Специальности»
`frontend/src/features/personas/PersonasPage.tsx:101` — `specialtiesMode` стартует `false`;
`initialSpec` (`:105`) инициализирует только `specialtyRoleKey/specialtyViewMode`, но режим никто
не включает: `consume()` (`:154-182`) разбирает лишь `t.personaId`, specialties-ветки в нём нет;
popstate на первичной загрузке не срабатывает.

Сценарий: F5 или прямая ссылка на `#/personas/specialties` / `/{roleKey}` / `/{roleKey}/edit` —
открывается общий раздел персон с пустым состоянием. Дефект зафиксирован ещё в ревью волны 5
(24.08, находка 5) и в этой ветке не закрыт.

Рекомендация: в `consume()` обработать `t.personaView === 'specialties'` (+ диплинк-парсер
`parseSpecialtiesHash`) так же, как это делает `onPop`.

## Minor

1. `SpecialtyEditView.tsx:851` — незакрытая ёлочка в tierText: `уровень «${TIER_TITLE[tier]}`
   без `»` → «Сейчас пойдёт: … уровень «Сильная, …».
2. `SpecialtyEditView.tsx:556-574` (HeroSection) рисует глиф в круге, а не `RoleAvatar` — форма
   правки единственная из трёх экранов без аватарки (рассинхрон с визиткой и списком).
3. `SpecialtyListView.tsx:274-287` — самодельный switch из `<span role="switch">` при живом
   `Toggle` из ui-кита (импортирован в соседнем RolePresetsBlock); `SpecialtyEditView.tsx:543-553`
   — самодельные кнопки тулбара при импортированном `Button`. Гайд: контролы — только из
   `components/ui/`.
4. Устаревшие комментарии, противоречащие v5: `SpecialtiesController.cs:51-57` («owner → user →
   global»), `PresetStore.cs:3-9` (три слоя как живые), `api.ts:424` («ими пользуются …
   SpecialRulesTab» — файл удалён), `RolePresetsBlock.tsx:241` (LayerSwitch удалён).
5. `types/index.ts:2244` — `SpecialtySettingsResponse.owner` объявлен обязательным, бэкенд его
   больше не отдаёт. Рантайм-читателей нет (проверено), но тип врёт — стоит сделать опциональным.
6. `useSpecialtiesCoverage` + `specialRules/model.ts:pickStartScope` — для не-админа считает охват
   по `settings['owner']` (undefined после v5) → бейдж охвата у не-админа никогда не появится;
   вся логика выбора стартового слоя — мёртвая. Оставшийся `features/specialties/specialRules/`
   (5 файлов) живёт только ради `coverageOf/tripleSummary` — кандидат на чистку в хвостовой задаче.
7. `SpecialtySettingsStore.MergeIntoGlobal:891` — `InsertRange(0, fresh)`: при 2+ админах с
   пресетами пресеты позднего админа встают в списке РАНЬШЕ раннего (порядок между админами ADR
   не специфицирован; на боевых данных админ один, на резолв не влияет — только порядок списка).
8. `SpecialtySettingsStore.BackupSourceFile:948-961` — провал копии `.v4.bak` логируется warning'ом,
   после чего миграция всё равно переписывает файл. По ADR страховка обязательна («разбор „куда
   делась настройка“ вообще был возможен»); хотя бы Error-уровень.
9. `SpecialtyEditView` превью «Сейчас пойдёт» под ячейками уровней резолвится сервером по
   сохранённому слою — при правке черновика строка продолжает говорить про старое значение
   (подпись «Наследуется/Сейчас пойдёт» верна только до первой правки).
10. `PersonasSpecialties.tsx:173-175` — мёртвый реэкспорт `SpecialtySettingsResponse`;
    `:78-85` — заглушка `_tierModels` «пока не нужны», хотя TierModelsSection уже считает превью.

## Что сделано хорошо

- Миграция v4→v5 — образцово: гранулярность влития повторяет прежние семантики наследования
  (права целиком, секции посекционно, пресеты конкатенацией с дедупом), «первый админ в users.json»
  с честной проверкой по фактическим данным прода, страховочная копия, идемпотентность по версии.
  Тесты закрывают все заявленные в ADR сценарии, включая конфликт двух админов.
- Контроллер: все точки записи закрыты `[Authorize(Roles = "admin")]` или удалены; `reset/owner`
  честно сужен до персон вызывающего по `UserId` из токена; PresetStore через ModelsController
  не-админу отдаёт Forbid (scope всегда Global после v5 — проверено).
- LayerSwitch и слоевой UI выпилены из файлов волны без висящих импортов; `specialties.mobile.css`
  и `layout.ts` удалены чисто.
- Сырого hex в новых .tsx нет; цвет/иконка ролей идут с бэка с фолбэками, не ломающими UI.

## Верификация (прогнано в worktree, до правок по находкам)

- `cd frontend; npx tsc -b` — **0 ошибок** (exit 0)
- `cd frontend; npm run lint:design` — **чисто** (exit 0)
- `cd backend; dotnet build` — **успешно** (exit 0)
- `cd backend; dotnet test --filter "Category!=Dns"` — **5812 passed / 3 skipped / 1 failed**.
  Единственный провал — `GitServiceTests.WriteDossiersBranch_Батчинг_Даёт_То_Же_Дерево_Что_ПофайловыйПлюминг`:
  «unable to write file .git/objects/…: Function not implemented» — средовой сбой записи git-объекта
  в temp на этой машине; `GitServiceTests.cs` и вся зона dossiers в дифе ветки не тронуты, к
  специальностям отношения не имеет. Все тесты специальностей и миграции — зелёные.

## Требуется до мержа

C1, M1–M5 — правками исполнителей соответствующих файлов:
C1, M4 — фронт волны (SpecialtyEditView/SpecialtyRoleView); M1 — владелец .gitignore/ассетов;
M2, M3 — согласование с волной ModelsSpend (M3 — файл lib/presets.ts, в дифе ветки); M5 —
PersonasPage (в дифе ветки).

---

# Закрытие находок (26.08.2026, Кира)

Задача `db86fd24`: C1 и M1–M5. По каждой находке — что изменено и как проверено.
Ветка не сливалась и не пушилась.

## C1 — useMemo после условного return (закрыто)

**Изменено.** `frontend/src/features/personas/SpecialtyEditView.tsx`: `useMemo(editLayerForBlock)`
поднят из-под раннего `return` при `!role` наверх, к остальным хукам (сразу после `canSave`);
на прежнем месте остался только не-хук `applySectionReducer`. Все данные для мемо (`roleKey`,
`recDraft`, `layerSettings`) готовы задолго до раннего возврата, поведение не изменилось.
В комментарии к хуку зафиксировано, почему он обязан стоять выше возвратов.

**Как проверено — воспроизведением в реальном браузере.** Собран временный стенд (страница Vite
+ Playwright/Chromium): `SpecialtyEditView` монтируется с ПУСТЫМ каталогом (`role = null` →
ранний return), через 100 мс каталог приезжает с ролью — ровно сценарий холодного входа по
прямому хешу. Ошибка ловится `getDerivedStateFromError` и читается из страницы.

| Версия кода | Результат прогона |
|---|---|
| дефектная (хук возвращён под `return`) | `CRASH: Rendered more hooks than during the previous render.` |
| исправленная | `OK — краха нет` (`state = catalog-arrived`, ошибок хуков нет) |

Стенд после прогона удалён — в дифе его нет. Дополнительно: `npx eslint` на файле — на дефектной
версии `react-hooks/rules-of-hooks`: «React Hook "useMemo" is called conditionally» (error), на
исправленной правило молчит. Прочие хуки компонента (`grep` по `use*`) все стоят выше
единственного раннего возврата.

## M1 — аватарки ролей под маской `*.jpg` (закрыто)

**Изменено.** `.gitignore`: точечное исключение с `!` сразу после маски `*.jpg` (строки 82–84)
с пояснением, зачем комплект лежит в репозитории. Формат файлов не менялся.

**Как проверено.**
`git status --porcelain -uall frontend/src/assets/specialties/` — все **14** файлов видны как
добавляемые (`?? …/analyst.jpg` … `?? …/tester.jpg`);
`git check-ignore frontend/src/assets/specialties/*.jpg` — пустой вывод, `exit=1` (не
игнорируется ни один). С ключом `-v` видно, что срабатывает именно строка-исключение
`.gitignore:84`.

## M2 — живые вызовы удалённых эндпоинтов в «Поставщиках моделей» (закрыто)

Бэкенд ветки оставил ровно: `GET settings`, `PUT settings/global`, `PUT settings/fallback/global`,
`GET/POST settings/reset/global`, `GET/POST settings/reset/owner`. Всё, что звало снятое,
переведено на общий слой.

**Изменено.**
- `features/modelsSpend/ChainsTab.tsx` — снят эффект `loadUserLayer(contextUserId)` (это был
  `GET settings/user/{id}` → 404 на каждый выбор пользователя); `ChainScope` сведён к `'global'`,
  из создания/переименования/удаления/правки шагов убраны scope- и userId-ветки и гейт
  `hasUserLayer`; бюджет подмен пишется только в `fallback/global`, у не-админа вместо ползунка —
  значение текстом (прежде ползунок звал снятый `fallback/owner`, ловил 404 и молча откатывался);
  кнопка «Новая цепочка» и правка шагов — только у админа (иначе гарантированный 403); из диалога
  создания убран выбор слоя, `ScopeBadge` всегда «Для всех»; пустое состояние для не-админа честно
  говорит, что цепочки собирает администратор.
- `features/modelsSpend/SlotsTab.tsx` — снят тот же эффект `loadUserLayer` и чтение `getUserLayer`
  в `commitSavePreset`; запись шагов и «Сохранить как цепочку» идут в `'global'`;
  `canSavePreset = isAdmin`; `presetScope` у `RoutePicker` теперь всегда `"global"` (прежний
  `undefined` у не-админа уводил черновик в снятый owner-слой).
- `components/PresetOptions.tsx` — `targetScope` жёстко `'global'` вместо `scope ?? 'owner'`;
  убраны ветки `hasUserLayer`/`getUserLayer`; **молчаливый `catch(() => {})` заменён на видимый
  тост** с текстом ошибки (прежде кнопка «Сохранить» у не-админа просто ничего не делала).
- `features/modelsSpend/ModelsSpendModal.tsx` — обёртка `onSaveLayer` больше не подмешивает
  `contextUserId`, пишет в `'global'`.
- `lib/api.ts` — `resetPreview`/`reset` сужены до `'owner' | 'global'` (scope `user` и проброс
  `userId` удалены), `setMaxSubstitutions` — до `'global'`; шапка секции переписана: перечислено,
  каких путей здесь быть НЕ должно.
- `lib/presets.ts` — `resetLayer` сужен до `'owner' | 'global'` без user-ветки; из `rollbackLocal`
  убран поход в `getUserLayer`.

**Граница, проведённая осознанно.** Сам канал user-слоёв в `lib/presets.ts`
(`loadUserLayer`/`getUserLayer`/`hasUserLayer`/`useUserLayer`/`commitUserLayer`/…) и определение
`api.specialties.getUserLayer` оставлены на месте: из приложения их больше не зовёт никто, но на
них висят четыре собственных юнит-набора (`__tests__/userLayers`, `presets`, `presets-user-gate`,
`presets-queue`), и снос канала — это снос этих наборов, то есть отдельная хвостовая чистка, а не
«убрать вызовы». Оба места помечены в коде как МЁРТВЫЕ после ADR-012 с запретом добавлять новые
вызовы.

**Как проверено.** `grep` по `src` (без тестов) на строки `settings/user/`,
`settings/fallback/owner`, `settings/owner`, `reset/user` — совпадений в исполняемом коде нет,
остались только комментарии-предупреждения и помеченное мёртвым определение
`api.specialties.getUserLayer`. Все фактические точки записи (`onSaveLayer`/`saveLayer`) передают
`'global'` — проверено перечислением call-site'ов. `npx tsc -b` — 0 ошибок (сужение типов `scope`
заодно делает возврат к снятым слоям ошибкой компиляции).
**Ограничение:** ручной прогон экрана «Модели и расход» в браузере под живым логином выполнить не
удалось — вход по паре логин/пароль, credentials дев-стенда у исполнителя нет. Проверка сделана
статически (типы + перечисление вызовов) и по ветвлениям UI.

## M3 — doSave пишет в global, applySaved/rollbackLocal — по исходному scope (закрыто)

**Изменено.** `lib/presets.ts:doSave` — коллапс на общий слой зафиксирован во всех трёх точках:
`applyLocal('global', …)` (было и раньше), `applySaved('global', …)` и `rollbackLocal('global', …)`
вместо входного `scope`. Входной аргумент помечен `void scope`, и в комментарии описано, что
именно ломалось: при `scope='owner'` ответ ложился в `_settings.owner`, а `mergeSavedLayer`
пересобирал `_settings.presets` из `owner + global` и удваивал список; при `scope='user'` откат
дёргал удалённый `GET settings/user/{id}`.

**Как проверено.** Источник дубля устранён структурно: `_settings.owner` больше не записывается
ни на успехе, ни на откате, поэтому `mergeSavedLayer` собирает пресеты из одного `global`.
Сценарий M3a (RoutePicker → «Собрать цепочку…») закрыт и со второй стороны: `PresetOptions` теперь
сам передаёт `'global'`. Отдельно проверено, что правка **не** добавила падений тестов:
`presets-queue` + `presets-user-gate` дают ровно 9 провалов и с моей версией `doSave`, и с
откаченной — цифра не меняется (см. «Красное на фронте» ниже).

## M4 — «Ваше»/«ваш текст» на общих секциях (закрыто)

**Изменено.**
- `features/personas/SpecialtyRoleView.tsx` — визитка передаёт `editLayer={null}` (было
  `editLayer={layerSettings}` в паре с тем же `globalLayer`), поэтому `effectivePromptSection`
  резолвит источник как `'global'`, а не `'owner'`.
- `components/specialties/RolePresetsBlock.tsx` — из карточки режима **view** убран признак
  «своё»: `OverrideBadge` («Свой текст»/«Типовой текст») больше не рисуется, всё нужное несёт
  бейдж источника «Из кода» / «Общее». Заодно подписи слоя `owner` приведены к честным для
  однослойной модели: `SRC_LABEL.owner` = «Общее», `SRC_NOTE.owner` = «Сейчас пойдёт: текст из
  общего слоя (настройки администратора)» — в режиме edit `owner` теперь означает «правимый слой»,
  а он общий, так что личной пометки не остаётся и там.

**Как проверено.** Прослежен резолв: в view-режиме `editLayer=null` → ветка `[ownerLayer,'owner']`
в `effectivePromptSection` пропускается (`if (!layer) continue`) → любая заданная админом секция
приходит с `enabledSource/textSource = 'global'` → бейдж «Общее» и подпись про общий слой. В
edit-режиме `editLayer` остался черновиком (иначе `editable = canEdit && hasOwnOverride` сломал бы
правку), но слово «Ваше» из лексики убрано целиком — `grep` по «Ваше» и «ваш текст» в
`frontend/src` пуст. `npx tsc -b` и `npm run lint:design` зелёные.

## M5 — диплинк `#/personas/specialties…` при F5 (закрыто)

**Изменено.** `features/personas/PersonasPage.tsx`:
- `specialtiesMode` инициализируется из хеша — `useState(() => parseSpecialtiesHash() !== null)`
  (было `useState(false)`); `specialtyRoleKey`/`specialtyViewMode` уже брались из `initialSpec`;
- **`mobileView` тоже инициализируется из хеша** (`'card'` при specialties-диплинке): на мобиле
  раздел рисуется только в режиме карточки (`mobileView === 'card' && (hasContent ||
  specialtiesMode)`), и без этого F5 по прямому хешу открывал список персон вместо запрошенного
  экрана — то есть дефект оставался ровно в той же формулировке;
- в `onPop` вход в specialties ставит `mobileView='card'` (как `openSpecialties`), а не `'list'`.

**Как проверено.** Разобрана цепочка первичного рендера для всех трёх форм адреса
(`#/personas/specialties`, `/{roleKey}`, `/{roleKey}/edit`): `parseSpecialtiesHash` разбирает
каждую (регексп проверен на всех трёх), инициализаторы `useState` дают `specialtiesMode=true`,
нужный `roleKey`/`viewMode` и `mobileView='card'`; `consume()` в этой ветке ничего не перебивает
(`t.personaId` пуст). Ветка рендера мобильного тела больше не отсекает раздел.

## Minor

Не брались (по постановке — на усмотрение, слияние не блокируют), кроме закрывшихся попутно:
устаревшие комментарии про три слоя в `api.ts` и `presets.ts` (m4 частично) и самодельный выбор
слоя в диалоге создания цепочки.

## Верификация (после правок)

| Команда | Итог |
|---|---|
| `cd frontend; npx tsc -b` | **0 ошибок**, exit 0 |
| `cd frontend; npm run lint:design` | **чисто**, exit 0 |
| `cd backend; dotnet build` | **успешно**, 0 предупреждений, 0 ошибок |
| `cd backend; dotnet test --filter "Category!=Dns"` | **5813 passed / 3 skipped / 0 failed** |

По бэкенду: на первом прогоне было 2 провала (`WorkflowMetaResolverTests` и соседний) с
`ObjectDisposedException: EventLogInternal` — падение Windows-логгера при отписке, не ассерт;
повторный чистый прогон зелёный целиком. `GitServiceTests` в этот раз прошли.

### Красное на фронте — НЕ от этих правок (нужно решение по ветке)

`npm test` (в перечень приёмки не входит) даёт **17 провалов в 3 файлах**, и все они —
последствия самой ветки, а не задачи `db86fd24`:

- `src/features/specialties/__tests__/write-contract.test.ts` — 8 провалов: тесты требуют
  `withDisplay`/`withoutDisplay` из `lib/specialties` и их импорт в `SpecialtyEditView`, а ветка
  удалила эти функции (`grep withDisplay src/lib/specialties.ts` → 0). Ни один из этих файлов
  задачей не правился.
- `src/lib/__tests__/presets-queue.test.ts` (6) и `presets-user-gate.test.ts` (3) — проверяют
  ключевание очереди PUT по `scope+userId` и гейт записи в user-слой, то есть ровно ту механику,
  которую ветка свернула в один общий слой ещё до этой задачи. Доказательство непричастности: с
  откаченным M3-изменением `doSave` те же 2 файла дают **те же 9 провалов** — цифра не меняется.

Решение (дочистить тесты вместе с мёртвым каналом user-слоёв или переписать их под один слой) — за
координатором/волной ModelsSpend; в рамках этой задачи трогать их было бы выходом за владение.
