// Мобильная шапка проекта: сколько compact-пилюль вкладок влезает в ряд.
//
// Прежний расчёт резервировал место только под кнопку проекта и «Ещё» — детские слоты
// шапки (плашка времени и меню ребёнка) не учитывал. PillSwitch не сжимается
// (flexShrink:0), при переполнении ряда правые элементы шапки выдавливало за край
// (L3 аудита 21.11, дефект 36e0160d).
//
// Чистая функция, отдельно от DOM — так её видно тестам без рендера WorkspacePage.
export function computeTabsFit(input: {
  // Внутренняя ширина шапки: clientWidth минус паддинги
  avail: number;
  // Ширины компакт-пилюль по порядку (offsetWidth скрытого эталона)
  pillWidths: number[];
  // Ширина кнопки «Ещё» (offsetWidth); кнопки нет — 0
  moreW: number;
  // Резерв детских слотов после пилюль — плашка времени и меню ребёнка, каждый
  // со своим зазором шапки. Взрослый экран — 0.
  childReserve: number;
}): number {
  // 64 — минимум под кнопку проекта (имя с эллипсисом), 14 — зазоры back|pills|⋯
  const budget = input.avail - 64 - input.moreW - 14 - input.childReserve;
  let used = 6; // внутренние отступы трека PillSwitch (padding 3×2)
  let fit = 0;
  for (let i = 0; i < input.pillWidths.length; i++) {
    const w = input.pillWidths[i] + (i > 0 ? 3 : 0);
    if (used + w <= budget) { used += w; fit++; } else break;
  }
  // Минимум одна пилюля — как было: ряд вкладок скрыть нельзя.
  return Math.max(1, Math.min(input.pillWidths.length, fit));
}
