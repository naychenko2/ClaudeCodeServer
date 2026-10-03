import { describe, expect, it } from 'vitest';
import { canSplitCenter, CHAT_SPLIT_FLOOR_PX, SPLIT_SIDE_MIN_PX } from './splitLayout';

describe('файл рядом с чатом: порог ширины центра', () => {
  const edge = CHAT_SPLIT_FLOOR_PX + SPLIT_SIDE_MIN_PX;

  it('чату остаётся 320 и больше — файл открывается рядом', () => {
    expect(canSplitCenter(edge)).toBe(true);
    expect(canSplitCenter(1000)).toBe(true);
  });

  it('чату остаётся меньше 320 (центр 1280 с закреплёнными «Чатами» и панелью ≈ 460) — файл во весь центр', () => {
    expect(canSplitCenter(edge - 1)).toBe(false);
    expect(canSplitCenter(460)).toBe(false);
  });

  it('ширина ещё не замерена — раскладка не мигает', () => {
    expect(canSplitCenter(0)).toBe(true);
  });
});
