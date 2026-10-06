import { describe, expect, it, vi } from 'vitest';
import { handleMenuEscape } from './Menu';
import { getPopupDepth } from '../../lib/popupEscape';

const key = (k: string, defaultPrevented = false) => ({ key: k, defaultPrevented, preventDefault: vi.fn() });

describe('Menu — закрытие по Escape', () => {
  it('Escape закрывает меню, гасит событие и возвращает фокус на якорь', () => {
    const onClose = vi.fn();
    const restore = vi.fn();
    const e = key('Escape');
    expect(handleMenuEscape(e, onClose, restore)).toBe(true);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(restore).toHaveBeenCalledTimes(1);
    expect(e.preventDefault).toHaveBeenCalledTimes(1);
  });

  it('другие клавиши меню не закрывают', () => {
    const onClose = vi.fn();
    const restore = vi.fn();
    expect(handleMenuEscape(key('Enter'), onClose, restore)).toBe(false);
    expect(handleMenuEscape(key('ArrowDown'), onClose, restore)).toBe(false);
    expect(onClose).not.toHaveBeenCalled();
    expect(restore).not.toHaveBeenCalled();
  });

  it('Escape, уже обработанный кем-то выше, меню не трогает', () => {
    const onClose = vi.fn();
    expect(handleMenuEscape(key('Escape', true), onClose, vi.fn())).toBe(false);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('счётчик попапов в покое нулевой (меню без монтирования его не держит)', () => {
    expect(getPopupDepth()).toBe(0);
  });
});
