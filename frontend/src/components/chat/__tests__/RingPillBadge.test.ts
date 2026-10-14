// Ари-лейбл кольцевой пилюли: строка «Локальная модель: <хвост/лицо>» под флагом
// local-model-stats и только для локального чата (m1, ревью Глеба). Статический рендер —
// как соседние тесты шапки (ToolUseView.test, sphereGate.test).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, describe, expect, it } from 'vitest';
import { setAllFlags } from '../../../lib/featureFlags';
import { RingPillBadge, type CostStats, type FalCostStats, type ProviderBalance } from '../ChatHeaderBar';
import type { GlifGenStats } from '../glifStats';
import type { ClaudeBilling } from '../../../types';
import type { ContextEstimate } from '../../../lib/context';
import type { RateWindow } from '../../../lib/rateLimit';
import type { LocalModelLiveState } from '../../../features/localModel/useLocalModelLive';

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
