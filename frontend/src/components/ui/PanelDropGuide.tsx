import type { DragEvent } from 'react';
import { C } from '../../lib/design';

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
const SEP_HIT = 22;
// Толщина направляющей. Длина штрихов у dashed пропорциональна толщине границы,
// так что чем толще — тем крупнее штрихи.
// ВАЖНО: у направляющей в покое borderRadius обязан быть 0. Скругление на
// элементе, у которого content схлопнут в ноль (border-box + размер == толщине
// границы), браузер рисует дугой и штриховка вырождается в сплошную линию —
// именно из-за этого плейсхолдер выглядел сплошным.
const SEP_LINE = 2;
// Отступ вдоль ДЛИНЫ направляющей — чтобы она не упиралась в торцы панелей
const SEP_INSET = 8;
// ЕДИНЫЙ зазор от кромки панели до направляющей. Все плейсхолдеры обязаны стоять
// на одном расстоянии, поэтому смещение считается формулой из base, а не задаётся
// вручную: у межколоночных зазор в потоке GAP, у крайних 0, у правого RAIL_GAP —
// без пересчёта они вставали бы на 3 / 7 / 1 px от панели соответственно.
//   sepShift(base) = SEP_CLEARANCE + SEP_LINE/2 - base/2
// Для base = GAP сдвиг нулевой: центр зазора и есть нужное место.
const SEP_CLEARANCE = 3;
const sepShift = (base: number) => SEP_CLEARANCE + SEP_LINE / 2 - base / 2;
// Приглушение направляющей в покое: она лишь намекает на возможные места вставки
// и не должна спорить с контентом панелей. Цвет остаётся C.textSecondary (он задаёт
// «характер» линии), гасится именно видимость — под курсором возвращается к 1,
// поэтому переход в акцентную сплошную читается тем заметнее, чем тише покой.
const SEP_REST_OPACITY = 0.25;

export function PanelDropGuide({ axis, dndActive, over, base = 0, edge, onDragOver, onDragLeave, onDrop }: {
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
  onDragOver: (e: DragEvent) => void;
  onDragLeave: () => void;
  onDrop: (e: DragEvent) => void;
}) {
  const vertical = axis === 'x';
  const shift = edge === 'start' ? -sepShift(base) : edge === 'end' ? sepShift(base) : 0;
  return (
    <div style={vertical
      ? { width: base, flexShrink: 0, alignSelf: 'stretch', position: 'relative' }
      : { height: base, flexShrink: 0, position: 'relative' }}
    >
      {dndActive && (
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
          {/* Коридор вокруг линии. Поля вдоль её длины — чтобы направляющая не
              упиралась в кромки панелей, а висела в зазоре с воздухом. */}
          <div style={{
            display: 'flex', boxSizing: 'content-box',
            transform: shift ? `translate${vertical ? 'X' : 'Y'}(${shift}px)` : undefined,
            ...(vertical
              ? { width: SEP_LINE, height: '100%', justifyContent: 'center', padding: `${SEP_INSET}px 0` }
              : { height: SEP_LINE, flex: 1, alignItems: 'center', padding: `0 ${SEP_INSET}px` }),
          }}>
            {/* Направляющая: в покое штриховая приглушённая, под курсором —
                сплошная акцентная. borderRadius в покое строго 0, иначе
                штриховка вырождается в сплошную (см. SEP_LINE выше). */}
            <div style={{
              borderRadius: over ? SEP_LINE : 0,
              background: over ? C.accent : 'transparent',
              opacity: over ? 1 : SEP_REST_OPACITY,
              transition: 'background 0.12s ease, border-color 0.12s ease, opacity 0.12s ease',
              ...(vertical
                ? {
                    width: over ? SEP_LINE : 0, height: '100%',
                    borderLeft: over ? 'none' : `${SEP_LINE}px dashed ${C.textSecondary}`,
                  }
                : {
                    height: over ? SEP_LINE : 0, flex: 1,
                    borderTop: over ? 'none' : `${SEP_LINE}px dashed ${C.textSecondary}`,
                  }),
            }} />
          </div>
        </div>
      )}
    </div>
  );
}
