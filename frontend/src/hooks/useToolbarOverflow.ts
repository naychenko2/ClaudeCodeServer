import { useState, useCallback, useLayoutEffect, useEffect, type RefObject } from 'react';

// Сколько одинаковых по ширине кнопок влезает в полосу контролов, чтобы она осталась
// в ОДНУ строку. Не влезшие вызывающий уводит в меню «⋯» — с конца списка, то есть
// справа налево.
//
// Считаем арифметикой от ширины полосы и номиналов трёх несжимаемых блоков
// (фиксированный левый, бейджи состояния, правая группа пикеров). Ширины этих блоков
// от результата НЕ зависят — поэтому пересчёт сходится и не зацикливается.
// Раньше блоки измерялись через offsetWidth, но badgesRef был сжимаемым (flexShrink:1),
// и его ширина зависела от того, сколько круглых кнопок мы уже показали — петля
// расходилась, и круглые кнопки оставались при нулевых пилюлях. Теперь badgesRef
// несжимаем (flexShrink:0), и его ширину мы подаём как константу.
interface Options {
  stripRef: RefObject<HTMLElement | null>;
  // Ширина фиксированного левого блока (modeButton и обвязка) — номинал по макету
  leftBlock: number;
  // Суммарная ширина показываемых бейджей состояния (teamPill + бейдж КР + loopPill).
  // 0, если ни один бейдж сейчас не активен — блок пуст.
  badgesWidth: number;
  // Ширина правой группы (модель + усилие + собеседник) в текущей форме —
  // A-wide / A / B / B2 / C. Номинал, не измерение.
  rightWidth: number;
  count: number;        // сколько кнопок можно сворачивать
  enabled: boolean;     // false — сворачивание выключено (широкий экран), видно всё
  itemWidth: number;
  gap: number;
  menuWidth: number;    // ширина кнопки «⋯»
  // Место под ГИБКОЕ содержимое строки, которое в замеряемые блоки не входит
  // (имя файла и т.п.): его ширина зависит от числа показанных кнопок, поэтому мерить
  // его нельзя — пересчёт зациклился бы. Резервируем константу и считаем от неё.
  reserve?: number;
  // Принудительно показать все кнопки из count (не прятать в ⋯). Используется, пока
  // правая группа ещё держит подписи — тогда приоритет «сначала сжатие правых»: левая
  // полоса остаётся в ряду, а правая группа сама уходит в иконочную форму по лестнице.
  // Как только правая группа в полностью иконочной форме C — флаг снимается, и левые
  // кнопки начинают уезжать в ⋯ по обычному бюджету
  forceAllVisible?: boolean;
}

export function useToolbarOverflow({
  stripRef, leftBlock, badgesWidth, rightWidth,
  count, enabled, itemWidth, gap, menuWidth, reserve = 0, forceAllVisible = false,
}: Options): number {
  const [visible, setVisible] = useState(count);

  const measure = useCallback(() => {
    if (!enabled || forceAllVisible) { setVisible(count); return; }
    const strip = stripRef.current;
    if (!strip) return;
    if (!strip.clientWidth) return;   // ещё не в лейауте — считать нечего
    // clientWidth включает собственные горизонтальные отступы полосы — вычитаем,
    // иначе на пару пикселей переоцениваем место и строка тихо вылезает за край
    const cs = getComputedStyle(strip);
    const total = strip.clientWidth - parseFloat(cs.paddingLeft || '0') - parseFloat(cs.paddingRight || '0');
    // Несжимаемые соседи: их ширина не зависит от того, сколько кнопок мы покажем
    const fixed = leftBlock + badgesWidth + rightWidth;
    // Каждая кнопка добавляет свою ширину + зазор перед собой. Плюс два зазора на
    // несжимаемые блоки справа (бейджи и группа пикеров) — они остаются отдельными
    // flex-детьми и съедают зазор, даже когда пусты.
    const avail = total - fixed - gap * 2 - reserve;
    const step = itemWidth + gap;
    if (avail >= count * step) { setVisible(count); return; }
    // Место под «⋯» резервируем, только если что-то реально прячем
    const fit = Math.floor((avail - (menuWidth + gap)) / step);
    setVisible(Math.max(0, Math.min(count, fit)));
  }, [enabled, forceAllVisible, count, itemWidth, gap, menuWidth, reserve, stripRef, leftBlock, badgesWidth, rightWidth]);

  // Пересчёт на КАЖДЫЙ рендер: первый layout может застать полосу недомеренной (панели
  // ещё раскладываются, аватар собеседника не загрузился), а разовый замер так и остался
  // бы стоять с заниженной шириной и прятал бы кнопки на пустом месте. Повторный вызов
  // с тем же результатом React гасит сам — итерация сходится за один-два прохода.
  // eslint-disable-next-line react-hooks/set-state-in-effect -- замер полосы после layout; итерация сходится, см. комментарий выше
  useLayoutEffect(measure);

  // Ширина полосы меняется вместе с окном и боковыми панелями. Размеры трёх блоков
  // здесь больше не отслеживаем: это номиналы, и на сжатие полосы они не реагируют —
  // ровно поэтому и сходится пересчёт.
  useEffect(() => {
    const strip = stripRef.current;
    if (!strip || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(strip);
    return () => ro.disconnect();
  }, [measure, stripRef]);

  return enabled ? visible : count;
}
