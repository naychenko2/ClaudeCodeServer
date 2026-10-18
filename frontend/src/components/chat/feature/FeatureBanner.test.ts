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
import { FeatureBanner, featureBannerModel, backRoleLabel, moreFeaturesCount } from './FeatureBanner';
import { __test } from '../../../lib/featuresIndex';
import { __test as backNavTest, recordBackNav, clearBackNav, backNavFrom } from '../../../lib/featureBackNav';
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

  it('ключевой чат: кнопка «Конвейер ▾» (kind pipeline), не «Ключевой чат»', () => {
    const m = featureBannerModel(feat({ keySessionId: 'chat-key' }), link(), 'chat-key');
    expect(m.button).toEqual({ label: 'Конвейер', kind: 'pipeline' });
  });

  it('не-ключевой чат с живым ключевым: кнопка «Ключевой чат» (kind key) — переход, не конвейер', () => {
    const m = featureBannerModel(feat({ keySessionId: 'chat-key' }), link(), 'chat-exec');
    expect(m.button).toEqual({ label: 'Ключевой чат', kind: 'key' });
  });

  it('архивный ключевой чат (keyArchived): кнопка «Ключевой чат (в архиве)» (R3, минор 1)', () => {
    const m = featureBannerModel(feat({ keySessionId: 'chat-key', keyArchived: true }), link(), 'chat-exec');
    expect(m.button).toEqual({ label: 'Ключевой чат (в архиве)', kind: 'key' });
  });

  it('куратор без stepRef/lane: роль по виду (куратор)', () => {
    const m = featureBannerModel(feat(), link({ kind: 'curator', stepRef: null, lane: null }), 'chat-cur');
    expect(m.role).toBe('куратор');
  });
});

// === 1b: «← назад» — роль исходного чата (backRoleLabel) ===

// Индекс с двумя чатами одной фичи: ключевой (orchestrator) + исполнитель (Ш2 · бэк).
function seedKeyAndExec(f: FeatureIndexFeature, keyLink: FeatureChatLink, execLink: FeatureChatLink, projectKey: string) {
  const index: FeaturesIndex = {
    features: [f],
    chats: { [f.keySessionId!]: [keyLink], 'chat-exec': [execLink] },
  };
  __test.setIndex(projectKey, index);
}

describe('backRoleLabel: роль исходного чата в фиче', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  it('исполнитель с stepRef+lane → «Ш2 · бэк»', () => {
    const f = feat({ keySessionId: 'chat-key' });
    seedKeyAndExec(f, { featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null },
      { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }, 'p1');
    expect(backRoleLabel('chat-exec', 'f1')).toBe('Ш2 · бэк');
  });

  it('исходный чат не принадлежит этой фиче → пусто (кнопки «← назад» нет)', () => {
    const f = feat({ keySessionId: 'chat-key' });
    // Связь чата-exec — к ДРУГОЙ фиче (f2), не к f1.
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-key': [{ featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null }],
        'chat-exec': [{ featureId: 'f2', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    expect(backRoleLabel('chat-exec', 'f1')).toBe('');
  });

  it('куратор без stepRef/lane → роль по виду (куратор)', () => {
    const f = feat({ keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-key': [{ featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null }],
        'chat-exec': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }] },
    };
    __test.setIndex('p1', index);
    expect(backRoleLabel('chat-exec', 'f1')).toBe('куратор');
  });
});

// === 1c: мультифича — число других фич (moreFeaturesCount) ===

describe('moreFeaturesCount: фронт не сортирует и не считает порядок', () => {
  it('одна фича → 0 (кнопки «· ещё» нет)', () => {
    expect(moreFeaturesCount([{ feature: feat(), link: link() }])).toBe(0);
  });
  it('две фичи → 1; три → 2 (N = entries.length − 1)', () => {
    expect(moreFeaturesCount([
      { feature: feat(), link: link() },
      { feature: feat({ featureId: 'f2', title: 'Вторая' }), link: link({ featureId: 'f2' }) },
    ])).toBe(1);
    expect(moreFeaturesCount([
      { feature: feat(), link: link() },
      { feature: feat({ featureId: 'f2' }), link: link({ featureId: 'f2' }) },
      { feature: feat({ featureId: 'f3' }), link: link({ featureId: 'f3' }) },
    ])).toBe(2);
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

// Мобильный рендер (360 px): плашка одной строкой + тач-цель 40×40.
function renderBannerMobile(sessionId: string, projectId: string | null): string {
  return renderToStaticMarkup(
    createElement(FeatureBanner, { sessionId, projectId, isMobile: true }),
  );
}

describe('FeatureBanner: рендер (SSR)', () => {
  beforeEach(() => {
    __test.reset();
    backNavTest.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    backNavTest.reset();
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

  // === Ф4: «Конвейер ▾» в ключевом чате ===

  it('ключевой чат: кнопка «Конвейер ▾» (data-testid feature-pipeline), НЕ «Ключевой чат»', () => {
    // Чат = ключевой (keySessionId === sessionId) → button kind pipeline.
    seedIndex(feat({ keySessionId: 'chat-key' }), link({ kind: 'orchestrator', stepRef: null, lane: null }), 'chat-key', 'p1');
    const html = renderBanner('chat-key', 'p1');
    expect(html).toContain('data-testid="feature-pipeline"');
    expect(html).toContain('Конвейер');
    expect(html).not.toContain('data-testid="feature-go"');
    expect(html).not.toContain('Ключевой чат');
  });

  it('не-ключевой чат: кнопка «Ключевой чат» (data-testid feature-go), без «Конвейер»', () => {
    seedIndex(feat({ keySessionId: 'chat-key' }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-go"');
    expect(html).toContain('Ключевой чат');
    expect(html).not.toContain('data-testid="feature-pipeline"');
  });

  it('архивный ключевой чат: кнопка «Ключевой чат (в архиве)» (R3, минор 1)', () => {
    seedIndex(feat({ keySessionId: 'chat-key', keyArchived: true }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('Ключевой чат (в архиве)');
  });

  // === Ф4: «← назад» только после перехода из плашки ===

  it('«← назад» (feature-back) — ПОСЛЕ перехода из плашки: роль исходного чата', () => {
    // Ключевой чат + исполнитель той же фичи; переход из плашки исполнителя.
    seedKeyAndExec(
      feat({ keySessionId: 'chat-key' }),
      { featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null },
      { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
      'p1',
    );
    recordBackNav('chat-exec', 'chat-key');
    const html = renderBanner('chat-key', 'p1');
    expect(html).toContain('data-testid="feature-back"');
    expect(html).toContain('← Ш2 · бэк');
  });

  it('«← назад» НЕ при обычном открытии ключевого чата (перехода из плашки не было)', () => {
    seedKeyAndExec(
      feat({ keySessionId: 'chat-key' }),
      { featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null },
      { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
      'p1',
    );
    // Без recordBackNav — «← назад» нет, даже в ключевом чате.
    const html = renderBanner('chat-key', 'p1');
    expect(html).not.toContain('data-testid="feature-back"');
  });

  it('«← назад» не показывается, если исходный чат — другой фиче (роль пуста)', () => {
    // Ключевой чат фичи f1; исходный чат-exec — фичи f2 (не f1).
    const index: FeaturesIndex = {
      features: [feat({ keySessionId: 'chat-key' })],
      chats: {
        'chat-key': [{ featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null }],
        'chat-exec': [{ featureId: 'f2', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }],
      },
    };
    __test.setIndex('p1', index);
    recordBackNav('chat-exec', 'chat-key');
    const html = renderBanner('chat-key', 'p1');
    expect(html).not.toContain('data-testid="feature-back"');
  });

  it('«← назад» в НЕ-ключевом чате не показывается (даже если back-nav записан)', () => {
    seedKeyAndExec(
      feat({ keySessionId: 'chat-key' }),
      { featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null },
      { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
      'p1',
    );
    // Back-nav ведёт в chat-key, но рендерим chat-exec (не ключевой) → «← назад» нет.
    recordBackNav('chat-exec', 'chat-key');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).not.toContain('data-testid="feature-back"');
  });

  it('возврат в исходный чат: «← назад» → исходный чат + сброс back-nav (кнопка исчезает)', () => {
    seedKeyAndExec(
      feat({ keySessionId: 'chat-key' }),
      { featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null },
      { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
      'p1',
    );
    // Переход из плашки исполнителя → ключевой чат.
    recordBackNav('chat-exec', 'chat-key');
    // Ключевой чат: «← назад» на исходный чат (chat-exec).
    expect(backNavFrom('chat-key')).toBe('chat-exec');
    // Клик «← назад»: возврат в исходный чат + сброс указателя (ядро goBack, без навигации).
    clearBackNav();
    expect(backNavFrom('chat-key')).toBeNull();
    // После возврата в исходный чат (chat-exec) «← назад» не показывается: указатель пуст.
    const html = renderBanner('chat-exec', 'p1');
    expect(html).not.toContain('data-testid="feature-back"');
  });

  // === Ф4: мультифича «· ещё N» ===

  it('чат с двумя фичами: «· ещё 1» (feature-more); порядок — от бэка', () => {
    const f1 = feat({ keySessionId: 'chat-key' });
    const f2 = feat({ featureId: 'f2', title: 'Вторая фича', keySessionId: 'chat-key2' });
    const index: FeaturesIndex = {
      features: [f1, f2],
      // chat-exec связан с ОБЕИМИ фичами: f1 (исполнитель) и f2 (куратор).
      chats: { 'chat-exec': [
        { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
        { featureId: 'f2', kind: 'curator', taskRole: null, stepRef: null, lane: null },
      ] },
    };
    __test.setIndex('p1', index);
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-more"');
    expect(html).toContain('· ещё 1');
  });

  it('чат с одной фичей: «· ещё» нет', () => {
    seedIndex(feat(), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).not.toContain('data-testid="feature-more"');
  });
});

// === Ф5: мобильная ветка (360 px) — одна строка 44 px, тач-цель 40×40, тап → шторка ===

describe('FeatureBanner: мобильная ветка (SSR, 360 px)', () => {
  beforeEach(() => {
    __test.reset();
    backNavTest.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    backNavTest.reset();
    setAllFlags({});
  });

  it('не-ключевой чат: одна строка (feature-banner-row) с телом (body) и тач-целью 40×40 (feature-go)', () => {
    seedIndex(feat(), link(), 'chat-exec', 'p1');
    const html = renderBannerMobile('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-banner-row"');
    expect(html).toContain('data-testid="feature-banner-body"');
    expect(html).toContain('data-testid="feature-go"');
    // Тач-цель 40×40 (TB.iconHitMobile): width/height 40px в стиле кнопки.
    expect(html).toContain('width:40px');
    expect(html).toContain('height:40px');
    // Строка 44 px.
    expect(html).toContain('min-height:44px');
  });

  it('мобила: статус и роль чата скрыты (в строке только глиф+название+рельса+счётчик)', () => {
    seedIndex(feat(), link(), 'chat-exec', 'p1');
    const html = renderBannerMobile('chat-exec', 'p1');
    // Название, счётчик и рельса — на месте.
    expect(html).toContain('Моя фича');
    expect(html).toContain('1/3 ↺1');
    expect(html).toContain('data-testid="feature-rail"');
    // Статус («Производство · идёт») и роль («· этот чат: …») — НЕ в строке.
    expect(html).not.toContain('Производство · идёт');
    expect(html).not.toContain('этот чат:');
  });

  it('мобила: «← назад» — иконка (data-testid feature-back), без текстовой роли', () => {
    seedKeyAndExec(
      feat({ keySessionId: 'chat-key' }),
      { featureId: 'f1', kind: 'orchestrator', taskRole: null, stepRef: null, lane: null },
      { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
      'p1',
    );
    recordBackNav('chat-exec', 'chat-key');
    const html = renderBannerMobile('chat-key', 'p1');
    // Ключевой чат: кнопка — «Конвейер» (feature-pipeline), «← назад» — иконка.
    expect(html).toContain('data-testid="feature-back"');
    expect(html).toContain('data-testid="feature-pipeline"');
    // Текстовая роль («Ш2 · бэк») в «← назад» не рисуется (только иконка).
    expect(html).not.toContain('← Ш2 · бэк');
  });

  it('мобила: чат без фичи → плашки нет (null)', () => {
    seedIndex(feat(), link(), 'other-chat', 'p1');
    expect(renderBannerMobile('nope', 'p1')).toBe('');
  });
});
