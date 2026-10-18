// Чипы-фильтры фич (FeatureFilterChips): ряд toggle-чипов со счётчиками.
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR). Принадлежность фичи к
// чипу фронт НЕ считает — бэк отдаёт chips[] (контракт «Контракт индекса фич —
// утверждён (Александр, 18.10)»); фронт только считает числа по chips[] и отдаёт
// выбранный чип наверх. Макет features-overview.html (.chips/.fch).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { FeatureFilterChips } from './FeatureFilterChips';
import { FEATURE_FILTER_CHIPS } from '../lib/chatGroups';
import type { FeatureChip, FeatureIndexFeature } from '../types/featureIndex';

function feat(id: string, chips: FeatureChip[]): FeatureIndexFeature {
  return {
    featureId: id, title: `Фича ${id}`, projectId: 'p1',
    bucket: chips.includes('closed') ? 'closed' : 'running',
    attention: null, rail: [],
    chips,
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
  };
}

function renderChips(features: FeatureIndexFeature[], selected: FeatureChip, isMobile = false): string {
  return renderToStaticMarkup(
    createElement(FeatureFilterChips, { features, selected, onSelect: () => {}, isMobile }),
  );
}

describe('FeatureFilterChips: рендер', () => {
  it('все пять чипов в порядке FEATURE_FILTER_CHIPS, с подписями', () => {
    const html = renderChips([], 'open');
    for (const { label } of FEATURE_FILTER_CHIPS) {
      expect(html).toContain(label);
    }
    // Порядок: «Открытые» перед «Закрытые»
    expect(html.indexOf('Открытые')).toBeLessThan(html.indexOf('Закрытые'));
  });

  it('счётчики считаются по features[].chips', () => {
    const features = [
      feat('a', ['open', 'waiting']),
      feat('b', ['open', 'stopped']),
      feat('c', ['closed']),
    ];
    const html = renderChips(features, 'open');
    // open=2, waiting=1, stopped=1, merge=0, closed=1 — числа видны в span-счётчиках
    expect(html).toContain('>2</span>');
    expect(html).toContain('>1</span>');
    expect(html).toContain('>0</span>');
  });

  it('выбранный чип — акцентный фон, остальные — прозрачные', () => {
    const html = renderChips([feat('a', ['open'])], 'stopped');
    // Выбранный (stopped) — background:var(--c-accent); остальные — transparent
    expect(html).toContain('background:var(--c-accent)');
    expect(html).toContain('background:transparent');
  });

  it('мобила: тач-цель minHeight 36px, десктоп — без', () => {
    expect(renderChips([], 'open', false)).not.toContain('min-height:36px');
    expect(renderChips([], 'open', true)).toContain('min-height:36px');
  });
});
