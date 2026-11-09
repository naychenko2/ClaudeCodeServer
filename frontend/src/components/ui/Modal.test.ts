// Слот header у Modal: шапка тела вне прокрутки. Ложное значение (header={cond && <X/>}
// при ложном cond даёт false) шапку не рисует, а заголовок остаётся в теле, как без слота.
// Окружение node: рендерим в строку, как прочие компонентные тесты проекта.
// Раскладка рисует ДВЕ ветки с разными обёртками шапки (десктоп: карточка .cc-modal-card,
// мобила: шторка .cc-sheet-card, Modal.tsx:215) — покрытие нужно обеих, иначе мобильная
// строка не проверяется ни одним кейсом.
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// renderToStaticMarkup порталов не знает: createPortal → pass-through (как в ConsentDialog.test)
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

// Мобильную ветку НЕЛЬЗЯ включить моком matchMedia: в статическом рендере
// useSyncExternalStore берёт getServerSnapshot, а у useMediaQuery он жёстко () => false
// (hooks/useMediaQuery.ts:22) — прогон с matches:true оставил десктопную карточку.
// Точка, где Modal выбирает раскладку, — хук (Modal.tsx:48), поэтому мокаем его:
// флаг читается в момент вызова, каждый кейс ставит его сам (мок — по всему файлу).
let isMobile = false;
vi.mock('./useIsMobileModal', () => ({ useIsMobileModal: () => isMobile }));

// Окно и документ — только среда рендера (портал, listeners); на раскладку не влияют.
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

describe('Modal: слот header (десктопная карточка)', () => {
  beforeEach(() => { isMobile = false; });

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

describe('Modal: слот header (мобильная шторка)', () => {
  // isMobile=true → Modal рисует ветку .cc-sheet-card (Modal.tsx:180-243)
  beforeEach(() => { isMobile = true; });

  it('мобильная раскладка: рендерится шторка, а не карточка', () => {
    const html = render(false);
    expect(html).toContain('cc-sheet-card');
    expect(html).not.toContain('cc-modal-card');
  });

  it('header={false}: обёртки шапки нет, заголовок остаётся в скролл-теле', () => {
    const html = render(false);
    expect(html).not.toContain(HEADER_WRAPPER);
    // На мобиле headerRow = один titleBlock (крестик — в drag-строке сверху), и без слота
    // шапки он уезжает в скролл-блок: заголовок не потерян, но и не в обёртке
    expect(html).toContain('Заголовок');
    expect(html).toContain('cc-sheet-content');
    expect(html.indexOf('Заголовок')).toBeGreaterThan(html.indexOf('cc-sheet-content'));
  });

  it('header={узел}: обёртка шапки есть, заголовок и слот — в ней, вне скролла', () => {
    const html = render(createElement('input', { placeholder: 'поиск' }));
    expect(html).toContain(HEADER_WRAPPER);
    expect(html).toContain('placeholder="поиск"');
    // Заголовок теперь в обёртке шапки — раньше скролл-блока с телом
    expect(html.indexOf('Заголовок')).toBeLessThan(html.indexOf('cc-sheet-content'));
  });
});
