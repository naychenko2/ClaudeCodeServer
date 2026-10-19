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

function renderChips(features: FeatureIndexFeature[], selected: FeatureChip, isMobile = false, closedCount?: number): string {
  return renderToStaticMarkup(
    createElement(FeatureFilterChips, { features, selected, onSelect: () => {}, isMobile, closedCount }),
  );
}
// Число после подписи чипа (span-счётчик следует за span-подписью)
function chipCount(html: string, label: string): string {
  const i = html.indexOf(`>${label}</span>`);
  if (i === -1) return '';
  const m = html.slice(i).match(/<span[^>]*>(\d+)<\/span>/);
  return m ? m[1] : '';
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

  it('m1: выбранный — accentLight + accentMuted; невыбранный — bgCard + borderLight', () => {
    const html = renderChips([feat('a', ['open'])], 'stopped');
    // m1: выбранный (stopped) — заливка accentLight, рамка accentMuted (макет .fch, не C.accent)
    expect(html).toContain('background:var(--c-accent-light)');
    expect(html).toContain('border:1px solid var(--c-accent-muted)');
    // невыбранные — bgCard + borderLight
    expect(html).toContain('background:var(--c-bg-card)');
    expect(html).toContain('border:1px solid var(--c-border-light)');
  });

  it('n2: высота по макету .fch — мобила 36px (тач-цель), десктоп 26px', () => {
    expect(renderChips([], 'open', false)).toContain('min-height:26px');
    expect(renderChips([], 'open', true)).toContain('min-height:36px');
  });

  it('n3: число «Закрытые» — из closedCount (нарисованные группы), не из всех закрытых фич', () => {
    // 2 закрытые фичи в индексе; без closedCount «Закрытые»=2, с closedCount=1 → 1
    const features = [feat('a', ['closed']), feat('b', ['closed'])];
    expect(chipCount(renderChips(features, 'open'), 'Закрытые')).toBe('2');
    expect(chipCount(renderChips(features, 'open', false, 1), 'Закрытые')).toBe('1');
    // остальные чипы не затронуты: «Открытые» по-прежнему по features
    expect(chipCount(renderChips(features, 'open', false, 1), 'Открытые')).toBe('0');
  });
});
