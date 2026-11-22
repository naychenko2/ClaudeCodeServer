// computeTabsFit — расчёт числа видимых вкладок мобильной шапки проекта.
// Дефект 36e0160d: у ребёнка в шапке ещё плашка времени и меню — прежний расчёт их
// не резервировал, PillSwitch (flexShrink:0) не сжимается, при переполнении ряда
// правые элементы шапки выдавливало за край.

import { describe, it, expect } from 'vitest';
import { computeTabsFit } from './mobileTabsFit';

describe('computeTabsFit · мобильная шапка проекта', () => {
  // 360px телефон: шапка 360 − 28 = 332. Пилюли: «Чаты» с подписью ~80, пять иконок ~40.
  // Кнопка «Ещё» 32; плашка времени ~70 и меню ребёнка 32, перед каждым зазор 10.
  const pills = [80, 40, 40, 40, 40, 40];
  const reserve = 70 + 10 + 32 + 10;

  it('взрослый экран: без детских слотов расчёт как раньше', () => {
    // 332 − 64 − 32 − 14 = 222: 80 → 86, +43=129, +43=172, +43=215, +43=258 > 222 → 4
    expect(computeTabsFit({ avail: 332, pillWidths: pills, moreW: 32, childReserve: 0 })).toBe(4);
  });

  it('детский экран на 360: резерв детских слотов оставляет ряд влезающим', () => {
    const fit = computeTabsFit({ avail: 332, pillWidths: pills, moreW: 32, childReserve: reserve });
    // 332 − 64 − 32 − 14 − 122 = 100: 80 → 86 ≤ 100 → 1, +43 = 129 > 100 → fit=1
    expect(fit).toBe(1);
    // Сумма фиксированных слотов и видимых пилюль не превышает шапку
    const visibleSum = 6 + 80; // трек 6 + первая пилюля
    expect(64 + 32 + reserve + visibleSum).toBeLessThanOrEqual(332);
  });

  it('детский экран на широком окне: больше пилюль, чем на 360', () => {
    const fit = computeTabsFit({ avail: 800, pillWidths: pills, moreW: 32, childReserve: reserve });
    expect(fit).toBe(6);
  });

  it('при тесноте минимум одна пилюля', () => {
    expect(computeTabsFit({ avail: 100, pillWidths: pills, moreW: 32, childReserve: 300 })).toBe(1);
  });
});
