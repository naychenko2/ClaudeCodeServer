import type { CSSProperties, DragEvent, HTMLAttributes } from 'react';
import type { LucideIcon } from 'lucide-react';
import { C, ISLAND } from '../../lib/design';

// Направляющая-плейсхолдер места вставки при перетаскивании панелей — общая для
// обеих зон (RightPanelStack, LeftPanelStack).
//
// В покое занимает в потоке ровно `base` px (обычно 0 — панели при DnD не
// «дышат»), а дроп-зона рисуется absolute-оверлеем поверх зазора: штриховая
// линия, под курсором — сплошная акцентная.
//
// axis задаёт ориентацию: 'x' — вертикальная линия между колонками, 'y' —
// горизонтальная между панелями колонки. Раньше это были два почти дословных
// компонента (ColumnSep/RowSep), расходившиеся при каждой правке геометрии.

// Ширина/высота дроп-зоны при перетаскивании (только оверлей, в потоке места
// не занимает — иначе панели ужимались бы на время DnD)
export const SEP_HIT = 22;
// Толщина направляющей. Длина штрихов у dashed пропорциональна толщине границы,
// так что чем толще — тем крупнее штрихи.
// ВАЖНО: у направляющей в покое borderRadius обязан быть 0. Скругление на
// элементе, у которого content схлопнут в ноль (border-box + размер == толщине
// границы), браузер рисует дугой и штриховка вырождается в сплошную линию —
// именно из-за этого плейсхолдер выглядел сплошным.
const SEP_LINE = 2;
// Отступ вдоль ДЛИНЫ направляющей — чтобы она не упиралась в торцы панелей
const SEP_INSET = 8;
// Шаг штриховки направляющей: сам штрих и период (штрих + пробел). Частый мелкий
// пунктир читается как «здесь может встать», редкий крупный — как декоративная
// рамка; в короткой линии дока последнее особенно заметно.
const DASH_LEN = 3;
const DASH_STEP = 6;
// ЕДИНЫЙ зазор от кромки панели до направляющей. Все плейсхолдеры обязаны стоять
// на одном расстоянии, поэтому смещение считается формулой из base, а не задаётся
// вручную: у межколоночных зазор в потоке GAP, у крайних 0, у правого RAIL_GAP —
// без пересчёта они вставали бы на 3 / 7 / 1 px от панели соответственно.
//   sepShift(base) = SEP_CLEARANCE + SEP_LINE/2 - base/2
// Для base = GAP сдвиг нулевой: центр зазора и есть нужное место.
const SEP_CLEARANCE = 3;
// Экспортируется, потому что то же место вставки рисует и наведение на иконку
// рельсы (PanelZone): считать сдвиг там второй раз значило бы развести знаки —
// линия наведения уже успела уехать на 9px внутрь панели, пока формула жила здесь.
export const sepShift = (base: number) => SEP_CLEARANCE + SEP_LINE / 2 - base / 2;
// Приглушение направляющей в покое: она лишь намекает на возможные места вставки
// и не должна спорить с контентом панелей. Цвет остаётся C.textSecondary (он задаёт
// «характер» линии), гасится именно видимость — под курсором возвращается к 1,
// поэтому переход в акцентную сплошную читается тем заметнее, чем тише покой.
// Значение общее с рамкой большого места: тонкая линия и контур прямоугольника —
// один и тот же знак, и при 0.25 линия рядом с ним выглядела выцветшей.
const SEP_REST_OPACITY = 0.6;
// Большое место вставки в покое гасится слабее сплошного: у него есть подложка и
// иконка, и при 0.25 они превращались в грязное пятно вместо подсказки.
const FILL_REST_OPACITY = 0.6;

// Прямоугольник «сюда встанет панель» — общий знак для двух путей открытия:
// дроп перетаскиваемой панели и наведение на её иконку в рельсе. Вид один, чтобы
// одно и то же обещание не выглядело по-разному; отличается только мишень —
// у наведения её нет (boxProps пуст, сверху ставят pointerEvents: none).
export function PanelDropSpot({ over = false, icon: Icon, boxProps, style }: {
  over?: boolean;
  icon?: LucideIcon;
  boxProps?: Pick<HTMLAttributes<HTMLDivElement>, 'onDragOver' | 'onDragLeave' | 'onDrop'>;
  style?: CSSProperties;
}) {
  return (
    <div
      {...boxProps}
      style={{
        boxSizing: 'border-box',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        // Под курсором контур сплошной — как у линии, которая на дропе тоже
        // перестаёт быть штриховой: штрих означает «возможное место», сплошная —
        // «панель встанет сюда»
        border: `${SEP_LINE}px ${over ? 'solid' : 'dashed'} ${over ? C.accent : C.textSecondary}`,
        borderRadius: ISLAND.radius,
        // Место читается как «сюда встанет панель» — у него есть подложка, а не
        // только контур: под курсором акцентная, в покое — тон утопленных зон
        background: over ? C.accentMuted : C.bgInset,
        color: over ? C.accent : C.textMuted,
        opacity: over ? 1 : FILL_REST_OPACITY,
        transition: 'background 0.12s ease, border-color 0.12s ease, opacity 0.12s ease',
        ...style,
      }}
    >
      {Icon && <Icon size={26} strokeWidth={1.5} />}
    </div>
  );
}

// Направляющая-линия места вставки. Второй знак той же пары, что PanelDropSpot:
// прямоугольник обещает панель во всю свободную область, линия — что панель
// втиснется в этот стык. Общий и для дропа, и для наведения на иконку рельсы.
export function PanelDropLine({ axis, over = false, accent = false, shift = 0, inset = SEP_INSET }: {
  axis: 'x' | 'y';
  over?: boolean;
  // Штриховая, но акцентным цветом (а не приглушённым серым): наведение на кнопку
  // рельсы — точное «кликнешь — встанет сюда», поэтому линия контрастная, но
  // остаётся пунктиром «возможного места», а не сплошной «отпустишь-сюда» дропа.
  accent?: boolean;
  // Сдвиг от кромки панели наружу (крайние места вставки) — см. sepShift
  shift?: number;
  // Поля по торцам линии. Между панелями им нужен воздух, чтобы направляющая не
  // упиралась в их кромки; в 40px-рельсе те же 8px съедали бы половину длины.
  inset?: number;
}) {
  const vertical = axis === 'x';
  return (
    // Коридор вокруг линии. Поля вдоль её длины — чтобы направляющая не
    // упиралась в кромки панелей, а висела в зазоре с воздухом.
    <div style={{
      display: 'flex', boxSizing: 'content-box',
      transform: shift ? `translate${vertical ? 'X' : 'Y'}(${shift}px)` : undefined,
      ...(vertical
        ? { width: SEP_LINE, height: '100%', justifyContent: 'center', padding: `${inset}px 0` }
        : { height: SEP_LINE, flex: 1, alignItems: 'center', padding: `0 ${inset}px` }),
    }}>
      {/* Направляющая: в покое штриховая приглушённая, под курсором —
          сплошная акцентная. Штрих рисуется ГРАДИЕНТОМ, а не border: dashed
          привязывает длину штриха к толщине линии (при 2px они выходят длинными
          и редкими), а градиент задаёт шаг сам — DASH_STEP. */}
      <div style={{
        borderRadius: over ? SEP_LINE : 0,
        background: over
          ? C.accent
          : `repeating-linear-gradient(${vertical ? 'to bottom' : 'to right'}, ${accent ? C.accent : C.textSecondary} 0 ${DASH_LEN}px, transparent ${DASH_LEN}px ${DASH_STEP}px)`,
        opacity: over ? 1 : accent ? 1 : SEP_REST_OPACITY,
        transition: 'background 0.12s ease, opacity 0.12s ease',
        ...(vertical
          ? { width: SEP_LINE, height: '100%' }
          : { height: SEP_LINE, flex: 1 }),
      }} />
    </div>
  );
}

export function PanelDropGuide({ axis, dndActive, over, base = 0, edge, fill, icon: Icon, onDragOver, onDragLeave, onDrop }: {
  // 'x' — вертикальная направляющая (между колонками), 'y' — горизонтальная
  // (между панелями внутри колонки)
  axis: 'x' | 'y';
  dndActive: boolean;
  over: boolean;
  base?: number;
  // Крайняя позиция: 'start' — перед первой панелью/колонкой, 'end' — после
  // последней. Направляющая уезжает наружу на sepShift(base), чтобы не липнуть
  // к кромке; в середине зазора (base = GAP) сдвиг не нужен.
  edge?: 'start' | 'end';
  // Забрать ВСЮ свободную высоту колонки под дроп-зону. Нужно последней
  // направляющей: панели с высотой по контенту (короткий список чатов) не
  // достают до низа, и целиться приходилось в узкую полоску у их кромки, хотя
  // ниже пустует полколонки. Сама линия остаётся у кромки панели — она
  // показывает МЕСТО вставки, а не размер будущей панели.
  fill?: boolean;
  // Иконка перетаскиваемой панели — рисуется в центре большого плейсхолдера
  // (только при fill: в тонкой линии её негде показать)
  icon?: LucideIcon;
  onDragOver: (e: DragEvent) => void;
  onDragLeave: () => void;
  onDrop: (e: DragEvent) => void;
}) {
  const vertical = axis === 'x';
  const shift = edge === 'start' ? -sepShift(base) : edge === 'end' ? sepShift(base) : 0;
  return (
    <div style={vertical
      ? { width: base, flexShrink: 0, alignSelf: 'stretch', position: 'relative' }
      : {
          height: base, flexShrink: 0, position: 'relative',
          // Растяжимое место вставки: забирает свободный низ колонки. Когда его
          // нет (панель заняла всю высоту), на время перетаскивания держим хотя бы
          // полосу в высоту обычной дроп-зоны — иначе поставить вторую панель под
          // первую было бы просто некуда. В покое minHeight нулевой, колонка не
          // «дышит».
          ...(fill ? { flex: 1, minHeight: dndActive ? SEP_HIT : base } : null),
        }}
    >
      {dndActive && fill && !vertical ? (
        // Свободный низ колонки целиком: и мишень дропа, и рамка будущего места.
        // Линии здесь мало — она сообщала бы «встанет вплотную к панели», хотя
        // панель займёт как раз всю эту пустоту.
        <PanelDropSpot
          over={over}
          icon={Icon}
          boxProps={{ onDragOver, onDragLeave, onDrop }}
          style={{
            position: 'absolute', zIndex: 5,
            left: 0, right: 0, top: base + SEP_CLEARANCE, bottom: 0,
            margin: `0 ${SEP_INSET}px`,
          }}
        />
      ) : dndActive && (
        <div
          onDragOver={onDragOver}
          onDragLeave={onDragLeave}
          onDrop={onDrop}
          style={{
            position: 'absolute', zIndex: 5, display: 'flex',
            ...(vertical
              ? {
                  top: 0, bottom: 0, left: (base - SEP_HIT) / 2, width: SEP_HIT,
                  alignItems: 'stretch', justifyContent: 'center',
                }
              : {
                  left: 0, right: 0, top: (base - SEP_HIT) / 2, height: SEP_HIT,
                  alignItems: 'center',
                }),
          }}
        >
          <PanelDropLine axis={axis} over={over} shift={shift} />
        </div>
      )}
    </div>
  );
}
