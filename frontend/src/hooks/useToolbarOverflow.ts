import { useState, useCallback, useLayoutEffect, useEffect, type RefObject } from 'react';

// Сколько одинаковых по ширине кнопок влезает в полосу контролов, чтобы она осталась
// В ОДНУ строку. Не влезшие вызывающий уводит в меню «⋯» — с конца списка, то есть
// справа налево.
//
// Считаем арифметикой, а не измерением каждой кнопки: все сворачиваемые кнопки полосы
// квадратные и одного размера, поэтому достаточно ширины самой полосы и трёх несжимаемых
// блоков (левый фиксированный, чипы-бейджи, правая группа пикеров). Ширины этих блоков
// от результата НЕ зависят — поэтому пересчёт сходится и не зацикливается.
interface Options {
  stripRef: RefObject<HTMLElement | null>;
  fixedLeftRef: RefObject<HTMLElement | null>;
  badgesRef: RefObject<HTMLElement | null>;
  rightRef: RefObject<HTMLElement | null>;
  count: number;        // сколько кнопок можно сворачивать
  enabled: boolean;     // false — сворачивание выключено (широкий экран), видно всё
  itemWidth: number;
  gap: number;
  menuWidth: number;    // ширина кнопки «⋯»
}

export function useToolbarOverflow({
  stripRef, fixedLeftRef, badgesRef, rightRef,
  count, enabled, itemWidth, gap, menuWidth,
}: Options): number {
  const [visible, setVisible] = useState(count);

  const measure = useCallback(() => {
    if (!enabled) { setVisible(count); return; }
    const strip = stripRef.current;
    if (!strip) return;
    if (!strip.clientWidth) return;   // ещё не в лейауте — считать нечего
    // clientWidth включает собственные горизонтальные отступы полосы — вычитаем,
    // иначе на пару пикселей переоцениваем место и строка тихо вылезает за край
    const cs = getComputedStyle(strip);
    const total = strip.clientWidth - parseFloat(cs.paddingLeft || '0') - parseFloat(cs.paddingRight || '0');
    // Несжимаемые соседи: их ширина не зависит от того, сколько кнопок мы покажем
    const fixed = (fixedLeftRef.current?.offsetWidth ?? 0)
      + (badgesRef.current?.offsetWidth ?? 0)
      + (rightRef.current?.offsetWidth ?? 0);
    // Каждая кнопка добавляет свою ширину + зазор перед собой. Плюс два зазора на
    // несжимаемые блоки справа (чипы-бейджи и группа пикеров) — они остаются
    // отдельными flex-детьми и съедают зазор, даже когда пусты.
    const avail = total - fixed - gap * 2;
    const step = itemWidth + gap;
    if (avail >= count * step) { setVisible(count); return; }
    // Место под «⋯» резервируем, только если что-то реально прячем
    const fit = Math.floor((avail - (menuWidth + gap)) / step);
    setVisible(Math.max(0, Math.min(count, fit)));
  }, [enabled, count, itemWidth, gap, menuWidth, stripRef, fixedLeftRef, badgesRef, rightRef]);

  // Пересчёт на КАЖДЫЙ рендер: первый layout может застать полосу недомеренной (панели
  // ещё раскладываются, аватар собеседника не загрузился), а разовый замер так и остался
  // бы стоять с заниженной шириной и прятал бы кнопки на пустом месте. Повторный вызов
  // с тем же результатом React гасит сам — итерация сходится за один-два прохода.
  useLayoutEffect(measure);

  // Ширина полосы меняется вместе с окном и боковыми панелями
  useEffect(() => {
    const strip = stripRef.current;
    if (!strip || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(strip);
    if (fixedLeftRef.current) ro.observe(fixedLeftRef.current);
    if (badgesRef.current) ro.observe(badgesRef.current);
    if (rightRef.current) ro.observe(rightRef.current);
    return () => ro.disconnect();
  }, [measure, stripRef, fixedLeftRef, badgesRef, rightRef]);

  return enabled ? visible : count;
}
