// Столбик дня на экране «Обзор»: рендер SpendOverview берёт сегменты из
// dayColumn (spendModel). Здесь гард на сам рендер: столбик красится ЦЕНОВЫМИ
// КЛАССАМИ (free → cheap → expensive), а не источниками, и высота столбика
// считается от дневного итога. Раньше этот кусок кода был ничем не покрыт:
// сборка сегментов жила прямо в компоненте.
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { C } from 'aihome_shell/kit';
import { SpendOverview } from '../SpendOverview';
import type { SpendOverviewResponse } from '../../../types';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr } from '../../../components/chat/__tests__/fakeDom';

const CHART_H = 130;   // высота области графика на не-планшете
const PAD = 12;

function data(over: Partial<SpendOverviewResponse> = {}): SpendOverviewResponse {
  return {
    from: '2026-10-06', to: '2026-10-07', detailDays: 2, windowStart: '2026-10-06', allUsers: false,
    totals: { input: 400, output: 100, cacheRead: 100, cacheCreation: 0, total: 600 },
    turns: 4, falGenerations: 0,
    byDay: [
      {
        date: '2026-10-06', aggregated: false, total: 600,
        bySource: { 'chat-turn': 600 },
        byTier: { free: 100, cheap: 200, expensive: 300 },
        falGenerations: 0,
      },
      // день без разбивки по классам (старый ответ бэка) — нейтральный столбик
      { date: '2026-10-07', aggregated: false, total: 300, bySource: { 'chat-turn': 300 }, falGenerations: 0 },
    ],
    cards: { projects: [], models: [], chats: [], personas: [], sources: [], providers: [] },
    topTurns: [],
    ...over,
  };
}

function render(d: SpendOverviewResponse) {
  return mount(createElement(SpendOverview, {
    data: d, showUsers: false, isMobile: false, isTablet: false, emptyKind: 'none',
    onOpen: () => {}, onClearFilters: () => {}, onClose: () => {},
  }));
}

function styleOf(node: unknown): Record<string, string> {
  return (node as { style: Record<string, string> }).style;
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('столбик дня в обзоре', () => {
  it('день с разбивкой по классам: три сегмента в порядке free → cheap → expensive', () => {
    const { container } = render(data());
    const segs = ['free', 'cheap', 'expensive'].map(k => findNodeByAttr(container, 'data-tier', k));
    expect(segs.every(Boolean)).toBe(true);
    expect(styleOf(segs[0]).background).toBe(C.success);
    expect(styleOf(segs[1]).background).toBe(C.info);
    expect(styleOf(segs[2]).background).toBe(C.warning);
  });

  it('цвета сегментов — классы, а не источники (у «ходов» цвет акцента)', () => {
    const { container } = render(data());
    const free = findNodeByAttr(container, 'data-tier', 'free')!;
    expect(styleOf(free).background).not.toBe(C.accent);
  });

  it('высота столбика — от дневного итога, сумма высот сегментов == высоте столбика', () => {
    const { container } = render(data());
    const heights = ['free', 'cheap', 'expensive']
      .map(k => parseFloat(styleOf(findNodeByAttr(container, 'data-tier', k)).height));
    expect(heights.every(h => h > 0)).toBe(true);
    expect(heights.reduce((a, h) => a + h, 0)).toBe(Math.round(600 / 600 * (CHART_H - PAD)));
  });

  it('день без разбивки по классам — нейтральный столбик', () => {
    const { container } = render(data());
    const neutral = findNodeByAttr(container, 'data-neutral', '1');
    expect(neutral).toBeTruthy();
    expect(styleOf(neutral).background).toBe(C.track);
    expect(parseFloat(styleOf(neutral).height)).toBe(Math.round(300 / 600 * (CHART_H - PAD)));
  });
});
