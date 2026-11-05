import { useRef, useState } from 'react';
import { Check, ChevronDown, Link2 } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { Menu, MenuItem, MenuSep } from './Menu';
import { ICON_SIZE, ICON_STROKE } from './icons';

// Чип-кнопка с выпадающим меню выбора раздела вики. Ставится в закреплённый слот
// шапки панели (PanelHeaderSlot pinned): виден всегда, в покое приглушён до 0.55,
// под курсором и при открытом меню — на полном контрасте (макет notes-wiki-panels-v1,
// раздел «Чип раздела»; в v1.1 чип убран, для этой волны принят v1).
// Данных вики компонент не знает: value, options и label приходят пропами.
// Меню — в anchor-режиме (fixed, портал в body): карточка PanelShell держит
// transform ради анимации появления, и absolute-меню из её шапки уезжало бы
// на это смещение и обрезалось overflow острова.

export type SectionChipValue = { mode: 'follow' } | { mode: 'all' } | { mode: 'section'; key: string };
export interface SectionChipOption { key: string; label: string }

// Стабильные тексты контрола (макет, раздел «Тексты»): подсказка чипа и его пункты.
export const SECTION_CHIP_TITLE = 'Раздел вики, по которому панель показывает данные';
export const SECTION_CHIP_FOLLOW_LABEL = 'Следовать за открытой страницей';
export const SECTION_CHIP_ALL_LABEL = 'Все разделы';

type MenuEntry =
  | { kind: 'item'; label: string; checked: boolean; onSelect: () => void }
  | { kind: 'sep' };

// Чистая часть меню: описания пунктов в порядке отрисовки и их действия.
// Вынесена ради теста в окружении node (DOM здесь нет): компонент ровно
// перекладывает эти описания в MenuItem/MenuSep. Разделы идут в порядке
// options — порядок каталога решает вызывающая сторона.
export function sectionMenuItems(
  value: SectionChipValue,
  options: readonly SectionChipOption[],
  allowAll: boolean,
  followLabel: string,
  pick: (v: SectionChipValue) => void,
): MenuEntry[] {
  return [
    { kind: 'item', label: followLabel, checked: value.mode === 'follow', onSelect: () => pick({ mode: 'follow' }) },
    { kind: 'sep' },
    ...(allowAll
      ? [{ kind: 'item' as const, label: SECTION_CHIP_ALL_LABEL, checked: value.mode === 'all', onSelect: () => pick({ mode: 'all' as const }) }]
      : []),
    ...options.map((o) => ({
      kind: 'item' as const,
      label: o.label,
      checked: value.mode === 'section' && value.key === o.key,
      onSelect: () => pick({ mode: 'section' as const, key: o.key }),
    })),
  ];
}

// Кольцо фокуса — инжектим один раз, как у Chip/IconButton
const FOCUS_CLASS = 'cc-section-chip';
if (typeof document !== 'undefined' && !document.getElementById('cc-section-chip-style')) {
  const el = document.createElement('style');
  el.id = 'cc-section-chip-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

export function SectionChip({ value, options, allowAll = true, followLabel = SECTION_CHIP_FOLLOW_LABEL, label, onChange, touch }: {
  value: SectionChipValue;
  options: readonly SectionChipOption[];
  // «Все разделы» в меню (по умолчанию есть; снимается там, где панель всегда по одному разделу)
  allowAll?: boolean;
  // подпись follow-пункта меню; по умолчанию — тексты макета
  followLabel?: string;
  // что показать на чипе: вызывающий сам решает подпись (имя раздела, «Все разделы»,
  // имя открытой страницы при follow) — компонент данных вики не знает
  label: string;
  onChange: (v: SectionChipValue) => void;
  // тач-цель: высота 32 (вместо 24) и maxWidth 140 (вместо 150)
  touch?: boolean;
}) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const [hover, setHover] = useState(false);
  const btnRef = useRef<HTMLButtonElement | null>(null);
  const open = anchor != null;
  const entries = sectionMenuItems(value, options, allowAll, followLabel, (v) => { onChange(v); setAnchor(null); });
  return (
    <>
      <button
        ref={btnRef}
        type="button"
        className={FOCUS_CLASS}
        title={SECTION_CHIP_TITLE}
        aria-haspopup="menu"
        aria-expanded={open}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        onClick={() => setAnchor(open ? null : btnRef.current?.getBoundingClientRect() ?? null)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: SP.xs,
          height: touch ? SP.xxl : SP.xl, boxSizing: 'border-box', padding: `0 ${SP.sm}px`,
          maxWidth: touch ? 140 : 150,
          borderRadius: R.sm, border: 'none',
          background: open ? C.accentMuted : C.bgInset,
          color: open ? C.accent : C.textPrimary,
          fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans,
          flexShrink: 0, cursor: 'pointer',
          opacity: open || hover ? 1 : 0.55,
          transition: 'opacity 0.12s, background 0.12s, color 0.12s',
        }}
      >
        {/* follow: панель следует за открытой страницей — значок перед текстом (макет) */}
        {value.mode === 'follow' && <Link2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{label}</span>
        <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      </button>
      {open && (
        <Menu anchor={anchor} onClose={() => setAnchor(null)}>
          {entries.map((e, i) => e.kind === 'sep'
            ? <MenuSep key={i} />
            : <MenuItem
              key={i}
              label={e.label}
              icon={e.checked ? <Check size={15} strokeWidth={ICON_STROKE} /> : undefined}
              onClick={e.onSelect}
            />)}
        </Menu>
      )}
    </>
  );
}
