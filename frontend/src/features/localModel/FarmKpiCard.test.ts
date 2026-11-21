// «Сейчас на ферме» (FarmKpiCard): чистый компонент — рендер только по пропу `data`.
// Проверяется: ровно четыре KPI в сетке 2×2 с подписями макета; окно '5m' берётся ПО
// КЛЮЧУ (при перевёрнутом порядке массива); отсутствующее / null / неfinite значение
// даёт «нет данных» в своей ячейке, ноль не подставляется и остальные KPI не скрываются;
// при 'yielded' все четыре ячейки — «нет данных», а не нули снимка; очередь > 0 красит
// только число (C.warningText), подпись остаётся обычной.
// DOM-паттерн — как TextTab.test.ts: linkedom + createRoot, mount с размонтированием.

import { describe, expect, it, vi } from 'vitest';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
// usePrefersReducedMotion и другие читатели темы вызывают matchMedia при загрузке модуля
(win as unknown as { matchMedia: unknown }).matchMedia = () => ({
  matches: false, addEventListener() {}, removeEventListener() {},
});
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {} });
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Динамический импорт ПОСЛЕ стабов: react-dom читает window при загрузке
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');
const { FarmKpiCard } = await import('./FarmKpiCard');
import type { LocalModelStats } from './api';

// Монтирует карточку и оставляет дерево живым (для чтения клеток сетки);
// unmount() снимает его из body — следующий mount не наследует чужое дерево.
function mountCard(data: LocalModelStats): { host: Element; unmount: () => void } {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => { root.render(createElement(FarmKpiCard, { data })); });
  return { host, unmount: () => { act(() => { root.unmount(); }); host.remove(); } };
}

// Клетки сетки — прямые дети грида: firstElementChild — стиль числа,
// lastElementChild — подпись
function cells(host: Element): Element[] {
  const grid = host.querySelector('div[style*="grid-template-columns"]');
  if (!grid) throw new Error('сетка KPI не найдена');
  return Array.from(grid.children);
}

// Заполненный снимок: 2 идут, 3 ждут, 38 ток/с генерации, сквозная 42 ток/с за 5 мин.
// Окна намеренно в порядке '1h' → '5m': порядок ответа не значим (types.ts).
const FULL: LocalModelStats = {
  configured: true, engine: 'vllm', model: 'Qwen3.8-27B', status: 'working',
  running: 2, waiting: 3,
  speed: { genPerStream: 38, prefillPerStream: 1240 },
  answerSpeed: {
    windows: [
      { window: '1h', endToEndTps: 36, medianTps: 34, requests: 21 },
      { window: '5m', endToEndTps: 42, medianTps: 39, requests: 6 },
    ],
    minTokensForMedian: 40,
  },
};

describe('FarmKpiCard — «Сейчас на ферме»', () => {
  it('четыре KPI в сетке 2×2 с подписями макета; окно 5m взято по ключу', () => {
    const { host, unmount } = mountCard(FULL);
    const cs = cells(host);
    expect(cs.length).toBe(4);
    expect(cs.map(c => (c.textContent ?? '').trim())).toEqual([
      '2запросов идёт', '3ждут в очереди', '38 ток/спока пишет · 1 мин', '42 ток/сскорость ответа · 5 мин',
    ]);
    // '5m' выбран по ключу, а не по индексу: он второй в массиве, значение 42, а не 36 из '1h'
    expect(host.innerHTML).toContain('grid-template-columns:1fr 1fr');
    expect(host.innerHTML).toContain('Сейчас на ферме');
    // числа — моно FS.xl 700 C.textHeading; подписи — FS.xs C.textSecondary (макет .kpi)
    const numStyle = cs[0].firstElementChild?.getAttribute('style') ?? '';
    expect(numStyle).toContain('JetBrains Mono');
    expect(numStyle).toContain('font-size:18px');
    expect(numStyle).toContain('font-weight:700');
    expect(numStyle).toContain('var(--c-text-heading)');
    const labelStyle = cs[0].lastElementChild?.getAttribute('style') ?? '';
    expect(labelStyle).toContain('font-size:11px');
    expect(labelStyle).toContain('var(--c-text-secondary)');
    unmount();
  });

  it('очередь > 0 красит только число очереди (var(--c-warning-text))', () => {
    const { host, unmount } = mountCard(FULL);
    const cs = cells(host);
    expect((cs[1].firstElementChild?.getAttribute('style') ?? '')).toContain('var(--c-warning-text)');
    // остальные числа — обычный заголовочный цвет; подпись очереди — тоже обычная
    expect((cs[0].firstElementChild?.getAttribute('style') ?? '')).toContain('var(--c-text-heading)');
    expect((cs[1].lastElementChild?.getAttribute('style') ?? '')).toContain('var(--c-text-secondary)');
    unmount();
  });

  it('очередь 0 — без warning-цвета, ноль остаётся числом', () => {
    const { host, unmount } = mountCard({ ...FULL, waiting: 0 });
    const cs = cells(host);
    expect(host.innerHTML).not.toContain('var(--c-warning-text)');
    expect(cs[1].textContent).toContain('0');
    expect((cs[1].firstElementChild?.getAttribute('style') ?? '')).toContain('var(--c-text-heading)');
    unmount();
  });

  it('null и отсутствующие значения — «нет данных» в своей ячейке, ноль не подставлен', () => {
    const data: LocalModelStats = {
      configured: true, engine: 'vllm', status: 'working',
      running: null as unknown as number,
      waiting: 1,
      // speed.genPerStream отсутствует; answerSpeed — окно '5m' без значения
      answerSpeed: { windows: [{ window: '5m', endToEndTps: null, requests: 0 }] },
    };
    const { host, unmount } = mountCard(data);
    const cs = cells(host);
    expect(cs.length).toBe(4);
    expect(cs[0].textContent).toContain('нет данных');
    expect(cs[2].textContent).toContain('нет данных');
    expect(cs[3].textContent).toContain('нет данных');
    // остальные KPI на месте: их значение не скрывается чужим отсутствием
    expect(cs[1].textContent).toContain('1');
    expect(host.innerHTML).not.toMatch(/0\s*ток\/с/);
    unmount();
  });

  it('нет answerSpeed вовсе и неfinite скорость — «нет данных», а не «0 ток/с» / «NaN ток/с»', () => {
    const { host, unmount } = mountCard({
      configured: true, engine: 'vllm', status: 'working', running: 0, waiting: 0,
      speed: { genPerStream: Number.NaN },
    });
    const cs = cells(host);
    expect(cs[2].textContent).toContain('нет данных');
    expect(cs[3].textContent).toContain('нет данных');
    // нулевые счётчики — измеренные значения, они остаются числами
    expect(cs[0].textContent).toContain('0');
    expect(cs[1].textContent).toContain('0');
    expect(host.innerHTML).not.toContain('NaN');
    unmount();
  });

  it("'yielded' — все четыре ячейки «нет данных», нули снимка не подставлены", () => {
    const { host, unmount } = mountCard({ ...FULL, status: 'yielded', running: 0, waiting: 0 });
    const cs = cells(host);
    expect(cs.length).toBe(4);
    for (const c of cs) expect(c.textContent).toContain('нет данных');
    expect(host.innerHTML).not.toMatch(/0запросов|0ток\/с/);
    unmount();
  });
});
