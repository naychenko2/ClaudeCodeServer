// Г11 (дефект 034360be, находка приёмки QA-8): на 768/1280 кнопки детской раскладки
// были 32×32 — тач-цель меньше 40 (docs/design/guidelines.md). Правка: PanelZone
// ребёнка передаёт PanelRail itemSize='lg' (40px-кнопки), и та же ширина держит
// капсулу (RAIL_W_LG=48): 40px-кнопка в 40px-полосе обрезалась бы краем (внутренняя
// ширина 38). Взрослый вызов (itemSize не задан) — прежнее: 32 в 40.
// Окружение — node + linkedom, как в RailHat.test.tsx; размеры читаются по
// inline-стилю (в linkedom именованные свойства CSSStyleDeclaration не открыты,
// сериализованный style-атрибут — да).

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import { MessageSquare } from 'lucide-react';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({
    matches: false, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { PanelRail, RAIL_W, RAIL_W_LG } = await import('./PanelRail');
import type { RailItem } from './PanelRail';

const item = (key: string): RailItem => ({
  key, title: key === 'chats' ? 'Чаты' : 'Файлы', Icon: MessageSquare,
  active: false, onClick: () => {},
});

async function render(props: Parameters<typeof PanelRail>[0]) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(PanelRail, props)); });
  await act(async () => { await Promise.resolve(); });
  return host as HTMLElement;
}

const num = (style: string, prop: string): number =>
  Number(style.match(new RegExp(`(?:^|[^-])${prop}:\\s*([\\d.]+)px`))?.[1] ?? 0);

const buttons = (host: HTMLElement) => Array.from(host.querySelectorAll('button'));

// Капсула — ПЕРВЫЙ элементный ребёнок корня рельсы (PanelRail.tsx:720-725: первыми
// детьми корня идут только она; футер и превью — дальше). Ищем по структуре, а не
// по «первому div с явной шириной»: если в поддереве появится другой элемент с
// шириной, мы не начнём мерить не то. Ширина при этом НЕ признак поиска: она
// проверяется здесь же и краснит кейс, если капсула не там или не того размера.
const capsuleOf = (host: HTMLElement, expectedW: number): HTMLElement | null => {
  const root = host.firstElementChild as HTMLElement | null;
  const capsule = (root?.firstElementChild as HTMLElement | null) ?? null;
  const w = capsule ? num(capsule.getAttribute('style') ?? '', 'width') : 0;
  expect(w, `капсула (первый ребёнок корня рельсы) должна быть ${expectedW}px; фактическая ширина ${w}`).toBe(expectedW);
  return capsule;
};

describe('PanelRail: тач-цели детской рельсы (Г11)', () => {
  it('itemSize="lg" → кнопки панелей и ящик «Ещё» не меньше 40×40, капсула 48', async () => {
    const host = await render({
      side: 'right', hat: 'Панели',
      groups: [[item('chats'), item('files')]],
      overflow: { items: [] },
      itemSize: 'lg',
    });
    const btns = buttons(host);
    // 2 кнопки панелей + ящик «Ещё»
    expect(btns.length, 'кнопки рельсы').toBe(3);
    for (const btn of btns) {
      const attr = btn.getAttribute('style') ?? '';
      const w = num(attr, 'width');
      const h = num(attr, 'height');
      expect(w, `ширина кнопки ${btn.getAttribute('aria-label')}; style="${attr}"`).toBeGreaterThanOrEqual(40);
      expect(h, `высота кнопки ${btn.getAttribute('aria-label')}; style="${attr}"`).toBeGreaterThanOrEqual(40);
    }
    // Капсула — ширина под 40px-кнопку с полями (RAIL_W_LG): в прежних 40px
    // (внутренние 38) кнопка 40 обрезалась бы краем
    const capsule = capsuleOf(host, RAIL_W_LG);
    expect(capsule, 'капсула не найдена по структуре').toBeTruthy();
  });

  it('без itemSize (взрослая рельса) — прежние 32 в капсуле 40: взрослую раскладку не трогали', async () => {
    const host = await render({
      side: 'right', hat: 'Панели',
      groups: [[item('chats')]],
      overflow: { items: [item('files')] },
    });
    const btns = buttons(host);
    expect(btns.length, 'кнопки рельсы').toBe(2);
    for (const btn of btns) {
      const attr = btn.getAttribute('style') ?? '';
      expect(num(attr, 'width'), `ширина; style="${attr}"`).toBe(32);
      expect(num(attr, 'height'), `высота; style="${attr}"`).toBe(32);
    }
    const capsule = capsuleOf(host, RAIL_W);
    expect(capsule, 'капсула не найдена по структуре').toBeTruthy();
  });
});
