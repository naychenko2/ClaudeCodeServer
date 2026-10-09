import { describe, expect, it, vi } from 'vitest';
import { handlePressableKey } from './PressableArea';

const area = {};
const nested = {};
const key = (k: string, target: object) =>
  ({ key: k, target, currentTarget: area, preventDefault: vi.fn() }) as unknown as Parameters<typeof handlePressableKey>[0];

describe('PressableArea — клавиатурная активация', () => {
  it('Enter и Space на самой области вызывают onPress и гасят нативное действие', () => {
    for (const k of ['Enter', ' ']) {
      const onPress = vi.fn();
      const e = key(k, area);
      expect(handlePressableKey(e, onPress)).toBe(true);
      expect(onPress).toHaveBeenCalledWith(e);
      expect(e.preventDefault).toHaveBeenCalledTimes(1);
    }
  });

  it('Enter со вложенной кнопки не вызывает onPress и не мешает её нативной активации', () => {
    const onPress = vi.fn();
    const e = key('Enter', nested);
    expect(handlePressableKey(e, onPress)).toBe(false);
    expect(onPress).not.toHaveBeenCalled();
    // Без preventDefault браузер сам активирует вложенную кнопку (click по Enter)
    expect(e.preventDefault).not.toHaveBeenCalled();
  });

  it('прочие клавиши область не трогают', () => {
    const onPress = vi.fn();
    const e = key('ArrowDown', area);
    expect(handlePressableKey(e, onPress)).toBe(false);
    expect(onPress).not.toHaveBeenCalled();
    expect(e.preventDefault).not.toHaveBeenCalled();
  });
});
