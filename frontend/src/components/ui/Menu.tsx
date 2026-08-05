import { useState } from 'react';
import { createPortal } from 'react-dom';
import type { CSSProperties, HTMLAttributes, MouseEvent, ReactNode } from 'react';
import { C, R, FONT, SHADOW, Z } from '../../lib/design';
import { IconButton } from './IconButton';

// Единое выпадающее меню: карточка + подложка для закрытия по клику вне.
// Два режима позиционирования:
//  - обычный: absolute внутри родителя position:relative; bottom (если задан) вместо
//    top — карточка растёт ВВЕРХ (для триггеров у нижнего края панели);
//  - anchor: fixed по DOMRect триггера — для меню внутри скролл-контейнеров
//    (overflow списка обрезал бы absolute). Направление — от maxHeight: вверх,
//    если под якорем не хватает места; по горизонтали клампится в окно.
//    Рисуется порталом в body: position:fixed отсчитывается от viewport, только
//    пока НИ У ОДНОГО предка нет transform/filter/perspective — а карточка
//    PanelShell держит transform ради анимации появления, и меню внутри панели
//    уезжало на её смещение и обрезалось overflow острова.
// Закрытие по Esc/скроллу в anchor-режиме — на вызывающей стороне (поведение, не контрол).
export function Menu({ onClose, align = 'right', top = 30, bottom, minWidth = 200, anchor, maxHeight = 300, gap = 6, anchorSide, inertBackdrop, children }: {
  onClose: () => void;
  align?: 'left' | 'right';
  top?: number;
  bottom?: number;
  minWidth?: number;
  // rect кнопки-триггера; задан — режим fixed (align/top/bottom игнорируются)
  anchor?: DOMRect;
  // высота меню для выбора направления в anchor-режиме (вверх, если снизу не влезает)
  maxHeight?: number;
  // зазор между якорем и карточкой в anchor-режиме; меньше — попап липнет к триггеру
  gap?: number;
  // Карточка встаёт СБОКУ от якоря, а не под ним: сторона — та, с которой у якоря
  // есть место (для кнопки в левой рельсе это 'left' — попап уезжает вправо).
  // Нужно кнопкам, прижатым к кромке окна: под ними места нет, а обычный расчёт
  // прижал бы карточку к тому же краю поверх самой рельсы. Низ карточки
  // выравнивается по низу якоря.
  anchorSide?: 'left' | 'right';
  // Подложка перестаёт ловить события. Нужно, когда из меню ЧТО-ТО ПЕРЕТАСКИВАЮТ:
  // подложка накрывает весь экран и первой перехватывает dragover, так что до
  // мест дропа под ней события не доходят вовсе. Закрыть меню на старте
  // перетаскивания нельзя — исчезнувший источник не дождётся dragend.
  inertBackdrop?: boolean;
  children: ReactNode;
}) {
  let pos: CSSProperties;
  if (anchor && anchorSide) {
    // Сбоку от якоря: по горизонтали — от его кромки. По вертикали карточка растёт
    // ВВЕРХ от низа якоря (кнопки у нижнего края рельсы), а если сверху не хватает
    // места — вниз от его верха, прижимаясь к нижней кромке окна.
    const room = anchor.bottom >= maxHeight + 8;
    pos = {
      position: 'fixed',
      ...(anchorSide === 'left' ? { left: anchor.right + gap } : { right: window.innerWidth - anchor.left + gap }),
      ...(room
        ? { bottom: window.innerHeight - anchor.bottom }
        : { top: Math.max(8, Math.min(anchor.top, window.innerHeight - maxHeight - 8)) }),
    };
  } else if (anchor) {
    const openUp = anchor.bottom + gap + maxHeight > window.innerHeight && anchor.top > maxHeight;
    const left = Math.max(8, Math.min(anchor.right - minWidth, window.innerWidth - minWidth - 8));
    pos = {
      position: 'fixed', left,
      ...(openUp ? { bottom: window.innerHeight - anchor.top + gap } : { top: anchor.bottom + gap }),
    };
  } else {
    pos = { position: 'absolute', ...(bottom != null ? { bottom } : { top }), [align]: 0 };
  }
  const card = (
    <>
      <div
        style={{ position: 'fixed', inset: 0, zIndex: Z.dropdown, pointerEvents: inertBackdrop ? 'none' : undefined }}
        onClick={onClose}
      />
      <div style={{
        ...pos, zIndex: Z.dropdown + 1,
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        boxShadow: SHADOW.dropdown, padding: 5, minWidth, display: 'flex', flexDirection: 'column',
        // В боковом режиме карточка встаёт от кромки окна и расти ей некуда: высоту
        // ограничиваем, а прокрутку содержимое организует само (так у него остаётся
        // возможность держать прилипший футер вне скролла)
        ...(anchorSide ? { maxHeight, overflow: 'hidden' } : null),
      }}>
        {children}
      </div>
    </>
  );
  // Обычный режим остаётся на месте: он позиционируется absolute относительно
  // своего родителя, и портал оторвал бы его от точки отсчёта
  return anchor ? createPortal(card, document.body) : card;
}

// Разделитель между смысловыми группами пунктов меню.
export function MenuSep() {
  return <div style={{ height: 1, background: C.borderLight, margin: '4px 6px' }} />;
}

// Единый пункт выпадающего меню.
export function MenuItem({ icon, label, onClick, danger, disabled, wrapper, action }: {
  icon?: ReactNode;
  label: ReactNode;
  onClick?: (e: MouseEvent) => void;
  danger?: boolean;
  disabled?: boolean;
  // Атрибуты обёртки пункта: ручка перетаскивания (draggable + обработчики drag'а)
  // у строк, которые можно вытащить из меню. Как у RailIconButton — дырявить API
  // самой кнопки ради этого не стоит.
  wrapper?: HTMLAttributes<HTMLElement>;
  // Второе действие строки — кнопка-иконка справа (у своего пункта меню есть и
  // основной клик, и побочная команда). Отдельной кнопкой, а не иконкой ВНУТРИ
  // пункта: <button> в <button> вложить нельзя, поэтому строка становится
  // flex-обёрткой, а подсветка наведения переезжает на неё.
  action?: { icon: ReactNode; title: string; onClick: () => void };
}) {
  const [hover, setHover] = useState(false);
  const color = disabled ? C.textMuted : (danger ? C.danger : C.textPrimary);
  const hovered = hover && !disabled;
  const style: CSSProperties = {
    display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
    // При действии-спутнике фон рисует обёртка: иначе подсветка обрывалась бы
    // ровно перед кнопкой справа
    background: hovered && !action ? C.bgSelected : 'none', border: 'none', borderRadius: R.md,
    padding: '9px 10px', cursor: disabled ? 'default' : 'pointer', color, fontSize: 13.5, fontFamily: FONT.sans,
    ...(action ? { flex: 1, minWidth: 0, paddingRight: 4 } : null),
  };
  const item = (
    <button
      onClick={onClick}
      disabled={disabled}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={style}
    >
      {icon && (
        <span style={{ display: 'inline-flex', alignItems: 'center', width: 15, height: 15, flexShrink: 0, color: 'inherit' }}>
          {icon}
        </span>
      )}
      {label}
    </button>
  );
  const row = action ? (
    <span
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', width: '100%', minWidth: 0,
        borderRadius: R.md, paddingRight: 4,
        background: hovered ? C.bgSelected : 'none',
      }}
    >
      {item}
      <IconButton size="xs" title={action.title} onClick={e => { e.stopPropagation(); action.onClick(); }}>
        {action.icon}
      </IconButton>
    </span>
  ) : item;
  // Обёртка только когда её просят: лишний span в разметке меню ни к чему
  return wrapper ? <span {...wrapper} style={{ display: 'flex', ...wrapper.style }}>{row}</span> : row;
}
