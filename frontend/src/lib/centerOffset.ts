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

// contentWidth — сколько контент внутри колонки занимает ПО ГОРИЗОНТАЛИ целиком,
// вместе со своей обвязкой (у чата это CHAT_COLUMN_W: колонка чтения + боковое поле +
// место под полосу прокрутки, а не голый CHAT_MAX_W). Занизишь — хук сочтёт чужое
// место свободным запасом и отдаст его под padding, а контент начнёт сжиматься
// раньше времени, причём вдвое быстрее движения панели: колонка теряет пиксель на
// движении панели и ещё пиксель на выросшей компенсации.
// Не передана — центр резиновый, компенсировать нечего: хук выключается.
export function useCenterOffset(contentWidth?: number): CenterOffset {
  const rootEl = useRef<HTMLElement | null>(null);
  const centerEl = useRef<HTMLElement | null>(null);
  // Версия узлов: меняется, когда любой из ref'ов получил новый элемент, — это и
  // перезапускает эффект с наблюдателем (сами ref'ы реактивность не дают)
  const [nodes, setNodes] = useState(0);
  // Пересборка подписок наблюдателя — ею пользуется эффект «на каждый рендер» ниже.
  // null, когда компенсация выключена (нет ширины контента или узлов)
  const observeRef = useRef<(() => void) | null>(null);

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

    // Наблюдаем не только корень с колонкой, но и ЗОНЫ по бокам — прямых детей корня.
    // Без них ловится не всё: когда одна панель закрывается, а другая такой же ширины
    // открывается (перенос панели на другую сторону), ширина колонки не меняется ни на
    // одном кадре анимации — меняется только её положение, и наблюдатель за колонкой
    // молчит от начала до конца. Ширина же самих зон при этом ходит туда-сюда, и по ней
    // перекос виден. Подписки пересобираются на каждый рендер (состав зон меняется):
    // disconnect + observe заново сам даёт стартовый колбэк, то есть и пересчёт.
    const ro = new ResizeObserver(apply);
    const observeAll = () => {
      ro.disconnect();
      ro.observe(root);
      ro.observe(center);
      for (const zone of Array.from(root.children)) ro.observe(zone);
    };
    observeRef.current = observeAll;
    observeAll();
    return () => { observeRef.current = null; ro.disconnect(); clear(); };
  }, [contentWidth, nodes]);

  // Пересборка подписок на КАЖДЫЙ рендер: зоны — живые узлы, они появляются и исчезают
  // (панель открыли, перенесли на другую сторону, свернули все разом). Наблюдатель,
  // подписанный один раз при монтировании, после такой перестановки следил бы за
  // выброшенными узлами и не видел новых. Заодно это и пересчёт: disconnect + observe
  // всегда даёт стартовый колбэк. Эффект без зависимостей, порядок — после основного.
  useLayoutEffect(() => { observeRef.current?.(); });

  return { rootRef, centerRef };
}
