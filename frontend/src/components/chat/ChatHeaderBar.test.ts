// Слот строки фичи в шапке (ревью F2, major 2): «слот задан» ≠ «в слоте есть строка».
// Чат БЕЗ фичи — шапка ровно как на master: узла chat-feature-bar нет, линия тулбара
// на месте (и при наличии строки контекста она не пропадает). Чат С фичей — узел есть,
// линия «при прокрутке» переезжает на строку фичи (одна, на нижней кромке шапки).
//
// Окружение node: renderToStaticMarkup. Пиксели высоты здесь не меряются — структурные
// признаки (узел слота, линия тулбара) их прямые следствия; живой замер — у Майи на
// стенде. Первый div ответа в тулбарной ветке — корень Toolbar (при отсутствии слота и
// контекста возвращается ровно он), поэтому его style — то, что рисует линию шапки.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { ChatHeaderBar } from './ChatHeaderBar';
import { C } from '../../lib/design';
import type { Session } from '../../types';

// Линия «при прокрутке» тулбара — маркер того, что шапка без строки фичи живёт как на
// master: её ставит ChatHeaderBar (transparent в начале ленты, C.borderLight при
// прокрутке). Когда слот заполнен, тулбар гасит свою (noBorder) и линия переезжает на
// слот. Поэтому по стилю корня тулбара видно и то, и другое.
const SCROLL_LINE = `border-bottom:1px solid ${C.borderLight}`;

// Стор аккаунта провайдера — не предмет теста, а его useSyncExternalStore не отдаёт
// server snapshot (для SSR-рендера в тесте нужен). Мок — пустой ответ.
vi.mock('../../lib/accountUsage', () => ({
  useAccountUsage: () => null,
  accountSnapshotsFor: () => [],
}));

const session = {
  id: 's-1', mode: 'default', status: 'active', messageCount: 0,
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
} as Session;

const base = {
  session,
  hasMessages: false,
  online: true,
  cost: { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 },
  falCost: { total: 0, count: 0, byModel: new Map() },
  glifCost: { count: 0, credits: 0, hasCredits: false, byType: new Map() },
  billing: 'api' as const,
  rateWindows: [],
  ctxEstimate: { window: 200000, fresh: false, level: 'normal' as const },
  isWaiting: false,
  isCompacting: false,
  canCompact: false,
  onCompact: () => {},
};

// Стиль ПЕРВОГО div ответа (корень тулбара в тулбарной ветке) — по нему видно, где линия.
function rootStyle(html: string): string {
  const m = html.match(/^<div[^>]*style="([^"]*)"/);
  return m ? m[1] : '';
}

function render(over: Record<string, unknown>): string {
  return renderToStaticMarkup(createElement(ChatHeaderBar, { ...base, ...over }));
}

describe('ChatHeaderBar: слот строки фичи', () => {
  it('без слота: ни узла chat-feature-bar, ни самого wrapper-слоя — ответ это один тулбар со своей линией', () => {
    const html = render({ scrolled: true });
    expect(html).not.toContain('chat-feature-bar');
    const style = rootStyle(html);
    // Линия тулбара на месте: noBorder не ставился, оверрайд ChatHeaderBar живой.
    expect(style).toContain(SCROLL_LINE);
  });

  it('без слота, но со строкой контекста: линия тулбара не пропадает (прежняя регрессия F2)', () => {
    const html = render({ scrolled: true, contextBar: createElement('span', { 'data-testid': 'ctx' }, 'материалы') });
    expect(html).not.toContain('chat-feature-bar');
    expect(html).toContain('data-testid="ctx"');
    expect(rootStyle(html)).toContain(SCROLL_LINE);
  });

  it('со слотом: узел chat-feature-bar есть, линия тулбара гасится и переезжает на строку фичи', () => {
    const html = render({
      scrolled: true,
      featureBar: createElement('div', { 'data-testid': 'feature-banner' }, 'строка'),
    });
    expect(html).toContain('data-testid="chat-feature-bar"');
    expect(html).toContain('data-testid="feature-banner"');
    // Тулбар без своей линии (её рисует слот — нижней кромкой шапки).
    const style = rootStyle(html);
    expect(style).toContain('border-bottom:none');
    expect(style).not.toContain(SCROLL_LINE);
    expect(html).toContain(SCROLL_LINE); // линия жива — на слоте
  });

  it('слот передан как null (так делает ChatPanel при чате без фичи): то же, что «не задан»', () => {
    const html = render({ scrolled: true, featureBar: null });
    expect(html).not.toContain('chat-feature-bar');
    expect(rootStyle(html)).toContain(SCROLL_LINE);
  });

  it('остров (island) без слота: узла слота нет — hero-шапка не обрастает пустым div', () => {
    const html = render({ island: true });
    expect(html).not.toContain('chat-feature-bar');
  });
});
