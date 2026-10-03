import { describe, expect, it } from 'vitest';
import { brushModeActive } from './ComposerChip';
import { IMAGE_COMPOSER_MODE } from './imageMode';

describe('brushModeActive', () => {
  it('кисть «Отметить» видна только в режиме «Картинка»', () => {
    expect(brushModeActive(IMAGE_COMPOSER_MODE)).toBe(true);
    expect(brushModeActive(null)).toBe(false);
    expect(brushModeActive(undefined)).toBe(false);
    expect(brushModeActive('sound')).toBe(false);
  });
});
