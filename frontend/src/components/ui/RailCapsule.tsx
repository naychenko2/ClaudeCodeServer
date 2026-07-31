import type { CSSProperties, HTMLAttributes, ReactNode } from 'react';
import { C, ISLAND } from '../../lib/design';

// Полукапсула-остров у края окна — общая оправа ВСЕХ вертикальных рельс: и рельсы
// панелей, и дока проектов под ней. Раньше геометрию рисовал каждый сам, и правка
// радиуса или тени в одном месте молча расходилась со вторым.
//
// Скруглена и обведена только сторона, обращённая к центру; прижатая к краю окна —
// прямая и без бордера. Вертикальный отступ подобран так, чтобы капсула с ОДНОЙ
// иконкой была ровно в высоту шапки панели (ISLAND.headerH), а центр первой кнопки
// сел на линию её заголовка.

// Ширина рельсы и зазор между рельсой и зоной панелей. Значения общие: рельсы
// обязаны быть зеркальны, иначе одна зона визуально «толще» другой.
export const RAIL_W = 40;
// Зазор 8, а не 4: в него встаёт крайняя направляющая места вставки (толщина 2 плюс
// отступ от кромки панели). При 4 она прижималась к рельсе вплотную и читалась как
// её граница, а не как «сюда встанет колонка».
export const RAIL_GAP = 8;
// Зазор между кнопками внутри капсулы
export const RAIL_ITEM_GAP = 6;

interface Props extends HTMLAttributes<HTMLDivElement> {
  // Сторона окна: разворачивает скругления, бордер и отступ до центра
  side: 'left' | 'right';
  // false — капсула плавно схлопывается (width→0, opacity→0), оставаясь в DOM.
  // Схлопывается и по ВЫСОТЕ вместе с паддингами: под рельсой может стоять второй
  // остров, и невидимая капсула держала бы его на отступе от верха — на пустом месте.
  visible?: boolean;
  // Зазор со стороны центра. Обычно его даёт зона панелей, но при закрытых панелях
  // задаётся здесь — иначе капсула липнет к контенту.
  gapToCenter?: number;
  // Обводка целиком одной строкой: React запрещает мешать сокращённые свойства с
  // посторонними (borderTop и т.п.) — снимая одно, он не восстанавливает другое.
  // Отсюда же и приём вызывающего: пунктирная мишень дропа задаётся ЦЕЛОЙ строкой.
  border?: string;
  background?: string;
  style?: CSSProperties;
  children: ReactNode;
}

export function RailCapsule({
  side, visible = true, gapToCenter = 0, border, background, style, children, ...rest
}: Props) {
  const isLeft = side === 'left';
  // Схлопнутая капсула не должна оставлять после себя ни линии, ни полоски padding
  const line = !visible ? '0 none transparent' : border ?? `1px solid ${C.border}`;
  return (
    <div
      {...rest}
      style={{
        width: visible ? RAIL_W : 0,
        opacity: visible ? 1 : 0,
        pointerEvents: visible ? 'auto' : 'none',
        maxHeight: visible ? undefined : 0,
        transition: 'width 0.15s ease-out, opacity 0.12s ease-out',
        flexShrink: 0, position: 'relative',
        display: 'flex', flexDirection: 'column', alignItems: 'center',
        gap: RAIL_ITEM_GAP,
        paddingTop: visible ? 4 : 0, paddingBottom: visible ? 4 : 0,
        // Тон шапок островов и сайдбаров — единая «оправа» интерфейса
        background: background ?? C.bgMain,
        borderTop: line, borderBottom: line,
        boxSizing: 'border-box', overflow: 'hidden',
        boxShadow: ISLAND.shadow,
        ...(isLeft
          ? {
              borderRight: line,
              borderTopRightRadius: ISLAND.radius, borderBottomRightRadius: ISLAND.radius,
              marginRight: gapToCenter,
            }
          : {
              borderLeft: line,
              borderTopLeftRadius: ISLAND.radius, borderBottomLeftRadius: ISLAND.radius,
              marginLeft: gapToCenter,
            }),
        ...style,
      }}
    >
      {children}
    </div>
  );
}
