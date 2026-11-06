// Слот header у Modal: шапка тела вне прокрутки. Ложное значение (header={cond && <X/>}
// при ложном cond даёт false) шапку не рисует, а заголовок остаётся в теле, как без слота.
// Окружение node: рендерим в строку, как прочие компонентные тесты проекта.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// renderToStaticMarkup порталов не знает: createPortal → pass-through (как в ConsentDialog.test)
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
});

const { Modal } = await import('./Modal');

// Обёртка шапки тела — маркер по классу: отступ шапки меняется без смены поведения
const HEADER_WRAPPER = 'cc-modal-header';

const render = (header: unknown) =>
  renderToStaticMarkup(createElement(Modal, {
    title: 'Заголовок', onClose: () => {}, header: header as never,
  }, 'тело'));

describe('Modal: слот header', () => {
  it('header={false}: шапки нет, заголовок остаётся в теле', () => {
    const html = render(false);
    expect(html).not.toContain(HEADER_WRAPPER);
    // headerRow (заголовок) не потерян и стоит внутри контента, а не в обёртке шапки
    expect(html).toContain('Заголовок');
    expect(html).toContain('cc-modal-content');
    expect(html.indexOf('Заголовок')).toBeGreaterThan(html.indexOf('cc-modal-content'));
  });

  it('header не передан или null: то же, что false', () => {
    expect(render(undefined)).toBe(render(false));
    expect(render(null)).toBe(render(false));
  });

  it('header={узел}: узел и заголовок уходят в обёртку шапки вне прокрутки', () => {
    const html = render(createElement('input', { placeholder: 'поиск' }));
    expect(html).toContain(HEADER_WRAPPER);
    expect(html).toContain('placeholder="поиск"');
    // Заголовок теперь в обёртке шапки — раньше контента с телом
    expect(html.indexOf('Заголовок')).toBeLessThan(html.indexOf('cc-modal-content'));
  });
});
