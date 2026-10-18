// Плашка фичи в чате (FeatureBanner): глиф, название, мини-рельса, статус,
// счётчик, роль чата, кнопка «Ключевой чат →»/«Открыть фичу», тон фона.
//
// Два уровня:
//   1 — ЧИСТАЯ модель featureBannerModel (тон/статус/счётчик/роль/кнопка) —
//      без рендера, независима от пояса (дата — локальным геттером от той же Date).
//   2 — SSR-рендер компонента (renderToStaticMarkup): 4 тона, закрытая, нет
//      ключевого, чат без фичи (null), оба флага выключены (null).
//
// Окружение node (без jsdom): renderToStaticMarkup. Стор индекса и флаги
// заполняются напрямую (__test.setIndex / setAllFlags) — useEffect-фетч в SSR
// не идёт, компонент читает уже заполненный стор.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, beforeEach, afterEach } from 'vitest';
import { FeatureBanner, featureBannerModel } from './FeatureBanner';
import { __test } from '../../../lib/featuresIndex';
import { setAllFlags } from '../../../lib/featureFlags';
import type { FeatureAttention, FeatureChatLink, FeatureIndexFeature, FeaturesIndex } from '../../../types/featureIndex';

// === Фикстуры ===

function rail(): FeatureIndexFeature['rail'] {
  return [
    { key: 'cut', state: 'done', tone: null },
    { key: 'plan', state: 'done', tone: null },
    { key: 'prod', state: 'active', tone: 'run' },
    { key: 'review', state: 'pending', tone: null },
    { key: 'qa', state: 'pending', tone: null },
    { key: 'final', state: 'pending', tone: null },
    { key: 'merge', state: 'pending', tone: null },
  ];
}

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: rail(),
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 1,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chat-key', keyArchived: false, keyProjectId: 'p1', chatCount: 2,
    ...over,
  };
}

function attention(kind: FeatureAttention['kind']): FeatureAttention {
  return { kind, since: null, reason: null, personaId: null, sessionId: null };
}

// Связь «этот чат × фича»: по умолчанию исполнитель Ш2 · бэк.
function link(over: Partial<FeatureChatLink> = {}): FeatureChatLink {
  return { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк', ...over };
}

// === 1: чистая модель ===

describe('featureBannerModel: тон, статус, счётчик, роль, кнопка', () => {
  it('идёт (run): тон run, статус «{этап} · идёт», счётчик 1/3 ↺1, роль Ш2 · бэк, кнопка «Ключевой чат»', () => {
    const m = featureBannerModel(feat(), link(), 'chat-exec');
    expect(m.tone).toBe('run');
    expect(m.status).toBe('Производство · идёт');
    expect(m.counter).toBe('1/3 ↺1');
    expect(m.role).toBe('Ш2 · бэк');
    expect(m.isKey).toBe(false);
    expect(m.button).toEqual({ label: 'Ключевой чат', kind: 'key' });
  });

  it('ждёт вас (wait): тон wait, статус «ждёт вас»', () => {
    const m = featureBannerModel(feat({ bucket: 'attention', attention: attention('wait') }), link(), 'chat-exec');
    expect(m.tone).toBe('wait');
    expect(m.status).toBe('ждёт вас');
  });

  it('к мержу (merge): тон wait, статус «ждёт вас · мерж»', () => {
    const m = featureBannerModel(feat({ bucket: 'attention', attention: attention('merge') }), link(), 'chat-exec');
    expect(m.tone).toBe('wait');
    expect(m.status).toBe('ждёт вас · мерж');
  });

  it('упала (stopped): тон stop, статус «стоит»', () => {
    const m = featureBannerModel(feat({ bucket: 'attention', attention: attention('stopped') }), link(), 'chat-exec');
    expect(m.tone).toBe('stop');
    expect(m.status).toBe('стоит');
  });

  it('смержена (closed + mergedAt): тон closed, статус «смержена <дата>», глиф done', () => {
    const f = feat({ bucket: 'closed', summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } });
    const m = featureBannerModel(f, link(), 'chat-exec');
    expect(m.tone).toBe('closed');
    expect(m.glyph).toBe('done');
    // дата — локальным геттером от той же Date (независимо от пояса машины)
    const d = new Date('2026-10-18T12:00:00Z');
    const date = `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
    expect(m.status).toBe(`смержена ${date}`);
  });

  it('закрыта без мержа: тон closed, статус «закрыта», глиф closed', () => {
    const m = featureBannerModel(feat({ bucket: 'closed', summary: null }), link(), 'chat-exec');
    expect(m.tone).toBe('closed');
    expect(m.glyph).toBe('closed');
    expect(m.status).toBe('закрыта');
  });

  it('нет ключевого (keySessionId = null): кнопка «Открыть фичу» (не переход)', () => {
    const m = featureBannerModel(feat({ keySessionId: null }), link(), 'chat-exec');
    expect(m.button).toEqual({ label: 'Открыть фичу', kind: 'feature' });
  });

  it('этот чат — ключевой (sessionId === keySessionId): роль «ключевой», isKey', () => {
    const m = featureBannerModel(feat({ keySessionId: 'chat-key' }), link(), 'chat-key');
    expect(m.isKey).toBe(true);
    expect(m.role).toBe('ключевой');
  });

  it('куратор без stepRef/lane: роль по виду (куратор)', () => {
    const m = featureBannerModel(feat(), link({ kind: 'curator', stepRef: null, lane: null }), 'chat-cur');
    expect(m.role).toBe('куратор');
  });
});

// === 2: SSR-рендер компонента ===

// Заполнить стор индекса одним проектом с одной фичей и связью «чат × фича».
function seedIndex(f: FeatureIndexFeature, l: FeatureChatLink, chatId: string, projectKey: string) {
  const index: FeaturesIndex = { features: [f], chats: { [chatId]: [l] } };
  __test.setIndex(projectKey, index);
}

function renderBanner(sessionId: string, projectId: string | null): string {
  return renderToStaticMarkup(
    createElement(FeatureBanner, { sessionId, projectId, isMobile: false }),
  );
}

describe('FeatureBanner: рендер (SSR)', () => {
  beforeEach(() => {
    __test.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    setAllFlags({});
  });

  it('идёт: плашка с тоном run и рамкой', () => {
    seedIndex(feat(), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-banner"');
    expect(html).toContain('data-banner-tone="run"');
    expect(html).toContain('Моя фича');
    expect(html).toContain('Ключевой чат');
  });

  it('ждёт вас: тон wait (C.infoBg)', () => {
    seedIndex(feat({ bucket: 'attention', attention: attention('wait') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="wait"');
    expect(html).toContain('var(--c-info-bg)');
  });

  it('упала: тон stop (C.dangerBg)', () => {
    seedIndex(feat({ bucket: 'attention', attention: attention('stopped') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="stop"');
    expect(html).toContain('var(--c-danger-bg)');
  });

  it('к мержу: тон wait (C.infoBg), статус «ждёт вас · мерж»', () => {
    seedIndex(feat({ bucket: 'attention', attention: attention('merge') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="wait"');
    expect(html).toContain('ждёт вас · мерж');
  });

  it('смержена: серая (C.bgInset), статус «смержена <дата>», глиф done', () => {
    seedIndex(feat({ bucket: 'closed', summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="closed"');
    expect(html).toContain('var(--c-bg-inset)');
    expect(html).toContain('смержена ');
  });

  it('нет ключевого (keySessionId = null): кнопка «Открыть фичу»', () => {
    seedIndex(feat({ keySessionId: null }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('Открыть фичу');
    expect(html).not.toContain('Ключевой чат');
  });

  it('чат без фичи: плашки нет (null)', () => {
    // индекс с фичей, но связь другого чата — для 'nope' links нет
    seedIndex(feat(), link(), 'other-chat', 'p1');
    const html = renderBanner('nope', 'p1');
    expect(html).toBe('');
  });

  it('оба флага выключены: плашки нет (null), даже если фича в сторе', () => {
    seedIndex(feat(), link(), 'chat-exec', 'p1');
    setAllFlags({ 'turn-plan-feature-mode': false, 'features-overview': false });
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toBe('');
  });
});
