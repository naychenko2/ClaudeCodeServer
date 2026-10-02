import { useLayoutEffect, useState, type ReactNode } from 'react';
import { C, R, TB } from '../../lib/design';
import type { SegmentOptionState } from './Segmented';

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
// Та же «пружинистая» кривая, что у пилюли главного меню (PillSwitch)
const QUIET_EASE = 'cubic-bezier(.32,.72,0,1)';

// Память выбранной позиции ВНЕ React, по persistKey — как у PillSwitch: переключатель,
// который перемонтируется вместе с хозяином (полосы над композером рисуют его каждая у
// себя), стартует с прошлой позиции и на следующем кадре доезжает до своей
const segMemory = new Map<string, number>();

// title — подсказка вместо label (например, «Обработка — сначала выберите звук»)
export interface IconSegmentedOption<T extends string> extends SegmentOptionState {
  value: T;
  label: string;    // tooltip кнопки
  icon: ReactNode;  // иконка 14px
}

export function IconSegmented<T extends string>({ value, options, onChange, style, quiet, persistKey }: {
  value: T;
  options: IconSegmentedOption<T>[];
  onChange: (v: T) => void;
  style?: React.CSSProperties;
  // Тихий вид: без утопленной дорожки и белой плашки с тенью — под выбранной иконкой
  // мягкая подложка C.bgSelected, выбор читается ещё и цветом иконки. Для мест, где
  // переключатель стоит рядом с содержимым, а не в шапке (полосы над композером)
  quiet?: boolean;
  persistKey?: string;
}) {
  const [hover, setHover] = useState<T | null>(null);
  const activeIdx = options.findIndex(o => o.value === value);
  // Где плашка стоит сейчас: у свежего экземпляра — из памяти, дальше доезжает
  const [thumbIdx, setThumbIdx] = useState(() => (persistKey ? segMemory.get(persistKey) ?? activeIdx : activeIdx));
  useLayoutEffect(() => {
    if (persistKey) segMemory.set(persistKey, activeIdx);
    if (thumbIdx === activeIdx) return;
    // Кадр со старой позицией должен отрисоваться — иначе transition не с чего запускать
    const id = requestAnimationFrame(() => setThumbIdx(activeIdx));
    return () => cancelAnimationFrame(id);
  }, [activeIdx, thumbIdx, persistKey]);
  return (
    <span style={{
      position: 'relative', display: 'flex', flexShrink: 0, gap: GAP, padding: PAD,
      background: quiet ? 'transparent' : C.track, borderRadius: R.md,
      ...style,
    }}>
      {/* Ползунок — единственная плашка, переезжающая к выбранной позиции.
          Плашка на КАЖДОЙ кнопке не годится: React перерисовал бы фон мгновенно,
          и переключение получилось бы без движения. */}
      {activeIdx >= 0 && thumbIdx >= 0 && (
        <span
          aria-hidden
          style={{
            position: 'absolute', top: PAD, left: PAD, width: BTN_W, height: BTN_H,
            borderRadius: R.sm,
            background: quiet ? C.bgSelected : TB.pillThumbBg,
            boxShadow: quiet ? 'none' : TB.pillThumbShadow,
            transform: `translateX(${thumbIdx * (BTN_W + GAP)}px)`,
            transition: quiet ? `transform 0.32s ${QUIET_EASE}` : 'transform 0.18s cubic-bezier(0.4, 0, 0.2, 1)',
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
            disabled={opt.disabled}
            title={opt.title ?? opt.label}
            style={{
              position: 'relative', width: BTN_W, height: BTN_H, padding: 0,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              border: 'none', borderRadius: R.sm,
              cursor: active ? 'default' : 'pointer',
              // Подсветка только у невыбранных: фон выбранной рисует ползунок под ними.
              // В тихом виде подложка ползунка того же цвета, что hover, — там наведение
              // только подкрашивает иконку, иначе оно читалось бы как выбор
              background: !quiet && !active && hover === opt.value ? C.bgSelected : 'transparent',
              // Цвет иконки одинаков во всех состояниях — см. комментарий сверху; в тихом
              // виде подложка едва видна, и выбор дочитывается цветом
              color: quiet ? (active || hover === opt.value ? C.textHeading : C.textMuted) : C.textSecondary,
              transition: quiet ? 'background 0.12s, color 0.2s' : 'background 0.12s',
              ...(opt.disabled
                ? { cursor: 'not-allowed', opacity: 0.35, background: 'transparent' }
                : opt.muted && !active ? { opacity: 0.5 } : null),
            }}
          >
            {opt.icon}
          </button>
        );
      })}
    </span>
  );
}
