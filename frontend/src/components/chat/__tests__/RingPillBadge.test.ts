// Ари-лейбл кольцевой пилюли: строка «Локальная модель: <хвост/лицо>» под флагом
// local-model-stats и только для локального чата (m1, ревью Глеба). Статический рендер —
// как соседние тесты шапки (ToolUseView.test, sphereGate.test).
// Вторая часть — циферблат кэша промпта (F-1b): появление по данным ленты, текст на
// десктопе и его отсутствие на мобиле, «идёт ход» без минут.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, describe, expect, it } from 'vitest';
import { CacheDialPip } from '../../ui/CacheDial';
import type { PromptCacheState } from '../../../lib/promptCache';
import { setAllFlags } from '../../../lib/featureFlags';
import { RingPillBadge, type CostStats, type FalCostStats, type ProviderBalance } from '../ChatHeaderBar';
import type { GlifGenStats } from '../glifStats';
import type { ClaudeBilling, ChatItem } from '../../../types';
import type { ContextEstimate } from '../../../lib/context';
import type { RateWindow } from '../../../lib/rateLimit';
import type { LocalModelLiveState } from '../../../features/localModel/useLocalModelLive';
import type { PromptCacheWire } from '../../../lib/promptCache';

const cost: CostStats = { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 };
const falCost: FalCostStats = { total: 0, count: 0, byModel: new Map() };
const glifCost: GlifGenStats = { count: 0, credits: 0, hasCredits: false, byType: new Map() };
// pct задан, чтобы пилюля рендерилась (строка контекста) даже когда строки
// «Локальная модель» нет — иначе негативные тесты проверяли бы тривиально (пилюля скрыта)
const estimate: ContextEstimate = { window: 200000, pct: 42, fresh: false, level: 'normal' };
const balance: ProviderBalance | null = null;
const billing: ClaudeBilling = 'subscription';
const windows: RateWindow[] = [];

// Генерация со скоростью: десктоп — «38 ток/с», мобила — «38 т/с»
const liveGenerating: LocalModelLiveState = { seen: true, phase: 'generating', genTps: 38, waitingTotal: null, lastTurn: null, lastActivityAt: null };
// Idle без скорости последнего хода — хвоста/лица нет
const liveIdle: LocalModelLiveState = { seen: true, phase: 'idle', genTps: null, waitingTotal: null, lastTurn: null, lastActivityAt: null };

// aria-label пилюли (атрибут aria-label в разметке) или null, если пилюли нет
function renderAriaLabel(live: LocalModelLiveState | undefined, isMobile = false): string | null {
  const html = renderToStaticMarkup(createElement(RingPillBadge, {
    estimate, isWaiting: false, isCompacting: false, canCompact: false, onCompact: () => {},
    online: true, assistantName: 'Тест',
    isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    isMobile, forceLocalModel: live,
  }));
  const m = /aria-label="([^"]*)"/.exec(html);
  return m ? m[1] : null;
}

// Есть ли в aria-label строка «Локальная модель: …» (null-лейбл — пилюли нет — строки тоже нет)
const hasLmLine = (label: string | null): boolean => label != null && label.includes('Локальная модель:');

afterEach(() => setAllFlags({}));

describe('RingPillBadge aria-label: локальная модель (m1)', () => {
  it('десктоп, генерация, флаг вкл — строка «Локальная модель: 38 ток/с»', () => {
    setAllFlags({ 'local-model-stats': true });
    const label = renderAriaLabel(liveGenerating);
    expect(label).toContain('Локальная модель: 38 ток/с');
  });

  it('мобила, генерация, контекст в норме — строка со словом-лицом «38 т/с»', () => {
    setAllFlags({ 'local-model-stats': true });
    const label = renderAriaLabel(liveGenerating, true);
    expect(label).toContain('Локальная модель: 38 т/с');
  });

  it('флаг выкл — строки нет (хотя чат локальный)', () => {
    setAllFlags({ 'local-model-stats': false });
    expect(hasLmLine(renderAriaLabel(liveGenerating))).toBe(false);
  });

  it('idle без скорости последнего хода — строки нет (активного состояния нет)', () => {
    setAllFlags({ 'local-model-stats': true });
    expect(hasLmLine(renderAriaLabel(liveIdle))).toBe(false);
  });

  it('idle с lastTurn: null — строки нет', () => {
    setAllFlags({ 'local-model-stats': true });
    expect(hasLmLine(renderAriaLabel(undefined))).toBe(false);
  });
});

// === Циферблат кэша (F-1b) ===
// Якорь привязан к «сейчас» теста: остаток 60 мин — тёплый, 5 мин — остывает, −1 мин — холодный.
const MIN = 60_000;
const cacheItem = (over: Partial<PromptCacheWire> = {}): ChatItem => ({
  kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1,
  promptCache: { readTokens: 184000, writtenTokens: 1000, anchorAtMs: Date.now(), ttl: '1h', missReason: 'none', ...over },
} as ChatItem);
const plainResult = (): ChatItem => ({ kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1 } as ChatItem);

function renderPill(items: ChatItem[], isMobile = false, isWaiting = false): string {
  return renderToStaticMarkup(createElement(RingPillBadge, {
    estimate, isWaiting, isCompacting: false, canCompact: false, onCompact: () => {},
    online: true, assistantName: 'Тест',
    isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    isMobile, items,
  }));
}

const ariaOf = (html: string): string => /aria-label="([^"]*)"/.exec(html)?.[1] ?? '';
// Видимый текст минуты/«остыл» рядом с циферблатом (в атрибутах не ищем)
const visibleLeftText = (html: string): string | null => />(\d+ мин|остыл)</.exec(html)?.[1] ?? null;

describe('RingPillBadge: циферблат кэша промпта (F-1b)', () => {
  it('до первого result’а с данными кэша — ни циферблата, ни разделителя', () => {
    const html = renderPill([plainResult()]);
    expect(html).not.toContain('<svg width="14"');
    expect(html).not.toContain('width:1px;height:12px');
    expect(ariaOf(html)).not.toContain('Кэш');
  });

  it('тёплый: циферблат есть, текста с минутами нет', () => {
    const html = renderPill([cacheItem()]);
    expect(html).toContain('<svg width="14"');
    expect(visibleLeftText(html)).toBeNull();
    expect(ariaOf(html)).toContain('Кэш тёплый · 60 мин');
  });

  it('остывает: минуты на десктопе, «дыхание» на секторе', () => {
    const html = renderPill([cacheItem({ anchorAtMs: Date.now() - 55 * MIN })]);
    expect(visibleLeftText(html)).toBe('5 мин');
    expect(ariaOf(html)).toContain('Кэш скоро остынет');
    expect(html).toContain('cc-cache-breathe');
  });

  it('холодный: пунктирный контур и «остыл»', () => {
    const html = renderPill([cacheItem({ anchorAtMs: Date.now() - 61 * MIN })]);
    expect(html).toContain('stroke-dasharray="3 2.2"');
    expect(visibleLeftText(html)).toBe('остыл');
    expect(ariaOf(html)).toContain('Кэш остыл');
  });

  it('мобила: циферблат есть, текста у него нет ни в каком состоянии', () => {
    expect(visibleLeftText(renderPill([cacheItem({ anchorAtMs: Date.now() - 55 * MIN })], true))).toBeNull();
    expect(visibleLeftText(renderPill([cacheItem({ anchorAtMs: Date.now() - 61 * MIN })], true))).toBeNull();
    // сам циферблат и минуты в aria-label при этом остаются
    expect(renderPill([cacheItem({ anchorAtMs: Date.now() - 55 * MIN })], true)).toContain('<svg width="14"');
    expect(ariaOf(renderPill([cacheItem({ anchorAtMs: Date.now() - 55 * MIN })], true))).toContain('5 мин');
  });

  it('идёт ход: показывается, но не как статус — без минут', () => {
    const html = renderPill([cacheItem()], false, true);
    expect(html).toContain('<svg width="14"');
    expect(visibleLeftText(html)).toBeNull();
    expect(ariaOf(html)).toContain('Кэш · идёт ход');
  });

  it('без items вообще — рендер не падает и циферблата нет (стабильный EMPTY_ITEMS)', () => {
    const html = renderToStaticMarkup(createElement(RingPillBadge, {
      estimate, isWaiting: false, isCompacting: false, canCompact: false, onCompact: () => {},
      online: true, assistantName: 'Тест',
      isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    }));
    expect(html).not.toContain('<svg width="14"');
    expect(ariaOf(html)).not.toContain('Кэш');
  });
});

// Мини-циферблат строки подсказки (tip) — тот же компонент, что в подсказке пилюли (F-1b-r)
describe('CacheDialPip: мини-циферблат в подсказке', () => {
  const state = (over: Partial<PromptCacheState> = {}): PromptCacheState => ({
    status: 'warm', ttl: '1h', anchorAtMs: Date.now(), expiresAt: Date.now() + 60 * MIN,
    remainingMs: 60 * MIN, readTokens: 1000, writtenTokens: 500, ...over,
  });
  it('тёплый: пип 12 px с сектором на весь круг', () => {
    const html = renderToStaticMarkup(createElement(CacheDialPip, { state: state(), isWaiting: false }));
    expect(html).toContain('<svg width="12"');
    expect(html).toContain('r="7"');
    expect(html).not.toContain('stroke-dasharray');
  });
  it('остывающий: «дыхание» сектора в пипе', () => {
    // Якорь 55 мин назад — liveState сам считает остаток 5 мин и статус «остывает»
    const html = renderToStaticMarkup(createElement(CacheDialPip, {
      state: state({ anchorAtMs: Date.now() - 55 * MIN, expiresAt: Date.now() + 5 * MIN, remainingMs: 5 * MIN }),
      isWaiting: false,
    }));
    expect(html).toContain('cc-cache-breathe');
  });
  it('идёт ход: пип серый, без сектора', () => {
    const html = renderToStaticMarkup(createElement(CacheDialPip, { state: state(), isWaiting: true }));
    expect(html).not.toContain('fill-opacity');
  });
});
