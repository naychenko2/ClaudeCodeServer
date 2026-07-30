import { useState, type ReactNode } from 'react';
import { C, R, TB } from '../../lib/design';

// Группа иконок-переключателей: «списком | деревом», «список | по дате | доска».
// Один выбранный вариант, подпись уходит в tooltip — форма для тесных мест,
// прежде всего для шапки панели (PanelHeaderSlot).
//
// Вид — дорожка с ползунком: утопленный тёмный фон группы, выбранная позиция
// белой плашкой, которая ЕДЕТ к новому варианту (отдельный слой под кнопками,
// сдвиг через transform). Цвет иконки НЕ меняется по состоянию — выбор читается
// плашкой, а не перекраской; так в ряду иконок шапки не появляется второй
// «активный» акцент рядом с оранжевой кнопкой действия.
//
// Чем отличается от соседей:
// - IconButton — одиночное действие, а не выбор из набора;
// - SegmentedControl — крупные сегменты с подписями (настройки, диалоги);
// - PillViewSwitcher (features/tasks) — та же дорожка, но с подписями;
//   остаётся там, где есть место (тело панели, мобила).

// Геометрия дорожки: кнопка 28×20 (иконка 14 + поля), зазор и рамка по 2 —
// итоговая высота 24, вписывается в шапку панели (ISLAND.headerH = 40).
const BTN_W = 28;
const BTN_H = 20;
const GAP = 2;
const PAD = 2;

export interface IconSegmentedOption<T extends string> {
  value: T;
  label: string;    // tooltip кнопки
  icon: ReactNode;  // иконка 14px
}

export function IconSegmented<T extends string>({ value, options, onChange, style }: {
  value: T;
  options: IconSegmentedOption<T>[];
  onChange: (v: T) => void;
  style?: React.CSSProperties;
}) {
  const [hover, setHover] = useState<T | null>(null);
  const activeIdx = options.findIndex(o => o.value === value);
  return (
    <span style={{
      position: 'relative', display: 'flex', flexShrink: 0, gap: GAP, padding: PAD,
      background: C.track, borderRadius: R.md,
      ...style,
    }}>
      {/* Ползунок — единственная белая плашка, переезжающая к выбранной позиции.
          Плашка на КАЖДОЙ кнопке не годится: React перерисовал бы фон мгновенно,
          и переключение получилось бы без движения. */}
      {activeIdx >= 0 && (
        <span
          aria-hidden
          style={{
            position: 'absolute', top: PAD, left: PAD, width: BTN_W, height: BTN_H,
            borderRadius: R.sm, background: TB.pillThumbBg, boxShadow: TB.pillThumbShadow,
            transform: `translateX(${activeIdx * (BTN_W + GAP)}px)`,
            transition: 'transform 0.18s cubic-bezier(0.4, 0, 0.2, 1)',
          }}
        />
      )}
      {options.map(opt => {
        const active = opt.value === value;
        return (
          <button
            key={opt.value}
            onClick={() => onChange(opt.value)}
            onMouseEnter={() => setHover(opt.value)}
            onMouseLeave={() => setHover(null)}
            title={opt.label}
            style={{
              position: 'relative', width: BTN_W, height: BTN_H, padding: 0,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              border: 'none', borderRadius: R.sm,
              cursor: active ? 'default' : 'pointer',
              // Подсветка только у невыбранных: фон выбранной рисует ползунок под ними
              background: !active && hover === opt.value ? C.bgSelected : 'transparent',
              // Цвет иконки одинаков во всех состояниях — см. комментарий сверху
              color: C.textSecondary,
              transition: 'background 0.12s',
            }}
          >
            {opt.icon}
          </button>
        );
      })}
    </span>
  );
}
