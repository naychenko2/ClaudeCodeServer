// Тесты блока «Метрики» экрана этапа (D1d): три доли раздельно с числами из ответа,
// metrics=null/undefined — блока нет, review=null — карточки ревью нет, approx — «≈»/«нет данных».
// Окружение node: renderToStaticMarkup.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureStageMetrics } from './FeatureStageMetrics';
import type { StageMetrics, StageMetricsShare } from '../../types/featureStats';

function share(over: Partial<StageMetricsShare> = {}): StageMetricsShare {
  return {
    minutes: 74, live: false, turns: 18, errorTurns: 1,
    tokens: { input: 120000, output: 18000, cacheRead: 2400000, cacheCreation: 90000 },
    costUsd: 3.41, costPartial: false, hiddenCostModels: [], models: [], tasks: null, ...over,
  };
}

// Фикстура из мини-разреза D0 §4.
function mk(over: Partial<StageMetrics> = {}): StageMetrics {
  return {
    mode: 'journal',
    window: { from: '2026-11-12T08:00:00Z', to: '2026-11-12T11:30:00Z', live: false, elapsedMinutes: 210 },
    executor: share(),
    review: share({
      minutes: 22, turns: 6, errorTurns: 0,
      tokens: { input: 30000, output: 4000, cacheRead: 500000, cacheCreation: 20000 },
      costUsd: 0, costPartial: true, hiddenCostModels: ['qwen3.8-flash'],
      tasks: [{ taskId: 't2', verdict: 'returned', sharedWith: 1 }],
    }),
    human: { minutes: 41, waits: 2, replies: 3, waitingNow: null },
    idleMinutes: 73,
    spendFrom: '2026-11-12',
    ...over,
  };
}

const html = (m: StageMetrics | null | undefined) =>
  renderToStaticMarkup(createElement(FeatureStageMetrics, { metrics: m, nowMs: Date.parse('2026-11-12T12:00:00Z') }));

function card(markup: string, id: string): string {
  const i = markup.indexOf(`data-testid="stage-metrics-${id}"`);
  if (i < 0) return '';
  const next = markup.indexOf('data-testid="stage-metrics-', i + 10);
  return markup.slice(i, next < 0 ? undefined : next);
}

describe('FeatureStageMetrics', () => {
  it('metrics=null и undefined — блока нет', () => {
    expect(html(null)).toBe('');
    expect(html(undefined)).toBe('');
  });

  it('три доли раздельно, числа из ответа', () => {
    const out = html(mk());
    const ex = card(out, 'executor');
    const rv = card(out, 'review');
    const hu = card(out, 'human');
    expect(ex).toContain('1 ч 14 мин');
    expect(ex).toContain('18 (упавших 1)');
    expect(ex).toContain('$3,41');
    expect(ex).toContain('2,4М');
    expect(rv).toContain('22 мин');
    expect(rv).toContain('6');
    expect(rv).not.toContain('упавших');
    expect(rv).toContain('Цена неполная');
    expect(hu).toContain('41 мин');
    expect(hu).toContain('Ответов человека');
    expect(hu).toContain('>3<');
    expect(ex).not.toContain('41 мин');
    expect(out).toContain('Простой этапа: 1 ч 13 мин');
  });

  it('review=null — карточки ревью нет', () => {
    const out = html(mk({ review: null }));
    expect(out).not.toContain('stage-metrics-review');
    expect(out).toContain('stage-metrics-executor');
  });

  it('расход не учитывался — словом, а не нулём', () => {
    const out = html(mk({
      executor: share({ tokens: null, costUsd: null }),
      human: { minutes: 0, waits: 0, replies: null, waitingNow: null },
    }));
    expect(card(out, 'executor')).toContain('не учитывался');
    expect(card(out, 'executor')).not.toContain('Стоимость');
    expect(card(out, 'human')).toContain('не учитывался');
  });

  it('approx: «≈» у исполнителя, у ревью и человека — «нет данных»', () => {
    const out = html(mk({
      mode: 'approx',
      executor: share({ turns: null, errorTurns: null }),
      review: share({ minutes: null, turns: null, errorTurns: null }),
      human: { minutes: null, waits: null, replies: 3, waitingNow: null },
    }));
    expect(card(out, 'executor')).toContain('≈ 1 ч 14 мин');
    expect(card(out, 'review')).toContain('нет данных');
    expect(card(out, 'human')).toContain('нет данных');
    expect(out).toContain('Журнал неполный');
  });

  it('ждёт сейчас — длительность от since на nowMs', () => {
    const out = html(mk({
      human: { minutes: 5, waits: 1, replies: 1, waitingNow: { taskId: 't', since: '2026-11-12T11:15:00Z', reason: 'нужен ответ' } },
    }));
    expect(card(out, 'human')).toContain('45 мин · нужен ответ');
  });
});
