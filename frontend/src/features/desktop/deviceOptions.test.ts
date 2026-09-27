import { describe, it, expect } from 'vitest';
import { devicesMenuVisible } from './deviceOptions';

describe('devicesMenuVisible', () => {
  it('только local-projects — пункт «Устройства» есть', () => {
    expect(devicesMenuVisible(false, true)).toBe(true);
  });
  it('только desktop-agent — пункт есть', () => {
    expect(devicesMenuVisible(true, false)).toBe(true);
  });
  it('оба флага — пункт есть', () => {
    expect(devicesMenuVisible(true, true)).toBe(true);
  });
  it('ни одного флага — пункта нет', () => {
    expect(devicesMenuVisible(false, false)).toBe(false);
  });
});
