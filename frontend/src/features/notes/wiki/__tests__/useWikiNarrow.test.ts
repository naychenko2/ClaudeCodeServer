// M0: решение «узко» для панелей вики (принцип П1): узко = ширина контейнера < MOBILE_MAX
// или мобильное устройство. Порог ровно MOBILE_MAX (600): при равном — НЕ узко.

import { describe, expect, it } from 'vitest';
import { isNarrow } from '../useWikiNarrow';

describe('isNarrow: узкий контейнер или мобильное устройство', () => {
  it('контейнер уже MOBILE_MAX → узко', () => {
    expect(isNarrow(340, false)).toBe(true);   // типичная правая зона панели вики
    expect(isNarrow(599, false)).toBe(true);   // последний узкий случай
  });

  it('контейнер ровно MOBILE_MAX → не узко (порог строгий)', () => {
    expect(isNarrow(600, false)).toBe(false);
  });

  it('замера ещё нет → не узко (широкая раскладка по умолчанию)', () => {
    expect(isNarrow(null, false)).toBe(false);
  });

  it('мобильное устройство → узко и без замера', () => {
    expect(isNarrow(null, true)).toBe(true);
    expect(isNarrow(1200, true)).toBe(true);  // и при широком окне на телефоне
  });
});
