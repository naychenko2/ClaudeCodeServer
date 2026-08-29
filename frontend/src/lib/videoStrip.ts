// Раскладка полосы каналов: что помещается в шапку, а что уходит под «⋯».
//
// Вынесено из компонента отдельной чистой функцией не ради красоты: правило «активный
// канал виден всегда» невозможно проверить глазами во всех сочетаниях ширин, а сломать
// его легко — и тогда человек не видит, что вообще играет, пока не откроет попап.

/** Зазор между кнопками полосы (px). Тот же, что в разметке — расчёт обязан совпадать. */
export const STRIP_GAP = 3;

export interface StripFit {
  /** Индексы каналов, которые рисуются кнопками, в исходном порядке. */
  visible: number[];
  /** Индексы, ушедшие в попап, в исходном порядке. */
  hidden: number[];
}

/**
 * Что влезает в полосу ширины <c>containerW</c>.
 *
 * @param widths ширины кнопок в порядке каналов
 * @param containerW доступная ширина полосы
 * @param moreW ширина кнопки «⋯» (её место резервируем, как только что-то не влезло)
 * @param activeIndex индекс играющего канала; -1 — не выбран
 *
 * Активный канал виден ВСЕГДА, даже если по порядку он далеко: полоса отвечает на
 * вопрос «что сейчас идёт», и спрятать ответ в попап — то же, что не ответить.
 * Ради него вытесняется хвост видимых, а не начало: слева каналы, которые человек
 * поставил первыми, и терять их привычные места на каждой смене канала нельзя.
 */
export function fitStrip(
  widths: number[],
  containerW: number,
  moreW: number,
  activeIndex: number,
): StripFit {
  const all = widths.map((_, i) => i);
  if (widths.length === 0) return { visible: [], hidden: [] };

  const total = widths.reduce((sum, w) => sum + w, 0) + STRIP_GAP * (widths.length - 1);
  // Влезло целиком — «⋯» не нужна вовсе
  if (total <= containerW) return { visible: all, hidden: [] };

  // Не влезло: место под «⋯» резервируем сразу. Иначе набор кнопок занял бы всю ширину,
  // а кнопке попапа осталось бы отрицательное место — и она вытолкнула бы последнюю кнопку.
  const budget = containerW - moreW - STRIP_GAP;

  const visible: number[] = [];
  let used = 0;
  for (let i = 0; i < widths.length; i++) {
    const need = widths[i] + (visible.length > 0 ? STRIP_GAP : 0);
    if (used + need > budget) break;
    visible.push(i);
    used += need;
  }

  // Активный оказался за краем — освобождаем ему место, снимая кнопки с хвоста
  if (activeIndex >= 0 && activeIndex < widths.length && !visible.includes(activeIndex)) {
    let need = widths[activeIndex] + (visible.length > 0 ? STRIP_GAP : 0);
    while (visible.length > 0 && used + need > budget) {
      const dropped = visible.pop()!;
      used -= widths[dropped] + (visible.length > 0 ? STRIP_GAP : 0);
      need = widths[activeIndex] + (visible.length > 0 ? STRIP_GAP : 0);
    }
    // Влез даже в одиночку — ставим на место по порядку; не влез вовсе (полоса уже
    // сузилась ниже одной кнопки) — полоса остаётся пустой, всё уходит в попап
    if (used + need <= budget) {
      visible.push(activeIndex);
      visible.sort((a, b) => a - b);
    }
  }

  const shown = new Set(visible);
  return { visible, hidden: all.filter(i => !shown.has(i)) };
}
