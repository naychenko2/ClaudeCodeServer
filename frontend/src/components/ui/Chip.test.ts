// Фокусируемый чип: Enter и Space = клик, остальные клавиши — нет; без keyboard атрибутов нет.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { Chip, chipKeyboardProps } from './Chip';

const press = (key: string) => {
  const onClick = vi.fn(), preventDefault = vi.fn();
  chipKeyboardProps(onClick).onKeyDown({ key, preventDefault });
  return { onClick, preventDefault };
};

describe('Chip keyboard', () => {
  it('Enter и Space кликают и гасят прокрутку', () => {
    for (const k of ['Enter', ' ']) {
      const r = press(k);
      expect(r.onClick).toHaveBeenCalledOnce();
      expect(r.preventDefault).toHaveBeenCalled();
    }
  });
  it('другие клавиши не кликают', () => expect(press('a').onClick).not.toHaveBeenCalled());
  it('с keyboard — role=button и tabindex=0; без него и без onClick — нет', () => {
    const on = renderToStaticMarkup(createElement(Chip, { keyboard: true, onClick: () => {} }, 'x'));
    expect(on).toContain('role="button"');
    expect(on).toContain('tabindex="0"');
    expect(renderToStaticMarkup(createElement(Chip, { onClick: () => {} }, 'x'))).not.toContain('tabindex');
    expect(renderToStaticMarkup(createElement(Chip, { keyboard: true }, 'x'))).not.toContain('tabindex');
  });
});
