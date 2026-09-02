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
  action?: { icon: ReactNode; title: string; onClick: () => void; disabled?: boolean };
  // Клик по строке не закрывает меню. Нужен строкам-настройкам (пилюли шапки):
  // там основной клик и есть переключение видимости, и набор выставляется одним
  // заходом — как у строк-переключателей, но без второго контрола рядом с глазиком
  keepOpen?: boolean;
  // Образец САМОГО элемента под подписью строки: настройка видимости пилюль шапки —
  // про внешний вид, и узнать пилюлю по картинке быстрее, чем по названию. Образец
  // живой (клик открывает её собственный поповер) и не всплывает в строку, поэтому
  // посмотреть содержимое скрытой пилюли можно не возвращая её в шапку
  preview?: ReactNode;
  // Разделитель перед строкой — отбивает секцию (действия | пилюли)
  separator?: boolean;
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
  // Якорь открытия: rect кнопки «⋯» либо точка курсора при внешнем открытии.
  // Десктопное меню ВСЕГДА рисуется fixed-порталом по этому якорю, а не absolute
  // от корня: тулбар живёт и в узких колонках (стена, боковые панели), где
  // absolute-карточка шире контейнера обрезалась его overflow и уезжала за кромку.
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  // Режет ли карточка своё содержимое. Прокрутка нужна только когда список выше
  // отведённой высоты; в остальных случаях overflow обязан быть видимым, иначе
  // карточка срезает поповеры, которые пилюли-образцы раскрывают ИЗ строки меню.
  // Считаем по факту после рендера, до замера безопаснее резать
  const [clip, setClip] = useState(true);
  const rootRef = useRef<HTMLDivElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const triggerElRef = useRef<HTMLElement | null>(null);
  const labelId = useId();

  // Объявлено ДО эффектов, которые её зовут: обращение к функции выше её объявления
  // не даёт эффекту видеть актуальное значение (react-hooks/immutability)
  const close = () => { setOpen(false); setAnchor(null); };

  // Закрытие: клик вне (десктоп-дропдаун) + Esc (везде)
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      // Карточка меню живёт ПОРТАЛОМ в body, то есть вне rootRef: без её проверки
      // mousedown по пункту считался бы кликом «вне», меню закрывалось бы до
      // события click, и само действие пункта не срабатывало бы вовсе
      if (rootRef.current?.contains(t) || menuRef.current?.contains(t)) return;
      close();
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
    // Портал считает координаты один раз, в момент открытия, и сам за якорем не
    // ходит: прокрутка увезла бы кнопку из-под карточки. Закрываем — то же, что
    // ui/Menu просит делать на вызывающей стороне.
    //
    // Слушаем ТОЛЬКО прокручиваемых предков триггера, а не все события с capture:
    // глобальный слушатель ловил и чужие прокрутки — лента чата доскроливается
    // до низа сама при каждом рендере, и меню в шапке закрывалось в тот же кадр,
    // то есть не открывалось вовсе.
    const scrollParents: (HTMLElement | Window)[] = [window];
    for (let el = rootRef.current?.parentElement; el; el = el.parentElement) {
      const oy = getComputedStyle(el).overflowY;
      if (oy === 'auto' || oy === 'scroll') scrollParents.push(el);
    }
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    scrollParents.forEach(t => t.addEventListener('scroll', close));
    window.addEventListener('resize', close);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
      scrollParents.forEach(t => t.removeEventListener('scroll', close));
      window.removeEventListener('resize', close);
    };
    // close стабилен по составу (setOpen/setAnchor) — в зависимостях не нужен, иначе
    // эффект пересоздавался бы каждый рендер
  }, [open]);

  // Замер после открытия: список влез — снимаем прокрутку, чтобы поповер образца
  // мог выйти за карточку (иначе он срезается её кромкой и виден полоской)
  useEffect(() => {
    if (!open || isMobile) { setClip(true); return; }
    const el = menuRef.current;
    if (el) setClip(el.scrollHeight > el.clientHeight + 1);
  // Длина списка, а не сам массив: он пересоздаётся каждым рендером, и эффект бежал
  // бы снова уже с раскрытым поповером — тот увеличивает scrollHeight, прокрутка
  // возвращалась бы ровно в момент, ради которого её и снимали
  }, [open, isMobile, items?.length]);

  // Расчёт направления дропдауна по rect якоря (кнопка «⋯» или точка курсора при
  // внешнем открытии) — общая часть toggle и openTrigger-эффекта
  const openNear = (anchorRect: DOMRect | null | undefined) => {
    const h = items ? items.length * ROW_H + (title ? TITLE_H : 0) + PAD_H : CHILDREN_H;
    // Внешний якорь приоритетнее rect корневого блока: right-click открывает меню
    // к точке курсора, а не под кнопкой «⋯»
    const r = anchorRect ?? rootRef.current?.getBoundingClientRect() ?? null;
    if (r && !isMobile) {
      const below = window.innerHeight - r.bottom - GAP - EDGE;
      const above = r.top - GAP - EDGE;
      const up = h > below && above > below;
      setDrop({ up, maxH: Math.max(MIN_H, Math.floor(up ? above : below)) });
    }
    setAnchor(r);
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

      {/* Десктопное меню — всегда fixed-портал по якорю: карточка шире узкой
          колонки (стена, боковые панели) обрезалась бы их overflow, а у правого
          клика якорь вообще на другом краю полосы. Портал вне контекста наложения
          зоны, поэтому и слой выше модалок — как в anchor-режиме ui/Menu */}
      {open && !isMobile && anchor && createPortal(
        <div
          ref={menuRef}
          role="menu" aria-labelledby={title ? labelId : undefined}
          style={fixedDropdownStyle(anchor, drop, align, clip)}
        >
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
    if (!isToggle && !item.keepOpen) onDone();   // переключатель и строка-настройка оставляют меню открытым
  };
  // Строка-превью не может быть <button>: внутри неё живёт сама пилюля, а она
  // тоже кнопка — вложенная кнопка невалидна и ломает разметку. Рисуем span
  // с тем же кликом: строка остаётся управляемой, а вложенность честной
  const Tag = (item.preview != null ? 'span' : 'button') as 'span';
  const row = (
    <Tag
      {...(item.preview != null
        ? { role: 'none' as const, onClick: handle }
        : {
            type: 'button' as const, onClick: handle, disabled: item.disabled,
            role: isToggle ? 'menuitemcheckbox' : 'menuitem',
            'aria-checked': isToggle ? item.toggle : undefined,
          })}
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
        {item.preview != null && (
          // Сам элемент под подписью: название говорит, что это, образец — как оно
          // выглядит. Пилюля остаётся рабочей (её поповер открывается и отсюда),
          // поэтому клик по ней не всплывает в строку и видимость не переключает
          <span
            style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, marginTop: 4 }}
            onClick={e => e.stopPropagation()}
          >
            {item.preview}
          </span>
        )}
        {item.sublabel && <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, fontWeight: 400 }}>{item.sublabel}</span>}
      </span>
      {isToggle && (
        <span style={{ ...switchTrack, background: item.toggle ? C.accent : C.track }}>
          <span style={{ ...switchThumb, transform: item.toggle ? 'translateX(16px)' : 'translateX(0)' }} />
        </span>
      )}
      {!isToggle && item.dot && <span style={dotBadgeStatic} />}
    </Tag>
  );
  // Разделитель перед строкой — отбивка секции (действия | пилюли)
  const sep = item.separator
    ? <div style={{ height: 1, background: C.borderLight, margin: '5px 8px 4px' }} />
    : null;
  if (!item.action) return sep ? <>{sep}{row}</> : row;
  // Строка с действием-спутником: подсветку наведения держит обёртка, кнопка
  // справа гасит всплытие — основное действие пункта от неё не срабатывает
  return (
    <>
      {sep}
      <span
        role="none"
        style={{ display: 'flex', alignItems: 'center', width: '100%', minWidth: 0, borderRadius: R.lg, paddingRight: 4 }}
        onMouseEnter={e => { e.currentTarget.style.background = C.bgInset; }}
        onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
      >
        {row}
        <ToolbarIconButton
          onClick={e => { e.stopPropagation(); if (!item.action!.disabled) item.action!.onClick(); }}
          title={item.action.title}
          disabled={item.action.disabled}
          isMobile={isMobile}
        >
          {item.action.icon}
        </ToolbarIconButton>
      </span>
    </>
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
const MIN_W = 240;   // минимальная ширина карточки меню
// Fixed-дропдаун по якорю (кнопка «⋯» либо точка курсора): координаты
// viewport-ные, портал в body. Направление вверх/вниз посчитано в openNear,
// здесь — привязка к нужной кромке якоря и кламп в окно.
function fixedDropdownStyle(anchor: DOMRect, drop: { up: boolean; maxH: number }, align: 'left' | 'right', clip: boolean): CSSProperties {
  // Ширина карточки заранее не известна (240..320 по содержимому), поэтому
  // выравнивание вправо задаём через right — иначе пришлось бы угадывать ширину
  const horizontal: CSSProperties = align === 'right'
    ? { right: Math.max(EDGE, window.innerWidth - anchor.right) }
    : { left: Math.max(EDGE, Math.min(anchor.left, window.innerWidth - MIN_W - EDGE)) };
  return {
    position: 'fixed',
    // Вверх — от верхней кромки якоря, вниз — от нижней; в обоих случаях зазор GAP
    ...(drop.up
      ? { bottom: Math.max(EDGE, window.innerHeight - anchor.top + GAP) }
      : { top: anchor.bottom + GAP }),
    ...horizontal,
    minWidth: MIN_W,
    // Потолок ширины — по месту до кромки окна: в узкой колонке карточка не должна
    // вылезать за экран, а расти ей есть куда только вправо/влево от якоря
    maxWidth: Math.min(320, window.innerWidth - 2 * EDGE),
    ...(drop.maxH ? { maxHeight: drop.maxH, overflowY: clip ? ('auto' as const) : ('visible' as const) } : null),
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
