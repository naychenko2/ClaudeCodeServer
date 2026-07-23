# Визуальная спецификация: иерархия чатов в списке (ТЗ для реализации)

Автор: Майя (UI/UX). Основано на реальном коде: `ChatCard.tsx`, `ChatList.tsx`, `FilterBar.tsx`,
`ChatOriginBadge.tsx`, `ListDateDivider.tsx`, `StatusIndicator.tsx`, `chatGroups.ts`, токены
`design.ts`, темы `theme.css`.

> **Ключ раскладки:** у `ChatCard` стоит `overflow:hidden` + `position:relative`, активная карточка
> рисует акцентную полосу внутри себя (`left:0,width:4`). Поэтому connector и chevron рисуются **НЕ в
> карточке, а в обёртке-строке** `ChatTreeRow` — иначе линию обрежет `overflow` карточки. `ChatCard`
> не меняется ни на строку.

## 1. Обёртка строки дерева
```
<ChatTreeRow depth=d hasChildren collapsed isLastChild activeInPath>
   [ spine-stub ]   ← вертикаль под своим chevron (если развёрнут и есть дети)
   [ spine-seg  ]   ← вертикаль-связь к родителю (если d≥1)
   [ elbow      ]   ← горизонталь-ввод в карточку (если d≥1)
   [ chevron    ]   ← контрол сворачивания (если hasChildren)
   <ChatCard …/>    ← без изменений
</ChatTreeRow>
```
Обёртка: `position:relative; padding-left: OFFSET(d)`. Линии — абсолютные `div` слева от карточки,
её `overflow:hidden` их не трогает.

## 2. Отступ вложенности
| Токен | Десктоп | Мобайл | Смысл |
|---|---|---|---|
| `STEP` | 14 | 14 | сдвиг на уровень |
| `GUTTER` | 20 | 28 | колонка под chevron/elbow слева от карточки |

- `OFFSET(d) = min(d,6) * STEP` — `padding-left` обёртки (клапм глубины 6).
- `cardLeftX(d) = min(d,6)*STEP + GUTTER` — левый край карточки.
- `spineX(d) = min(d,6)*STEP + GUTTER/2` — ось (spine) уровня.

Шаг маленький намеренно: связь несёт connector-линия, а не величина отступа → на глубине 3-4 ширина
сайдбара (~300px) остаётся жить (на d4 заголовку ~128px, ellipsis уже есть). Все корни (включая
бездетные/сироты) тоже получают `GUTTER` — единое выравнивание колонки, не пометка сироты.

## 3. Connector-линии — обязательны
Аргумент дизайнера: карточки высокие и «шумные» (статус, заголовок, превью, бейджи, лицо-подложка);
14px-отступ между ними читается как случайный сдвиг, а не вложенность. Бейдж «Задача» говорит *что*
это исполнитель, но не *чей* — линия отвечает мгновенно.

Геометрия (одна формула на всех уровнях — гарантия, что «не разъезжается»):
- **elbow** (ребёнок d≥1): `y=elbowY`, от `x=spineX(d-1)` до `x=cardLeftX(d)` — заводим в левый край карточки.
- **spine-seg** (ребёнок d≥1): `x=spineX(d-1)`, `top:0..bottom:0`; у ПОСЛЕДНЕГО ребёнка — только до `elbowY` (хвост не свисает).
- **spine-stub** (развёрнутый родитель с детьми): `x=spineX(d)`, от `elbowY+7` до `bottom:0`.

`spineX(d-1)` одинаков в stub родителя и seg детей → ось всегда на одном X на любой глубине.
`elbowY` — центр ПЕРВОЙ строки карточки (не всей, высота пляшет от бейджей): десктоп ≈20, мобайл ≈23.

- **Толщина:** 1px (как `ListDateDivider` и рамки — hairline-консистентность).
- **Цвет:** без нового токена — `C.divider` (обычная), `C.accent` (ветка к активному чату).
  Light `#DDD4C4`/`#D97757`, Dark `#454037`/`#E38A6A`. Линии `z-index` ниже карточки (она непрозрачная).
- **Рекомендация:** подсветить весь путь корень→активный чат в `C.accent` (флаг `activeInPath`) — сильный wayfinding.
- Эскалация, если бледно в light: завести `--c-tree-line` (light `#D0C6B4`), но стартуем с `divider`.

## 4. Collapse-контрол (chevron)
- Позиция: в `GUTTER`-колонке слева от карточки, центр по `spineX(d)`, вертикаль по `elbowY`.
  Снаружи карточки → ноль конфликтов со статус-точкой, акцентной полосой, hover-действиями, подложкой.
- Вид: lucide `ChevronRight`, `size 14`, `strokeWidth 2.2`; развёрнут — поворот 90° (▾), свёрнут ▸, `transform .15s`.
- Состояния: покой `C.textMuted`, hover `C.textSecondary`, активный путь `C.accent`.
- Tap-зона: min `32×32`, `onClick` → `stopPropagation` (не открыть чат).
- Память: `Set<string>` свёрнутых id в localStorage, ключ `cc_chat_tree_collapsed:global` (и `:{projectId}`). Дефолт — всё развёрнуто.

## 5. Счётчик детей у свёрнутого
Показывать (фан-аут делегирования). Микро-бейдж у свёрнутого chevron в его верхне-правом углу, в пределах `GUTTER`:
круг 14px `R.max`, фон `C.accentLight`, текст `C.accent` `FONT.mono` `FS.xs`; считаем ПРЯМЫХ детей.
Развёрнутый — бейджа нет.

## 6. Тумблер «Плоский / Иерархия»
- Место: `FilterBar`, справа. Корень бара → `flex; justify-content:space-between`: слева «⌕ Фильтр ·N», справа сегмент.
- Вид: 2-сегментный pill в НЕЙТРАЛЬНОМ стиле `TB.pill*` (не accent — accent занят фильтрами; режим ≠ фильтр).
  - track `TB.pillTrack (C.bgSelected)`, `R.pill (9)`, `padding:2`;
  - активный thumb `TB.pillThumbBg (C.bgWhite)` + `SHADOW.thumb`, текст `C.textHeading` `600`;
  - неактивный `transparent`, текст `C.textMuted`; сегмент `padding:4px 10px` `FS.sm` `FONT.sans`.
- Иконки: `List` (Плоский) / `ListTree` (Иерархия), `size 14`. Мобайл — только иконки (подпись в title/aria-label).
- Состояние: localStorage `cc_chat_view:global` / `:{projectId}`, значения `'flat'|'tree'`, дефолт `'flat'`, проп `view` в `ChatList`.

## 7. Тёмная/светлая тема
Всё на CSS-переменных, спец-значений не вводим. Внимание: connector `C.divider` виден в обеих; активная ветка
`C.accent` в dark ярче (норм); счётчик `accent на accentLight`; тумблер — в dark контраст thumb/track мал, поэтому
`SHADOW.thumb` обязательна (поднимает активный сегмент).

## 8. Мобильная раскладка
Ширина списка ~360px (больше десктоп-сайдбара) — отступы менее болезненны. `STEP=14`, `GUTTER=28` (крупнее tap).
`elbowY≈23`. Chevron у родителей виден ВСЕГДА (в gutter, по `hasChildren`, не по hover) — collapse без наведения.
Tap chevron ≥32×32 `stopPropagation`; тап по остальной карточке открывает чат. На d4/360px заголовку ~190px.

## 9. Empty-state
- Чатов нет → существующее «Пока нет чатов. Начните новый.».
- Все корни скрыты фильтрами → существующее «Все чаты скрыты фильтрами» (origin к детям не применяется, к корням — да).
- Иерархия вкл., но связей нет → тонкая строка сверху: «⋔ Пока нет вложенных чатов — здесь появятся исполнители
  делегированных задач.» (`FS.sm`, `C.textMuted`, `padding:10px 8px`; только при `view==='tree'` и нуле связей).

## 10. Порядок рендера
- `view==='tree'`: `groupChats`/`ListDateDivider` НЕ вызываем (даты off). Дерево из массива по `parentSessionId`, рекурсивно.
- Корни — по MAX активности поддерева; дети внутри родителя — по своему `updatedAt` desc.
- Pinned-корни поднимаем вверх среди корней (без заголовка «Закреплённые» — групп-заголовки в дереве off).
- Свёрнутый узел: поддерево НЕ рендерим вовсе (не прячем) — иначе держит высоту.

## Таблица констант
```
STEP         = 14
GUTTER       = 20 / 28            // десктоп / мобайл
OFFSET(d)    = min(d,6)*STEP      // padding-left обёртки
spineX(d)    = min(d,6)*STEP + GUTTER/2
cardLeftX(d) = min(d,6)*STEP + GUTTER
elbowY       = 20 / 23            // десктоп / мобайл (центр первой строки карточки)
connector    = 1px, C.divider (активный путь → C.accent)
chevron      = ChevronRight 14, sw 2.2, C.textMuted (hover C.textSecondary)
countBadge   = ⌀14, R.max, bg C.accentLight, text C.accent, FONT.mono, FS.xs
toggle       = TB.pill* (track bgSelected, thumb bgWhite + SHADOW.thumb, R.pill)
persist      = cc_chat_view:{scope} ('flat'|'tree'), cc_chat_tree_collapsed:{scope} (Set<id>)
```

Спорные точки (решено): connector-линии обязательны; новый цветовой токен НЕ нужен (`C.divider` + `C.accent`);
тумблер — нейтральный TB-сегмент, не accent; chevron строго снаружи карточки (единственное место без конфликтов,
позволяет не трогать `ChatCard`).
