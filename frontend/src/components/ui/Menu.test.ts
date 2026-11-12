// Слот header у Menu: шапка вне прокрутки. Ложное значение (header={cond && <X/>}
// при ложном cond даёт false) шапку не рисует, а содержимое идёт списком.
// Окружение node: рендерим в строку, как прочие компонентные тесты проекта.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// renderToStaticMarkup порталов не знает: createPortal → pass-through (как в Modal.test)
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.stubGlobal('document', {
  body: { nodeType: 1 },
  head: { appendChild: () => {} },
  documentElement: { dataset: {} },
  createElement: () => ({ id: '', textContent: '' }),
  getElementById: () => ({}),
  querySelector: () => null,
  addEventListener: () => {},
  removeEventListener: () => {},
  activeElement: null,
});

const { Menu } = await import('./Menu');

// Обёртка шапки меню — маркер по классу, а не по отступу: смена отступа не поведение
const HEADER_WRAPPER = 'cc-menu-header';

const render = (header: unknown) =>
  renderToStaticMarkup(createElement(Menu, {
    onClose: () => {}, header: header as never,
  }, 'пункты'));

describe('Menu: слот header', () => {
  it('header={false}: обёртки шапки нет, содержимое идёт списком', () => {
    const html = render(false);
    expect(html).not.toContain(HEADER_WRAPPER);
    expect(html).toContain('пункты');
  });

  it('header не передан или null: то же, что false', () => {
    expect(render(undefined)).toBe(render(false));
    expect(render(null)).toBe(render(false));
  });

  it('header={узел}: узел уходит в обёртку шапки, содержимое — под ней', () => {
    const html = render(createElement('input', { placeholder: 'поиск' }));
    expect(html).toContain(HEADER_WRAPPER);
    expect(html).toContain('placeholder="поиск"');
    // Содержимое — после обёртки шапки: шапка стоит вне прокрутки, список под ней
    expect(html.indexOf('пункты')).toBeGreaterThan(html.indexOf(HEADER_WRAPPER));
  });
});
