import { useCallback, useLayoutEffect, useRef, useState } from 'react';

// Удержание центральной колонки по центру ОКНА, а не по центру остатка между зонами
// панелей.
//
// Задача. Центр (лента чата, список проектов, текст заметки) ограничен своей шириной
// и центрируется `margin: 0 auto` внутри колонки, которая осталась от зон панелей.
// Пока зоны симметричны, это и есть центр окна. Но стоит открыть панель слева —
// колонка сдвигается, и вместе с ней уезжает вправо весь контент, хотя свободного
// места справа вагон. Глаз читает это как «интерфейс перекосило».
//
// Решение. Колонке добавляется компенсирующий padding с той стороны, где панелей
// МЕНЬШЕ: он сужает область центрирования ровно настолько, чтобы её середина снова
// совпала с серединой окна. Контент внутри при этом ничего не знает о компенсации —
// он как центрировался своим `margin: 0 auto`, так и центрируется.
//
// Арифметика. Пусть L и R — занятое зонами слева и справа, Wc — ширина колонки.
// Центр колонки стоит в (Wc + L − R) / 2 от левого края окна, центр окна — в
// (L + Wc + R) / 2. Разница — (L − R) / 2, и убирает её padding = L − R с той
// стороны, где зона уже.
//
// Ограничение. Съесть можно только то, что колонка НЕ отдала контенту: запас
// slack = Wc − contentWidth. Больше — и контент начнёт сжиматься, а сжатая ради
// симметрии лента хуже несимметричной. Поэтому компенсация обрезается по slack и
// на узком окне честно вырождается в ноль.
// Сам расчёт — чистой функцией, отдельно от DOM: так его видно тестам.
// Возврат: >0 — поджать колонку справа на столько пикселей, <0 — слева, 0 — не трогать.
export function computeCenterShift(m: {
  rootLeft: number; rootRight: number;
  centerLeft: number; centerRight: number;
  contentWidth: number;
}): number {
  const colWidth = m.centerRight - m.centerLeft;
  // Запас — то, что колонка не отдала контенту. Съедать больше нельзя: контент начнёт сжиматься
  const slack = Math.max(0, colWidth - m.contentWidth);
  const skew = (m.centerLeft - m.rootLeft) - (m.rootRight - m.centerRight);
  return Math.max(-slack, Math.min(skew, slack));
}

export interface CenterOffset {
  // На корень раскладки (внутри него лежат обе зоны и колонка центра)
  rootRef: (el: HTMLElement | null) => void;
  // На саму центральную колонку
  centerRef: (el: HTMLElement | null) => void;
}

// contentWidth — ширина контента внутри колонки (CHAT_MAX_W и т.п.). Не передана —
// центр резиновый, компенсировать нечего: хук выключается.
export function useCenterOffset(contentWidth?: number): CenterOffset {
  const rootEl = useRef<HTMLElement | null>(null);
  const centerEl = useRef<HTMLElement | null>(null);
  // Версия узлов: меняется, когда любой из ref'ов получил новый элемент, — это и
  // перезапускает эффект с наблюдателем (сами ref'ы реактивность не дают)
  const [nodes, setNodes] = useState(0);

  const rootRef = useCallback((el: HTMLElement | null) => {
    if (rootEl.current === el) return;
    rootEl.current = el;
    setNodes(v => v + 1);
  }, []);
  const centerRef = useCallback((el: HTMLElement | null) => {
    if (centerEl.current === el) return;
    centerEl.current = el;
    setNodes(v => v + 1);
  }, []);

  useLayoutEffect(() => {
    const root = rootEl.current;
    const center = centerEl.current;
    if (!center) return;

    const clear = () => { center.style.paddingLeft = ''; center.style.paddingRight = ''; };
    if (!contentWidth || !root) { clear(); return; }

    // Стиль правим ПРЯМО ЗДЕСЬ, а не через состояние React. Колбэк ResizeObserver
    // выполняется после раскладки, но ДО отрисовки кадра, поэтому изменение padding
    // попадает в тот же кадр, что и новая ширина зоны. Через setState компенсация
    // применялась бы только следующим кадром — центр успевал мотнуться в сторону и
    // вернуться, что и читалось как рывок при открытии панели.
    //
    // Меряем ВНЕШНИЕ границы: они не зависят от padding, который сами же ставим,
    // поэтому наблюдатель не гоняется за собственным результатом.
    const apply = () => {
      const r = root.getBoundingClientRect();
      const c = center.getBoundingClientRect();
      const shift = computeCenterShift({
        rootLeft: r.left, rootRight: r.right,
        centerLeft: c.left, centerRight: c.right,
        contentWidth,
      });
      center.style.paddingRight = shift > 0 ? `${shift}px` : '';
      center.style.paddingLeft = shift < 0 ? `${-shift}px` : '';
    };

    const ro = new ResizeObserver(apply);
    ro.observe(root);
    ro.observe(center);
    return () => { ro.disconnect(); clear(); };
  }, [contentWidth, nodes]);

  return { rootRef, centerRef };
}
