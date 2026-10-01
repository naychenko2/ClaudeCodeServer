import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Окружение node: окно с matchMedia для useIsMobile; шторку рисуем по layout явно
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 360, innerHeight: 740,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { GenerationPanel } = await import('./GenerationPanel');
const { PanelHeaderSlotContext } = await import('../ui/panelHeaderSlotContext');

const foot = { count: 1, maxCount: 1 as const, price: ['≈ $0.04', '1 × $0.04 за картинку'] as [string, string], runLabel: 'Изменить', onRun: () => {} };
const panel = (extra: Record<string, unknown> = {}) => createElement(GenerationPanel<'a'>, {
  title: 'Картинки', icon: null, tabs: [{ value: 'a', label: 'Настройки' }], tab: 'a', onTabChange: () => {},
  foot, peekSummary: 'hero.png · fal', onClose: () => {}, layout: 'sheet', children: 'тело', ...extra,
});
const inShell = (el: ReturnType<typeof panel>) => createElement(PanelHeaderSlotContext.Provider, {
  value: { hasHeader: true, el: null, elLeft: null, elPinned: null, hold: () => {} },
}, el);
const count = (html: string, s: string) => html.split(s).length - 1;

describe('шторка каркаса панели генерации', () => {
  it('вне оболочки: шторка 88 % со своей шапкой и кнопкой «опустить до цены»', () => {
    const html = renderToStaticMarkup(panel());
    expect(html).toContain('role="dialog"');
    expect(html).toContain('height:88%');
    expect(html).toContain('Опустить до цены — лента станет доступна');
    expect(html).toContain('тело');
  });

  it('опущенная: шапка, сводка и низ с ценой, без тела и затемнения', () => {
    const html = renderToStaticMarkup(panel({ peeked: true }));
    expect(html).toContain('Поднять шторку');
    expect(html).toContain('hero.png · fal');
    expect(html).toContain('≈ $0.04');
    expect(html).not.toContain('тело');
    expect(html).not.toContain('height:88%');
  });

  it('внутри оболочки зоны шторки нет: одна шапка — оболочки, своей каркас не рисует', () => {
    const html = renderToStaticMarkup(inShell(panel()));
    expect(html).not.toContain('role="dialog"');
    expect(count(html, 'Закрыть панель — сводка останется в полосе')).toBe(0);
    expect(html).not.toContain('Опустить до цены');
    expect(html).toContain('тело');
  });
});
