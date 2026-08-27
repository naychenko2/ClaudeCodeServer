# QA: ручная проверка раздела «Специальности» (волна 4, 26.08.2026)

**Ветка:** `feature/specialties-personas-parity` (worktree `C:/Sources/aihome-specialties`), HEAD `1f5dc4d0` + незакоммиченные правки волн 1–3.
**Стенд:** отдельный QA-инстанс этого worktree — бэкенд `:5090` (`dotnet bin/Debug/net10.0/ClaudeHomeServer.dll`, `ASPNETCORE_ENVIRONMENT=Development`), фронт `vite dev :5173` с `BACKEND_PORT=5090`. Данные — копия dev-стора в `C:/ClaudeData/qa-specialties` (боевой и общий dev-стенд не тронуты).
**Учётки:** `admin` (роль admin, флаг `specialty-prompt-sections` включён), `qa-user` (роль user, заведена через `POST /api/users`).
**Инструмент:** Playwright (chromium, headless), скрипты — `C:/ClaudeData/qa-specialties/pw/*.js`, снимки — [shots/](shots).
**Ширины:** десктоп 1440×900, мобила 360×800 (`isMobile`, `hasTouch`).

> **Важно про воспроизводимость.** Во время прогона файлы раздела продолжали
> править параллельные исполнители волны (последняя запись в `frontend/src` —
> 20:23). Все дефекты ниже перепроверены **после** этой отсечки, на текущем
> состоянии дерева. Один найденный ранее дефект (значок роли в форме правки
> рисовался глифом мимо `RoleAvatar`) к моменту перепроверки уже исправлен
> соседним исполнителем и в список не попал.

---

## Итог по критериям готовности

| # | Критерий | Результат |
|---|---|---|
| 1 | Список специальностей прокручивается до конца | **пройдено** (десктоп и 360) |
| 2 | Переключателя слоёв и выбора пользователя нигде нет | **пройдено** |
| 3 | Визитка роли показывает реальных персон с этой ролью | **пройдено** |
| 4 | Переход из визитки в персону работает | **пройдено** |
| 5 | Пустое состояние «Пока никто не работает по этой роли» | **пройдено** |
| 6 | Админ из визитки уходит в правку | **пройдено** |
| 7 | Меняются доступ, инструменты, секции промпта, привязки, модели и пресеты | **пройдено** (пресеты — read-only по замыслу формы) |
| 8 | Изменения сохраняются и видны после перезагрузки | **частично** — сохраняются и видны при повторном заходе, но F5 на под-адресе выкидывает из раздела → **Д-2** |
| 9 | У неадмина кнопки «Редактировать» нет | **пройдено** |
| 10 | Прямой хеш `.../edit` неадмина не пускает | **пройдено формально** — не пускает никого, включая админа → **Д-2** |
| 11 | `PUT` на бэке отдаёт отказ неадмину | **пройдено** (403) |
| 12 | Раздел работает без файлов аватарок (фолбэк на глиф) | **пройдено** |
| — | Побочно: форма персоны после снятия слоёв | **БЛОКЕР** → **Д-1** |

**Блокирующих дефектов не осталось.** Д-1 закрыт фронтендером и перепроверен —
см. раздел «Перепрогон после правок» ниже; там же состояние остальных трёх.
Открытыми остаются **Д-2** (средний) и **Д-4** (мелкий).

> Таблица выше и разделы «Что проверено» / «Дефекты» описывают **первый** прогон
> — то состояние, в котором дефекты были найдены. Итог после правок — в разделе
> «Перепрогон после правок».

---

## Что проверено и как

### 1. Список специальностей и его скролл — пройдено

Вход админом → «Персоны» → вкладка «Специальности» (`#/personas/specialties`).
Каталог из 14 ролей, бейдж «14 из 14», все роли в секции с правилами.

* Десктоп 1440: скроллер раздела `scrollHeight 979 / clientHeight 746`,
  прокрутка доехала до `scrollTop = 233 = max`, хвостовая подсказка
  «Список ролей задан продуктом…» видна.
  Снимки: [desktop-1440-list.png](shots/desktop-1440-list.png),
  [desktop-1440-list-bottom.png](shots/desktop-1440-list-bottom.png).
* Мобила 360: `scrollHeight 2328 / clientHeight 708`, доехали до `1620 = max`,
  хвост виден, горизонтального выхода нет (`scrollWidth == clientWidth == 360`).
  Снимки: [mobile-360-list.png](shots/mobile-360-list.png),
  [mobile-360-list-bottom.png](shots/mobile-360-list-bottom.png).
* Тумблер «Показать все роли каталога» переключается; секции `rest` пустой —
  у всех 14 ролей на стенде заданы правила, поэтому добавлять нечего.

### 2. Слои и выбор пользователя — пройдено

На всех трёх экранах (витрина, визитка, форма правки) искал подписи
«Общие настройки», «Мои настройки», «Общий слой», «Личный слой», «слой»,
«Для пользователя», «Выбрать пользователя», «Владелец настроек»,
«Персональные настройки» — **ноль совпадений**. `<select>` на витрине и визитке
— **0**; в форме правки — **2**, и оба принадлежат строке привязки по умолчанию
(«Тип умения» и «Режим»), к слоям отношения не имеют.

На бэкенде запись сузилась до одного маршрута: `PUT /api/specialties/settings`
(бывшая точка записи личного слоя) отвечает **405 Method Not Allowed**,
`GET /api/specialties/settings` отдаёт только `version / maxSubstitutions /
global / presets` — слоёв `owner`/`user` в ответе больше нет.

### 3–4. Визитка роли: персоны и переход в персону — пройдено

Роль «Координатор» (`#/personas/specialties/coordinator`): срез «Кто работает по
этой роли» показал **2 персоны** — «Ассистент» и «Тимур», обе действительно с
`specialty = coordinator`; счётчик «2 персоны», у каждой пометка
«не хватает типовых умений: 2» и кнопка «Применить типовые».
Клик по строке персоны увёл на `#/personas/ebd53cb5-…` — карточка персоны
открылась.
Снимки: [desktop-1440-role-coordinator.png](shots/desktop-1440-role-coordinator.png),
[desktop-1440-persona-from-role.png](shots/desktop-1440-persona-from-role.png),
[mobile-360-role.png](shots/mobile-360-role.png),
[mobile-360-role-bottom.png](shots/mobile-360-role-bottom.png).

### 5. Пустое состояние — пройдено

Роль «Дизайнер» (персон нет): счётчик «пусто», блок с подписью
**«Пока никто не работает по этой роли»**.
Снимок: [desktop-1440-role-empty.png](shots/desktop-1440-role-empty.png).

### 6–8. Правка роли админом и стойкость — пройдено (кроме F5, см. Д-2)

Путь: визитка «Дизайнер» → «Редактировать» → `#/personas/specialties/designer/edit`.
Изменил в одном заходе: **доступ** `Полный → Только чтение`, **инструменты** —
снял «Веб», **привязки по умолчанию** — добавил умение с условием
«QA-проверка волны 4», **уровень по умолчанию** — «Средняя». Сохранил.

Записалось в общий слой (`C:/ClaudeData/qa-specialties/specialty-settings.json`,
узел `Global.Specialties.designer`):

```json
{ "Access": "readOnly", "Tools": ["tasks","notes"], "DisallowedTools": null,
  "DefaultBindings": [ { "Type": "knowledge", "Mode": "auto",
                         "Condition": "QA-проверка волны 4", "SkillName": null } ],
  "TierStrong": "preset:8a1f2c4e-…", "TierMedium": "preset:7b3d4e5f-…",
  "TierWeak": "preset:6c4d5e6f-…", "DefaultTier": "medium" }
```

В **свежей сессии браузера** (новый контекст, повторный вход) визитка роли
показывает «ДОСТУП: Только чтение», «ИНСТРУМЕНТЫ: Задачи · Заметки», строку
«Знание · QA-проверка волны 4 (по событию)»; в форме правки выбран уровень
«Средняя». Снимки:
[desktop-1440-role-designer-persisted.png](shots/desktop-1440-role-designer-persisted.png),
[mobile-360-edit-default-tier.png](shots/mobile-360-edit-default-tier.png).

**Секции промпта** (роль «Аналитик»): «Задать свой текст» → набрал свой текст →
«Сохранить» → на визитке роли текст показывается вместо типового.
Снимки: [desktop-1440-edit-section-saved.png](shots/desktop-1440-edit-section-saved.png),
[desktop-1440-role-section-own-text.png](shots/desktop-1440-role-section-own-text.png).

**Модели по уровням** правятся через `RoutePicker` (ячейки заполнены пресетами
Dev-стенда), **пресеты** на форме — read-only список по замыслу (правятся в
отдельной вкладке); это соответствует комментарию в `SpecialtyEditView.tsx`.

### 9–11. Права неадмина — пройдено

Учётка `qa-user` (роль `user`):

* Витрина и визитка открываются, содержимое общее — [desktop-1440-user-list.png](shots/desktop-1440-user-list.png), [desktop-1440-user-role.png](shots/desktop-1440-user-role.png).
* Кнопки «Редактировать» на визитке **нет** (0 совпадений).
* Прямой хеш `#/personas/specialties/designer/edit` (сменой `location.hash` из
  открытого раздела) — форма правки не открылась: экран остался визиткой,
  кнопки «Сохранить» нет, маркера `ключ: designer` нет —
  [desktop-1440-user-edit-hash.png](shots/desktop-1440-user-edit-hash.png).
* Бэкенд, вызовы с JWT `qa-user`:

  | Запрос | Ответ |
  |---|---|
  | `PUT /api/specialties/settings/global` | **403** |
  | `PUT /api/specialties/settings/fallback/global` | **403** |
  | `POST /api/specialties/settings/reset/global` | **403** |
  | `PUT /api/specialties/settings` (старый маршрут) | **405** |
  | `GET /api/specialties/settings` | 200 |

### 12. Раздел без файлов аватарок — пройдено

Временно убрал `frontend/src/assets/specialties/` (14 jpg) целиком, перезагрузил
раздел: витрина отрисовала все **14 карточек ролей**, внутри карточек
`<img> = 0`, `<svg> = 14` — фолбэк на lucide-глиф в круге цвета роли;
визитка роли — два глифа (тулбар 40 и hero 80), ошибок в консоли нет.
Каталог, скролл, переходы работают.
Снимки: [desktop-1440-no-avatars-list.png](shots/desktop-1440-no-avatars-list.png),
[desktop-1440-no-avatars-role.png](shots/desktop-1440-no-avatars-role.png).
После проверки папка возвращена на место (14 файлов на месте).

С аватарками все три экрана берут картинку из одного источника: карточка списка
— 1 `<img>`, визитка — `/specialties/analyst.jpg` в тулбаре и в hero, форма
правки — та же картинка в hero ([icon-01-list-card.png](shots/icon-01-list-card.png),
[icon-02-role.png](shots/icon-02-role.png)).

---

## Дефекты

### Д-1 (БЛОКЕР). Форма персоны падает: `Cannot read properties of undefined (reading 'defaultSpecialty')`

**Где:** `frontend/src/features/personas/PersonaForm.tsx:289-291`
**Кому:** исполнителю «снятие слоёв настроек на фронте»

**Шаги воспроизведения**

1. Войти админом (`admin`), открыть раздел «Персоны».
2. Кликнуть **любую** персону в левом списке (проверено на «Аналитик/Метида»).
3. Нажать «Редактировать».
4. **Факт:** вместо формы — экран `ErrorBoundary` «Что-то пошло не так».
   **Ожидание:** открывается форма правки персоны.

Снимки: [crash-01-after-persona-click.png](shots/crash-01-after-persona-click.png),
[crash-02-after-edit-click.png](shots/crash-02-after-edit-click.png),
[crash-03-details.png](shots/crash-03-details.png).

**Стек (из «Подробности ошибки»)**

```
TypeError: Cannot read properties of undefined (reading 'defaultSpecialty')
    at personaCellPlaceholder (PersonaForm.tsx:216:37)
    at PersonaForm (PersonaForm.tsx:1867)
    ...
    at PersonaStudio (PersonasPage.tsx:949)
```

**Разбор.** Волна убрала слои: `SpecialtiesController.GetSettings` теперь отдаёт
только `version / maxSubstitutions / global / presets`. `PersonaForm` продолжает
читать снятый слой:

```ts
const rec = specialty !== 'none' && specSettings
  ? effectiveSpecialtyRecord(specSettings.global, specSettings.owner, specialty) : null;
const defRec = specSettings?.owner.defaultSpecialty ?? specSettings?.global.defaultSpecialty ?? null;
```

Строка 291: `?.` обрывается на `specSettings`, а не на `owner` — обращение к
`undefined.defaultSpecialty` бросает исключение. Строка 290 передаёт `undefined`
вторым аргументом. Ветка безусловная, поэтому падает **любая** персона.

**Почему не поймала сборка.** `frontend/src/types/index.ts:2238-2250` всё ещё
объявляет у `SpecialtySettingsResponse` поля снятых слоёв, причём `owner` —
обязательным:

```ts
global: SpecialtySettingsLayer;
owner: SpecialtySettingsLayer;      // ← слоя больше нет в ответе
user?: SpecialtySettingsLayer;      // ← и этого
userId?: string;                    // ← и этого
```

`npx tsc -b` в worktree проходит **без единой ошибки** — тип врёт, компилятор
молчит, падает рантайм.

**Что это регрессия волны, а не старый баг:** в `master` (`C:/Sources/ClaudeCodeServer`)
тот же `PersonaForm.tsx:289-291` работает, потому что там
`SpecialtiesController.cs:116-118` отдаёт `global`, `user` и `owner`.

**Предлагаемое лечение:** привести тип в соответствие ответу бэка (убрать
`owner`/`user`/`userId`) — компилятор сам покажет оставшиеся места, — и переписать
`personaCellPlaceholder` на один общий слой. Заодно проверить
`lib/presets.ts:261` (`merged.owner?.presets`) — там опциональная цепочка, не
падает, но читает несуществующий слой.

---

### Д-2 (средний). Прямой хеш и перезагрузка на под-адресах раздела выкидывают в витрину «Персоны»

**Где:** `frontend/src/features/personas/PersonasPage.tsx` (инициализация `specialtiesMode`)
**Кому:** исполнителю навигации раздела

**Шаги воспроизведения**

1. Войти любым пользователем.
2. Открыть в адресной строке `#/personas/specialties` (или
   `#/personas/specialties/analyst`, или `#/personas/specialties/analyst/edit`).
3. Нажать F5.
4. **Факт:** показывается витрина «Персоны», а URL переписывается в
   `#/personas` — под-адрес теряется.
   **Ожидание:** открывается запрошенный экран раздела.

Проверено на всех трёх под-адресах и на обеих ролях (admin и user) — результат
одинаковый: `isPersonasHub = true`, `urlNow = #/personas`.
Снимки: [deeplink-admin-list.png](shots/deeplink-admin-list.png),
[deeplink-admin-role.png](shots/deeplink-admin-role.png),
[deeplink-admin-edit.png](shots/deeplink-admin-edit.png),
[deeplink-user-edit.png](shots/deeplink-user-edit.png),
[desktop-1440-after-reload.png](shots/desktop-1440-after-reload.png).

**Разбор.** `PersonasPage` читает под-адрес из хеша при монтировании
(`parseSpecialtiesHash()` → `specialtyRoleKey`, `specialtyViewMode`), но сам
признак «мы в разделе» инициализируется константой:

```ts
const [specialtiesMode, setSpecialtiesMode] = useState(false);
```

Хеш на него не влияет, поэтому центр рисует хаб, а разобранный `roleKey`
остаётся неиспользованным.

**Почему это важно, хотя внешне похоже на защиту.** Критерий «прямой хеш
`.../edit` неадмина не пускает» формально выполняется, но по неверной причине:
не пускает никого, включая админа. Реальная защита (даунгрейд `edit → role` при
`!isAdmin` в `PersonasSpecialties` и `[Authorize(Roles = "admin")]` на бэке)
проверена отдельно и работает — см. пункты 9–11. Кроме того, страдает критерий
«изменения видны после перезагрузки»: после F5 приходится заходить в раздел
заново.

---

### Д-3 (мелкий, тексты). В форме правки роли строка превью моделей дублирует префикс и обрывает кавычку

**Где:** `frontend/src/features/personas/SpecialtyEditView.tsx`, `TierPreviewRow`
(`formatEffectiveLine(preview, { tierText: 'уровень «${TIER_TITLE[tier]}' })` и
конкатенация `'Сейчас пойдёт: ' + text`)

**Шаги воспроизведения**

1. Войти админом → «Специальности» → любая роль → «Редактировать».
2. Прокрутить до блока «Модели по уровням».
3. **Факт** (под каждой из трёх ячеек):
   `Сейчас пойдёт: Сейчас пойдёт: MiniMax-M3[1m] · уровень «Сильная, модель — от специальности`
   **Ожидание:** префикс один, кавычка закрыта:
   `Сейчас пойдёт: MiniMax-M3[1m] · уровень «Сильная», модель — от специальности`.

`formatEffectiveLine` уже возвращает строку с префиксом, а `TierPreviewRow`
добавляет свой; в `tierText` не хватает закрывающей `»`. Воспроизводится на
всех ролях и обеих ширинах.
Снимки: [desktop-1440-edit-tier-preview.png](shots/desktop-1440-edit-tier-preview.png),
[mobile-360-edit-default-tier.png](shots/mobile-360-edit-default-tier.png).

---

### Д-4 (мелкий, мобила). На 360 CSS переключатель «Уровень по умолчанию» вылезает за поле формы

**Где:** `frontend/src/features/personas/SpecialtyEditView.tsx`, секция «Уровень по
умолчанию» (`PillSwitch fill` с четырьмя опциями)

**Шаги воспроизведения**

1. Ширина окна 360 CSS, войти админом.
2. «Специальности» → любая роль → «Редактировать» → прокрутить до «Уровень по
   умолчанию».
3. **Факт:** последняя опция «Слабая» упирается в правую кромку экрана без
   отступа и обрезается; трёхопционный «Доступ» в той же форме помещается.

Измерено (роль «Координатор», 360×800):

| Элемент | left | right |
|---|---|---|
| трек переключателя | 32 | **328** |
| опция «Не задано» | 35 | 121.4 |
| опция «Сильная» | 124.4 | 199.9 |
| опция «Средняя» | 202.9 | 278.8 |
| опция **«Слабая»** | 281.8 | **349.4** |
| для сравнения, «Свой список» в «Доступ» | 226.9 | 327.5 |

Опции выходят за трек на **≈21 px**. `document.scrollWidth` при этом остаётся
360 (трек `overflow-x: visible`, страница не расширяется), поэтому автоматическая
проверка «нет горизонтального скролла» дефект не ловит — видно только глазами.
Снимок: [mobile-360-edit-tier-coordinator.png](shots/mobile-360-edit-tier-coordinator.png).

---

## Замечания без статуса дефекта

* **Две колонки визитки на десктопе не расходятся при открытом списке персон.**
  При 1440 центр раздела ~591 px, а колонки визитки просят `380 + 300 + 28`,
  поэтому «Кто работает по этой роли» переносится под основную колонку.
  Раскладка соответствует коду (`flexWrap: wrap`) и не ломается, но задуманного
  «в две колонки» на типовом десктопе не видно — вопрос к дизайну, не дефект.
* **Длинные подписи ролей в карточках витрины обрезаются** («Исполните…
  (бэкенд)»). Полное имя есть в `title` карточки, ширина трека минимальная
  150 px — поведение штатное.
* **`ERR_CONNECTION_RESET` в консоли** ловился, когда падал мой dev-сервер vite;
  к продукту отношения не имеет.
* **Битый аватар персоны «Виктор»** на снимках — артефакт QA-стенда: я копировал
  в него JSON-сторы без папки с картинками аватаров. Не дефект.

---

## Перепрогон после правок (26.08.2026, 21:0x)

Дефекты Д-1…Д-4 были отданы фронтендеру задачей `6146aad0`. После её закрытия
дождался пятиминутного затишья правок в `frontend/src` и `backend` и перепрогнал
всё заново на том же стенде.

**Сборка:** `npx tsc -b` — код возврата 0, `npm run lint:design` — код возврата 0.

> По ходу правок сборка какое-то время была красной:
> `SlotsTab.tsx(121,29)` и `(572,26)` — `Cannot find name 'loadUserLayer' /
> 'getUserLayer'` (хвост снятия слоёв). К моменту перепрогона починено.

| Дефект | Состояние | Чем подтверждено |
|---|---|---|
| **Д-1** блокер, крах формы персоны | **закрыт** | тот же путь (персона из левого списка → «Редактировать») больше не падает: `crashed = false`, форма открывается — [crash-02-after-edit-click.png](shots/crash-02-after-edit-click.png). Обращения к слою `owner` нет ни в `PersonaForm.tsx`, ни в `SpecialtySettingsResponse`. |
| **Д-2** прямой хеш и F5 | **открыт** | перепроверено на трёх под-адресах и обеих ролях — по-прежнему витрина «Персоны», URL переписан в `#/personas` |
| **Д-3** дубль префикса и кавычка | **закрыт** | `Сейчас пойдёт: MiniMax-M3[1m] · уровень «Сильная», модель — от специальности` — префикс один, кавычка закрыта |
| **Д-4** переключатель уровня на 360 | **открыт** | геометрия не изменилась: трек 32..328, опции 35..349.4 |

**Контрольная проверка критериев после правок — регрессий нет:** вкладка
открывает раздел (`#/personas/specialties`), список прокручивается до хвоста на
1440 и на 360, визитка «Координатор» показывает 2 персоны и уводит в персону,
пустое состояние «Пока никто не работает по этой роли» на месте, слоёв и выбора
пользователя нет ни на одном экране (`words: []`, `selects: 0/0/2`), сохранённые
правки роли «Дизайнер» на месте («Только чтение», «Задачи · Заметки», привязка
«Знание · QA-проверка волны 4»), у неадмина кнопки нет и прямой хеш `.../edit`
его не пускает, форма правки на 360 без горизонтального выхода.

### Что осталось открытым

**Д-2.** Правка `specialtiesMode` (теперь `useState(() => parseSpecialtiesHash() !== null)`)
нужная, но недостаточная: URL успевает схлопнуться **до** монтирования
`PersonasPage`. Сидирование адреса в `App.tsx` (`if (!initialHash?.history)
navReplace(seed)`, ~строка 493) кладёт `NavSnapshot` без `personaView`, а
`toHash` (`lib/nav.ts:32-34`) сводит `{screen:'personas'}` к `#/personas`.
Плюс `lib/nav.ts:104-112` знает только `#/personas/specialties` — под-адреса
`{roleKey}` и `/edit` в `HashTarget` не попадают вовсе. Чинить надо там, а не в
`PersonasPage`.

**Д-4.** `fill={!isMobile}` геометрию не изменил: опции и до правки шли
естественной шириной (86.4 / 75.5 / 75.9 / 67.6 px). Дело не в `fill` — четырём
опциям нужно ~317 px, а трек формы на 360 CSS даёт 296. Нужно другое решение:
перенос на две строки, сокращение подписей или `overflow-x` у самого трека.

## Как поднять этот стенд заново

```powershell
# 1. Данные (копия dev-стора, чтобы не драться с общим стендом)
New-Item -ItemType Directory -Force C:\ClaudeData\qa-specialties
'users.json','personas.json','specialty-settings.json','projects.json','app-settings.json',
'local-actions.json','model-fallback.json','groups.json','jwt-secret.txt','image-generation.json',
'instance-id.txt' | ForEach-Object { Copy-Item "C:\ClaudeData\dev\$_" "C:\ClaudeData\qa-specialties\$_" -Force }

# 2. backend/ClaudeHomeServer/appsettings.Local.json в worktree:
#    Urls http://localhost:5090, DataPath C:/ClaudeData/qa-specialties/projects.json

# 3. Бэкенд и фронт
cd C:\Sources\aihome-specialties\backend; dotnet build ClaudeHomeServer/ClaudeHomeServer.csproj
$env:ASPNETCORE_ENVIRONMENT='Development'
Start-Process dotnet 'bin/Debug/net10.0/ClaudeHomeServer.dll' -WorkingDirectory C:\Sources\aihome-specialties\backend\ClaudeHomeServer -WindowStyle Hidden
$env:BACKEND_PORT='5090'
Start-Process node './node_modules/vite/bin/vite.js --port 5173' -WorkingDirectory C:\Sources\aihome-specialties\frontend -WindowStyle Hidden
```

Скрипты прогонов лежат в `C:/ClaudeData/qa-specialties/pw/` (`01`…`14`);
`lib.js` — вход, навигация и снимки.
