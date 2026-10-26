// Тег статуса фичи: глиф очереди и моно-цифры «N из M» (макет feature-queue-1.md, «Тег и глиф»).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { StatusTag, featureRunStatusTag } from './FeatureStatusTag';
import { featureDisplayState } from '../../lib/chatGroups';
import { FONT } from '../../lib/design';
import type { FeatureIndexFeature } from '../../types/featureIndex';

const html = (tag: ReturnType<typeof featureRunStatusTag>) => renderToStaticMarkup(createElement(StatusTag, { tag }));
const queueHtml = html(featureRunStatusTag('paused', null, { holdReason: 'queue', queue: { position: 2, length: 3 } }));

describe('StatusTag: очередь', () => {
  it('глиф: три полосы 12×2.4 rx 1.2 по y 2.5/6.8/11.1, currentColor, нижние opacity .55, 10 px', () => {
    expect(queueHtml).toMatch(/<svg[^>]*width="10"[^>]*height="10"/);
    const rects = queueHtml.match(/<rect[^>]*>/g) ?? [];
    expect(rects).toHaveLength(3);
    const ys = ['2.5', '6.8', '11.1'];
    rects.forEach((r, i) => {
      expect(r).toContain('width="12"');
      expect(r).toContain('height="2.4"');
      expect(r).toContain('rx="1.2"');
      expect(r).toContain(`y="${ys[i]}"`);
      expect(r).toContain('fill="currentColor"');
      if (i === 0) expect(r).not.toContain('opacity');
      else expect(r).toContain('opacity="0.55"');
    });
  });

  it('цифры «N из M» набраны FONT.mono, подпись целиком сохранена', () => {
    const m = /<span[^>]*data-testid="queue-nums"[^>]*>([^<]*)<\/span>/.exec(queueHtml);
    expect(m?.[1]).toBe('2 из 3');
    expect(queueHtml).toContain(FONT.mono.replace(/'/g, '&#x27;'));
    expect(queueHtml.replace(/<[^>]+>/g, '')).toBe('в очереди · 2 из 3');
  });

  it('очередь без цифр: глиф есть, моно-спана нет', () => {
    const h = html(featureRunStatusTag('paused', null, { holdReason: 'queue' }));
    expect(h).toContain('queue-glyph');
    expect(h).not.toContain('queue-nums');
  });

  it('holdReason=manual: без глифа очереди, подпись «на паузе»', () => {
    const h = html(featureRunStatusTag('paused', null, { holdReason: 'manual' }));
    expect(h).not.toContain('queue-glyph');
    expect(h.replace(/<[^>]+>/g, '')).toBe('на паузе');
  });
});

// R-Q5C-A: один источник подписи — хелпер chatGroups.queueLabel. Тег статуса и статус
// строки индекса (featureDisplayState) для одной строки индекса дают побайтно одно и то же.
function queuedFeature(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Фича', projectId: 'p1',
    bucket: 'attention', attention: null, state: 'paused',
    holdReason: 'queue', queue: { position: 2, length: 3 },
    rail: [], chips: ['open'],
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  };
}

describe('queueLabel: единый источник подписи (тег = строка индекса)', () => {
  it('«в очереди · 2 из 3» побайтно из featureRunStatusTag и из featureDisplayState', () => {
    const tag = featureRunStatusTag('paused', null, { holdReason: 'queue', queue: { position: 2, length: 3 } });
    expect(tag.label).toBe('в очереди · 2 из 3');
    const st = featureDisplayState(queuedFeature());
    expect(st.kind).toBe('queue');
    expect(st.status).toBe(tag.label);
  });

  it('queue=null: «в очереди» без цифр и нулей — побайтно в обоих местах', () => {
    const tag = featureRunStatusTag('paused', null, { holdReason: 'queue' });
    expect(tag.label).toBe('в очереди');
    expect(featureDisplayState(queuedFeature({ queue: null })).status).toBe(tag.label);
  });
});
