import type { ReactNode, CSSProperties } from 'react';
import { useEffect, useRef, useState, useId } from 'react';
import { createPortal } from 'react-dom';
import { MoreHorizontal } from 'lucide-react';
import { C, R, TB, FONT, SHADOW, Z } from '../lib/design';
import { ToolbarIconButton } from './Toolbar';

// === Единый overflow-примитив тулбаров ===
// «Что не влезло → в меню». На десктопе — дропдаун под триггером, на мобиле —
// боттом-шит (тот же визуальный язык, что у палитры AiLauncher). Заменяет собой
// разрозненные бэспоук-реализации (MobileCombinedBadge / FilterBar / mode-дропдаун).
//
// Два способа наполнения (взаимоисключимы):
//   • items — простой список строк (иконка + подпись [+ описание], пункт или переключатель);
//   • children — произвольное содержимое (секции фильтров и т.п.).
//
// Разметка «primary/overflow» — явная (без авто-измерения ширины): вызывающий тулбар
// сам решает, что оставить в ряду, а что передать сюда.

export type OverflowItem = {
  key: string;
  icon?: ReactNode;
  label: string;
  sublabel?: string;
  onClick?: () => void;
  // undefined — обычный пункт (клик закрывает меню); boolean — строка-переключатель
  // (клик переключает, меню остаётся открытым, справа рисуется свитч по значению).
  toggle?: boolean;
  dot?: boolean;        // точка-индикатор справа («живой» пункт)
  active?: boolean;     // текущий раздел — подсвечивается accent-цветом
  danger?: boolean;
  disabled?: boolean;
  // Второе действие строки — кнопка-иконка справа (у пункта есть и основной клик,
  // и побочная команда: глазик видимости кнопки в ряду). Клик по ней НЕ выполняет
  // основное действие и НЕ закрывает меню — набор выставляется одним заходом.
  // Отдельной кнопкой, а не иконкой ВНУТРИ пункта: <button> в <button> вложить
  // нельзя, поэтому строка становится flex-обёрткой (тот же приём, что в ui/Menu)
  action?: { icon: ReactNode; title: string; onClick: () => void };
};

type TriggerRenderer = (p: { open: boolean; toggle: () => void; ref: (el: HTMLElement | null) => void }) => ReactNode;

export function ToolbarOverflowMenu({
  isMobile,
  items,
  children,
  title,
  triggerIcon,
  triggerLabel,
  triggerTitle = 'Ещё',
  indicator,
  align = 'right',
  renderTrigger,
  openTrigger,
}: {
  isMobile?: boolean;
  items?: OverflowItem[];
  children?: ReactNode;
  title?: string;
  triggerIcon?: ReactNode;
  triggerLabel?: string;
  triggerTitle?: string;
  // number>0 → счётчик-бейдж; true → точка; ReactNode → как есть; иначе ничего
  indicator?: number | boolean | ReactNode;
  align?: 'left' | 'right';
  renderTrigger?: TriggerRenderer;
  // Внешнее открытие меню (right-click по зоне тулбара): при изменении counter
  // меню открывается по anchor (если задан; без anchor — под триггером «⋯»).
  // Якорь с нулевым размером ставит дропдаун прямо к точке курсора
  openTrigger?: { counter: number; anchor?: DOMRect | null };
}) {
  const [open, setOpen] = useState(false);
  // Куда раскрывать десктопный дропдаун и сколько ему позволено занять по высоте.
  // Жёсткого «вниз» мало: тулбар композера стоит у нижней кромки окна, и меню
  // уходило за экран целиком (на мобиле беды нет — там боттом-шит). Считаем в
  // момент открытия по rect триггера: вверх, если снизу места меньше, чем сверху.
  const [drop, setDrop] = useState<{ up: boolean; maxH: number }>({ up: false, maxH: 0 });
  // Якорь внешнего открытия (right-click): пока задан, десктоп-дропдаун рендерится
  // fixed-порталом по точке курсора; закрытие меню сбрасывает якорь
  const [extAnchor, setExtAnchor] = useState<DOMRect | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerElRef = useRef<HTMLElement | null>(null);
  const labelId = useId();

  // Закрытие: клик вне (десктоп-дропдаун) + Esc (везде)
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDown); document.removeEventListener('keydown', onKey); };
    // close стабилен по составу (setOpen/setExtAnchor) — без него в зависимостях эффект
    // пересоздавался бы каждый рендер
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const close = () => { setOpen(false); setExtAnchor(null); };
  // Расчёт направления дропдауна по rect якоря (кнопка «⋯» или точка курсора при
  // внешнем открытии) — общая часть toggle и openTrigger-эффекта
  const openNear = (anchorRect: DOMRect | null | undefined) => {
    const h = items ? items.length * ROW_H + (title ? TITLE_H : 0) + PAD_H : CHILDREN_H;
    // Внешний якорь приоритетнее rect корневого блока: right-click открывает меню
    // к точке курсора, а не под кнопкой «⋯»
    const r = anchorRect ?? rootRef.current?.getBoundingClientRect();
    if (r && !isMobile) {
      const below = window.innerHeight - r.bottom - GAP - EDGE;
      const above = r.top - GAP - EDGE;
      const up = h > below && above > below;
      setDrop({ up, maxH: Math.max(MIN_H, Math.floor(up ? above : below)) });
    }
    setExtAnchor(anchorRect ?? null);
    setOpen(true);
  };
  const toggle = () => {
    if (open) { close(); return; }
    // Высоту меряем оценкой, а не по факту: реальная высота известна только у уже
    // отрисованного меню, а измерять его в layout-эффекте — значит дёргать setState
    // из эффекта ради того же ответа. Строк у списка мы знаем, произвольному
    // содержимому (children) даём типовой максимум.
    openNear(undefined);
  };

  // Внешнее открытие (right-click по зоне): counter меняется — открываем по якорю
  const prevTrigger = useRef(0);
  useEffect(() => {
    if (!openTrigger || openTrigger.counter === prevTrigger.current) return;
    prevTrigger.current = openTrigger.counter;
    openNear(openTrigger.anchor);
    // openNear намеренно не в зависимостях: она замкнута на текущие items/title,
    // а эффект должен срабатывать только по счётчику
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openTrigger]);

  const content = children ?? (items ? (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      {items.map(it => (
        <ItemRow key={it.key} item={it} isMobile={isMobile} onDone={close} />
      ))}
    </div>
  ) : null);

  // --- Триггер ---
  const setTriggerRef = (el: HTMLElement | null) => { triggerElRef.current = el; };
  let trigger: ReactNode;
  if (renderTrigger) {
    // eslint-disable-next-line react-hooks/refs -- setTriggerRef — callback ref, React зовёт его на коммите, не в рендере
    trigger = renderTrigger({ open, toggle, ref: setTriggerRef });
  } else if (triggerLabel) {
    // Кнопка с подписью (например «Фильтры») — chip-стиль тулбара
    trigger = (
      <button
        type="button" onClick={toggle} title={triggerTitle}
        aria-haspopup="menu" aria-expanded={open}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
          height: isMobile ? TB.iconHitMobile : 34, padding: '0 12px',
          borderRadius: R.lg, border: `1px solid ${open ? C.accent : C.border}`,
          background: open ? C.accentLight : C.bgWhite, color: open ? C.accent : C.textSecondary,
          fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, cursor: 'pointer', position: 'relative',
        }}
      >
        {triggerIcon}
        <span>{triggerLabel}</span>
        {typeof indicator === 'number' && indicator > 0 && (
          <span style={countBadgeInline}>{indicator}</span>
        )}
      </button>
    );
  } else {
    // Дефолтный icon-триггер «⋯» с опциональным индикатором
    trigger = (
      <span style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
        <ToolbarIconButton onClick={toggle} title={triggerTitle} isMobile={isMobile} active={open}>
          {triggerIcon ?? <MoreHorizontal size={18} />}
        </ToolbarIconButton>
        {typeof indicator === 'number' && indicator > 0 && <span style={countBadge}>{indicator}</span>}
        {indicator === true && <span style={dotBadge} />}
      </span>
    );
  }

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, display: 'inline-flex' }}>
      {trigger}

      {open && !isMobile && !extAnchor && (
        <div role="menu" aria-labelledby={title ? labelId : undefined} style={dropdownStyle(align, drop)}>
          {title && <div id={labelId} style={sectionTitle}>{title}</div>}
          {content}
        </div>
      )}
      {/* Внешний якорь (right-click): fixed-портал в body по точке курсора —
          absolute-дропдаун от корня не доехал бы до курсора на другом краю полосы */}
      {open && !isMobile && extAnchor && createPortal(
        <div role="menu" aria-labelledby={title ? labelId : undefined} style={fixedDropdownStyle(extAnchor, drop)}>
          {title && <div id={labelId} style={sectionTitle}>{title}</div>}
          {content}
        </div>,
        document.body,
      )}

      {open && isMobile && createPortal(
        <div style={sheetOverlay} onMouseDown={close}>
          <div
            className="cc-sheet-card"
            style={sheetCard} onMouseDown={e => e.stopPropagation()}
            role="dialog" aria-modal="true" aria-labelledby={title ? labelId : undefined}
          >
            <div style={sheetHandle} />
            {title && <div id={labelId} style={{ ...sectionTitle, padding: '0 12px 8px' }}>{title}</div>}
            <div style={{ paddingBottom: 'env(safe-area-inset-bottom, 0px)' }}>{content}</div>
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

// === Строка меню/шита ===
function ItemRow({ item, isMobile, onDone }: { item: OverflowItem; isMobile?: boolean; onDone: () => void }) {
  const isToggle = item.toggle !== undefined;
  const handle = () => {
    if (item.disabled) return;
    item.onClick?.();
    if (!isToggle) onDone();   // переключатель оставляет меню открытым
  };
  const row = (
    <button
      type="button" onClick={handle} disabled={item.disabled}
      role={isToggle ? 'menuitemcheckbox' : 'menuitem'} aria-checked={isToggle ? item.toggle : undefined}
      style={{
        display: 'flex', alignItems: 'center', gap: 12, width: '100%', textAlign: 'left',
        border: 'none', background: 'transparent', cursor: item.disabled ? 'default' : 'pointer',
        borderRadius: R.lg, padding: isMobile ? '11px 12px' : '9px 10px',
        minHeight: isMobile ? 44 : undefined, fontFamily: FONT.sans,
        color: item.danger ? C.dangerText : item.active ? C.accent : C.textHeading,
        opacity: item.disabled ? 0.5 : 1,
        // При действии-спутнике фон и скругление рисует обёртка, иначе подсветка
        // обрывалась бы ровно перед кнопкой справа
        ...(item.action ? { flex: 1, minWidth: 0, paddingRight: 4 } : null),
      }}
      onMouseEnter={e => { if (!item.disabled && !item.action) e.currentTarget.style.background = C.bgInset; }}
      onMouseLeave={e => { if (!item.action) e.currentTarget.style.background = 'transparent'; }}
    >
      {item.icon != null && (
        <span style={{
          width: 24, display: 'grid', placeItems: 'center', flex: 'none',
          color: item.danger ? C.dangerText : item.active ? C.accent : C.textSecondary,
        }}>
          {item.icon}
        </span>
      )}
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'block', fontSize: 14, fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{item.label}</span>
        {item.sublabel && <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, fontWeight: 400 }}>{item.sublabel}</span>}
      </span>
      {isToggle && (
        <span style={{ ...switchTrack, background: item.toggle ? C.accent : C.track }}>
          <span style={{ ...switchThumb, transform: item.toggle ? 'translateX(16px)' : 'translateX(0)' }} />
        </span>
      )}
      {!isToggle && item.dot && <span style={dotBadgeStatic} />}
    </button>
  );
  if (!item.action) return row;
  // Строка с действием-спутником: подсветку наведения держит обёртка, кнопка
  // справа гасит всплытие — основное действие пункта от неё не срабатывает
  return (
    <span
      style={{ display: 'flex', alignItems: 'center', width: '100%', minWidth: 0, borderRadius: R.lg, paddingRight: 4 }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgInset; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
    >
      {row}
      <ToolbarIconButton
        onClick={e => { e.stopPropagation(); item.action!.onClick(); }}
        title={item.action.title}
      >
        {item.action.icon}
      </ToolbarIconButton>
    </span>
  );
}

// === Стили ===
// Метрики для выбора направления дропдауна: высота строки/заголовка/полей списка,
// запас для произвольного содержимого, зазор до триггера, отступ от кромки окна
// и минимум, ниже которого сворачивать меню бессмысленно (лучше проскроллить).
const ROW_H = 40;
const TITLE_H = 24;
const PAD_H = 12;
const CHILDREN_H = 320;
const GAP = 6;
const EDGE = 8;
const MIN_H = 140;

function dropdownStyle(align: 'left' | 'right', drop: { up: boolean; maxH: number }): CSSProperties {
  return {
    position: 'absolute',
    ...(drop.up ? { bottom: `calc(100% + ${GAP}px)` } : { top: `calc(100% + ${GAP}px)` }),
    left: align === 'left' ? 0 : undefined, right: align === 'right' ? 0 : undefined,
    minWidth: 240, maxWidth: 320,
    // Потолок по свободному месту: даже развёрнутое в нужную сторону длинное меню
    // не должно вылезать за кромку — остаток прокручивается внутри
    ...(drop.maxH ? { maxHeight: drop.maxH, overflowY: 'auto' as const } : null),
    background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
    boxShadow: SHADOW.dropdown, padding: 6, zIndex: Z.dropdown,
  };
}
// Fixed-дропдаун по внешнему якорю (right-click): координаты viewport-ные, портал в
// body — расчёт направления (вверх/вниз) общий с absolute-режимом через openNear
function fixedDropdownStyle(anchor: DOMRect, drop: { up: boolean; maxH: number }): CSSProperties {
  const left = Math.max(EDGE, Math.min(anchor.left, window.innerWidth - 240 - EDGE));
  return {
    position: 'fixed',
    top: anchor.bottom + GAP,
    left,
    minWidth: 240, maxWidth: 320,
    ...(drop.maxH ? { maxHeight: drop.maxH, overflowY: 'auto' as const } : null),
    background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
    boxShadow: SHADOW.dropdown, padding: 6,
    // Портал в body — вне контекста наложения исходной зоны, слой выше модалок
    // (как у anchor-режима ui/Menu)
    zIndex: Z.modal + 1,
  };
}
const sheetOverlay: CSSProperties = {
  position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal,
  display: 'flex', alignItems: 'flex-end', justifyContent: 'center',
};
const sheetCard: CSSProperties = {
  width: '100%', maxWidth: '100%', background: C.bgCard, border: `1px solid ${C.border}`,
  borderTopLeftRadius: R.sheet, borderTopRightRadius: R.sheet, boxShadow: SHADOW.sheet,
  padding: 8, maxHeight: '82vh', overflowY: 'auto',
};
const sheetHandle: CSSProperties = {
  width: 38, height: 4, borderRadius: 999, background: C.border, margin: '6px auto 10px',
};
const sectionTitle: CSSProperties = {
  fontFamily: FONT.mono, fontSize: 10.5, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, padding: '6px 10px 4px',
};
const switchTrack: CSSProperties = {
  width: 34, height: 18, borderRadius: 999, position: 'relative', flex: 'none', transition: 'background .16s',
};
const switchThumb: CSSProperties = {
  position: 'absolute', top: 2, left: 2, width: 14, height: 14, borderRadius: '50%',
  background: C.bgWhite, boxShadow: SHADOW.thumb, transition: 'transform .16s',
};
const countBadge: CSSProperties = {
  position: 'absolute', top: -2, right: -2, minWidth: 15, height: 15, padding: '0 3px',
  borderRadius: 999, background: C.accent, color: C.onAccent, fontSize: 9, fontWeight: 700,
  fontFamily: FONT.mono, display: 'grid', placeItems: 'center', pointerEvents: 'none',
};
const countBadgeInline: CSSProperties = {
  minWidth: 16, height: 16, padding: '0 4px', borderRadius: 999, background: C.accent, color: C.onAccent,
  fontSize: 10, fontWeight: 700, fontFamily: FONT.mono, display: 'grid', placeItems: 'center',
};
const dotBadge: CSSProperties = {
  position: 'absolute', top: 0, right: 0, width: 8, height: 8, borderRadius: '50%',
  background: C.accent, border: `2px solid ${C.bgPanel}`, pointerEvents: 'none',
};
const dotBadgeStatic: CSSProperties = {
  width: 8, height: 8, borderRadius: '50%', background: C.accent, flex: 'none',
};
