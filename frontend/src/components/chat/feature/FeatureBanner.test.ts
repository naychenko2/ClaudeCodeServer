// Плашка фичи в чате (FeatureBanner) — СТРОКА с точками этапов (макет feature-banner-1,
// вариант «А · Строка» + кликабельные этапы из «Г»). Три вопроса строки:
//   • ГДЕ МЫ     — 8 точек этапов (design может быть skipped) + подпись «{этап} · {состояние}» (слова 1:1 с pillModel/попапом);
//   • ЧТО ОТ МЕНЯ — одно главное действие (waiting → «Ответить», stopped → «Чат»,
//                    merge_pending → «Открыть», иначе → «Открыть страницу фичи»);
//   • КТО РАБОТАЕТ — чип роли этого чата («этот чат — {роль}» / «ключевой»).
//
// Два уровня:
//   1 — ЧИСТАЯ модель featureBannerRow (where/действие/тон/роль) — без рендера,
//      табличный прогон по 9 состояниям FeatureState.
//   2 — РЕНДЕР панели с НАСТОЯЩИМИ данными списка (стор индекса через __test.setIndex,
//      не прямой проп): SSR (подпись/действие/тон по каждому состоянию) + mount
//      (клик по точке → cc-open-feature, «впереди» не кликабельно).
//
// Очередь фич (R-Q5D-B, MAJOR-1 из RV-K-Q5D): рендер подписи — РАЗБИВКОЙ: leading-слово
// в <b> цвета textHeading, суффикс — обычным начертанием и цветом ряда; активная точка
// фичи в очереди приглушена (C.textMuted, как в плашке группы) и её aria — «· в очереди
// N из M» (макет feature-queue-1.html:421-423).
//
// R-Q5D-C (главное замечание RV-K-Q5D-B): разбивка — ТОЛЬКО в не-тревожных состояниях.
// В тревожных (stopped — C.dangerText, waiting/merge_pending — C.info) фраза «где мы»
// рисуется целым элементом тоном тревоги (макет баннера feature-banner-1.html:526):
// тревога должна читаться тревогой. Ассерт текста фразы — по ВИДИМОМУ содержимому
// span'а «где мы» (whereInner), не по всему html: aria-label всегда несёт целую фразу,
// и toContain по html проходит даже когда leading-слово из рендера выброшено (c3).
//
// Окружение node (без jsdom): SSR — renderToStaticMarkup; mount — fakeDom.
// Стор индекса и флаги заполняются напрямую (setIndex / setAllFlags) — useEffect-фетч
// в SSR не идёт, компонент читает уже заполненный стор.

import { createElement, act } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { FeatureBanner, featureBannerRow } from './FeatureBanner';
import { C, CHAT_MAX_W } from '../../../lib/design';
import { __test } from '../../../lib/featuresIndex';
import { setAllFlags } from '../../../lib/featureFlags';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../__tests__/fakeDom';
import { __test as runsTest } from '../../../lib/featureRuns';
import type { FeatureAttention, FeatureChatLink, FeatureIndexFeature, FeaturesIndex } from '../../../types/featureIndex';
import type { FeatureQueue, FeatureRun, FeatureState } from '../../../types/featureRun';
import { queueLabelParts } from '../../../lib/chatGroups';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

// m7: управляемый индекс фич — mock `api.tasks.featuresIndex` возвращает этот объект,
// чтобы live-перезапрос (useFeaturesIndex) не затирал синхронный сид стора.
const mockIdx = vi.hoisted(() => ({
  features: [] as FeatureIndexFeature[],
  chats: {} as Record<string, FeatureChatLink[]>,
}));
// M3: управляемый ответ `features?sessionId=` — mock `api.tasks.getFeatures` возвращает
// эти runs, чтобы шторка (через useFeatureRuns) получала НАСТОЯЩИЕ данные стора,
// а не пустой список.
const mockRuns = vi.hoisted(() => [] as FeatureRun[]);
// Персоны: управляемый список для getPersonaById (имя исполнителя в «Ответить {имя}»).
const mockPersonas = vi.hoisted(() => [] as { id: string; name: string }[]);
// M2: openChatById — vi.fn, чтобы тест проверил ЦЕЛЬ главного действия (чат тревоги).
const mockOpenChat = vi.hoisted(() => ({ openChatById: vi.fn(async () => true) }));
// === Мок lib/api: featuresIndex → mockIdx, getFeatures → mockRuns ===
vi.mock('../../../lib/api', () => ({
  api: {
    sessions: { list: async () => [] },
    chats: { list: async () => [] },
    tasks: {
      execute: async () => {},
      featuresIndex: async () => mockIdx,
      getFeatures: async () => mockRuns,
    },
    personas: { list: async () => [] },
  },
}));
// === Мок lib/openChat: openChatById → mockOpenChat.openChatById (M2) ===
vi.mock('../../../lib/openChat', () => ({ openChatById: mockOpenChat.openChatById }));
// === Мок signalr: onMessage/onPreviewMessage — no-op (иначе getConnection() строит
// реальное соединение '/hubs/session', которое в node не резолвится) ===
vi.mock('../../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
// === Мок lib/personas: ensurePersonasLoaded — no-op; getPersonaById — из mockPersonas ===
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, ensurePersonasLoaded: async () => {}, getPersonaById: (id: string) => mockPersonas.find(p => p.id === id) };
});

// === Фикстуры ===

// Рельса по контракту — ровно 8 сегментов в порядке жизненного цикла: design без задач
// (skipped), cut/plan пройдены, prod — активная (run), остальное — впереди.
function rail(): FeatureIndexFeature['rail'] {
  return [
    { key: 'cut', state: 'done', tone: null },
    { key: 'design', state: 'skipped', tone: null },
    { key: 'plan', state: 'done', tone: null },
    { key: 'prod', state: 'active', tone: 'run' },
    { key: 'review', state: 'pending', tone: null },
    { key: 'qa', state: 'pending', tone: null },
    { key: 'final', state: 'pending', tone: null },
    { key: 'merge', state: 'pending', tone: null },
  ];
}

// Строка фичи: по умолчанию running (prod активна). `state` — переопределяемый (9 значений).
function feat(state: FeatureState = 'running', over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', attention: null, state,
    rail: rail(),
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 1,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chat-key', keyArchived: false, keyProjectId: 'p1', chatCount: 2,
    ...over,
  };
}

function attention(kind: FeatureAttention['kind'], over: Partial<FeatureAttention> = {}): FeatureAttention {
  return { kind, since: null, reason: null, personaId: null, sessionId: null, taskId: null, ...over };
}

// Связь «этот чат × фича»: по умолчанию исполнитель Ш2 · бэк.
function link(over: Partial<FeatureChatLink> = {}): FeatureChatLink {
  return { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк', ...over };
}

// Заполнить стор индекса одним проектом: одна фича + связь «чат × фича».
function seedIndex(f: FeatureIndexFeature, l: FeatureChatLink, chatId: string, projectKey: string) {
  const index: FeaturesIndex = { features: [f], chats: { [chatId]: [l] } };
  __test.setIndex(projectKey, index);
}

// === 1: чистая модель featureBannerRow — подпись/действие/тон/роль по 9 состояниям ===

describe('featureBannerRow: «где мы», действие, тон, роль', () => {
  // Таблица: state → (подпись, действие, тон). Активная стадия фикстуры — prod.
  // Слова — из ОДНОГО ИСТОЧНИКА с пилюлей (statusPhrase, pillModel.ts): running —
  // «{этап}» + (имена идущих, если есть); stopped — «стоит» + (имя); merged — «смержена»
  // (без даты, как в пилюле); merge_pending — «Мерж · ждёт согласия».
  const cases: { state: FeatureState; where: string; action: string; kind: 'reply' | 'chat' | 'open' | 'page'; tone: 'run' | 'wait' | 'stop' | 'closed' }[] = [
    { state: 'not_started', where: 'Не начата', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
    { state: 'running', where: 'Производство', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
    { state: 'waiting', where: 'Производство · ждёт вас', action: 'Ответить', kind: 'reply', tone: 'wait' },
    { state: 'stopped', where: 'стоит', action: 'Чат', kind: 'chat', tone: 'stop' },
    { state: 'merge_pending', where: 'Мерж · ждёт согласия', action: 'Открыть', kind: 'open', tone: 'wait' },
    { state: 'merging', where: 'Мерж · идёт', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
    { state: 'merged', where: 'смержена', action: 'Открыть страницу фичи', kind: 'page', tone: 'closed' },
    { state: 'closed', where: 'закрыта', action: 'Открыть страницу фичи', kind: 'page', tone: 'closed' },
    { state: 'idle', where: 'Простаивает', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
  ];

  it.each(cases)('state=$state → «$where», действие «$action» ($kind), тон $tone', c => {
    const m = featureBannerRow(feat(c.state), link(), 'chat-exec');
    expect(m.where).toBe(c.where);
    expect(m.action.label).toBe(c.action);
    expect(m.action.kind).toBe(c.kind);
    expect(m.tone).toBe(c.tone);
  });

  // merged — БЕЗ даты (как в пилюле; общий источник statusPhrase): «смержена».
  it('merged: «смержена» (без даты, как в пилюле)', () => {
    const f = feat('merged', { summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } });
    expect(featureBannerRow(f, link(), 'chat-exec').where).toBe('смержена');
  });

  // running с идущими: «{этап} · {имена}» (общий источник с пилюлей; имена — из
  // nowPersonaIds индекса, уникальные, в порядке индекса).
  it('running с идущими: «Производство · Кира, Денис» (имена из nowPersonaIds)', () => {
    mockPersonas.length = 0;
    mockPersonas.push({ id: 'p-kira', name: 'Кира' }, { id: 'p-denis', name: 'Денис' });
    const f = feat('running', { nowPersonaIds: ['p-kira', 'p-denis'] });
    expect(featureBannerRow(f, link(), 'chat-exec').where).toBe('Производство · Кира, Денис');
  });

  // stopped с персоной: «стоит · {имя}» (общий источник с пилюлей).
  it('stopped с персоной: «стоит · Денис»', () => {
    mockPersonas.length = 0;
    mockPersonas.push({ id: 'p-denis', name: 'Денис' });
    const f = feat('stopped', { attention: attention('stopped', { personaId: 'p-denis' }) });
    expect(featureBannerRow(f, link(), 'chat-exec').where).toBe('стоит · Денис');
  });

  it('waiting с персоной: «Ответить {имя}» (имя — из стора персон)', () => {
    mockPersonas.length = 0;
    mockPersonas.push({ id: 'p-kira', name: 'Кира' });
    const f = feat('waiting', { attention: attention('wait', { personaId: 'p-kira' }) });
    expect(featureBannerRow(f, link(), 'chat-exec').action.label).toBe('Ответить Кира');
  });

  it('stopped с персоной: «Чат {имя}»', () => {
    mockPersonas.length = 0;
    mockPersonas.push({ id: 'p-denis', name: 'Денис' });
    const f = feat('stopped', { attention: attention('stopped', { personaId: 'p-denis' }) });
    expect(featureBannerRow(f, link(), 'chat-exec').action.label).toBe('Чат Денис');
  });

  // merging: подпись «Мерж · идёт» БЕЗ имени (attention.personaId недостижимо при
  // state=merging, FeatureProjector.cs:1663; имя потребует merge.personaId в индексе).
  it('merging: «Мерж · идёт» без имени (attention.personaId недостижимо)', () => {
    const f = feat('merging');
    expect(featureBannerRow(f, link(), 'chat-exec').where).toBe('Мерж · идёт');
  });

  it('роль: исполнитель step → «Ш2 · бэк»; lane «back» (англ.) нормализуется в «бэк»', () => {
    expect(featureBannerRow(feat('running'), link(), 'chat-exec').role).toBe('Ш2 · бэк');
    expect(featureBannerRow(feat('running'), link({ lane: 'back' }), 'chat-exec').role).toBe('Ш2 · бэк');
  });

  it('роль: куратор без stepRef/lane → «куратор» (фолбэк на вид связи)', () => {
    expect(featureBannerRow(feat('running'), link({ kind: 'curator', stepRef: null, lane: null }), 'chat-cur').role).toBe('куратор');
  });

  it('этот чат — ключевой (sessionId === keySessionId): роль «ключевой», isKey', () => {
    const m = featureBannerRow(feat('running', { keySessionId: 'chat-key' }), link(), 'chat-key');
    expect(m.isKey).toBe(true);
    expect(m.role).toBe('ключевой');
  });

  it('точки: done/active кликабельны, pending/absent/skipped — нет (8 точек, порядок — от бэка)', () => {
    const m = featureBannerRow(feat('running'), link(), 'chat-exec');
    expect(m.dots).toHaveLength(8);
    expect(m.dots.map(d => d.key)).toEqual(['cut', 'design', 'plan', 'prod', 'review', 'qa', 'final', 'merge']);
    expect(m.dots.filter(d => d.clickable).map(d => d.key)).toEqual(['cut', 'plan', 'prod']);
  });
});

// === 1б: очередь фич (волна 6) — «В очереди · N из M» в подписи «где мы» ===
// Макет feature-queue-1, таблица «Подпись»: рельса в чате — bold «В очереди»,
// post « · 2 из 3» (тот же statusPhrase, что у пилюли). Источник — holdReason/queue
// строки индекса (позицию считает бэк). Нет queue (бэк не отдал) — без цифр.

describe('FeatureBanner: очередь фич (подпись «В очереди · N из M»)', () => {
  beforeEach(() => {
    __test.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    setAllFlags({});
  });

  const queued = (over: Partial<FeatureIndexFeature> = {}) =>
    feat('paused', { holdReason: 'queue', queue: { position: 2, length: 3 }, ...over });

  it('holdReason=queue: bold «В очереди», post « · 2 из 3» (разбивка модели)', () => {
    const m = featureBannerRow(queued(), link(), 'chat-exec');
    expect(m.whereBold).toBe('В очереди');
    expect(m.wherePost).toBe(' · 2 из 3');
    expect(m.where).toBe('В очереди · 2 из 3');
  });

  it('queue не пришёл (бэк ещё не отдал) → «В очереди» без цифр', () => {
    const m = featureBannerRow(queued({ queue: null }), link(), 'chat-exec');
    expect(m.whereBold).toBe('В очереди');
    expect(m.wherePost).toBe('');
    expect(m.where).toBe('В очереди');
  });

  it('holdReason=manual → прежняя подпись «На паузе», без «В очереди» (даже с queue в данных)', () => {
    const m = featureBannerRow(feat('paused', { holdReason: 'manual', queue: { position: 2, length: 3 } }), link(), 'chat-exec');
    expect(m.where).toBe('На паузе');
    expect(m.where).not.toContain('В очереди');
  });

  it('рендер (SSR): «В очереди · 2 из 3» в плашке, тон не тревожный (run)', () => {
    const f = queued();
    seedIndex(f, link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('В очереди · 2 из 3');
    // Тон тот же, что у обычной паузы (не wait/stop): тревоги очередь не поднимает.
    expect(html).toContain('data-banner-tone="run"');
  });

  // MAJOR-1 из RV-K-Q5D: рендер — РАЗБИВКОЙ, а не одним span'ом (макет .ban .ph b):
  // leading-слово в <b> цвета textHeading, суффикс — вне <b>, обычным начертанием.
  // Очередь — состояние НЕ-тревожное (whereTone run): при R-Q5D-C разбивка остаётся
  // (ассерты «целой фразы» — только у тревожных wait/stop, в describe «Рендер с данными списка»).
  // Мутация (в отчёте): выбросить <b> из run-ветки рендера → тест красный
  // (первый assert по <b>, второй — по суффиксу вне <b>).
  it('рендер (SSR): «В очереди» в <b> цвета textHeading, post « · 2 из 3» — вне <b> (макет .ban .ph b)', () => {
    seedIndex(queued(), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    const m = html.match(/<span data-testid="feature-where"[^>]*>([\s\S]*?)<\/span>/);
    expect(m, 'нет «где мы»').not.toBeNull();
    const inner = m![1];
    // Leading-слово — в <b> с явным цветом textHeading (макет .ban .ph b).
    expect(inner, 'leading-слово не в <b> цвета textHeading').toContain(`<b style="color:${C.textHeading}">В очереди</b>`);
    // Суффикс — вне <b>, обычным начертанием (в <b> его нет).
    expect(inner.slice(inner.indexOf('</b>') + 4), 'post вне <b>').toBe(' · 2 из 3');
    expect(inner.slice(0, inner.indexOf('</b>')), 'post не должен быть внутри <b>').not.toContain('2 из 3');
  });
});

// === 2: рендер панели с настоящими данными списка (SSR) ===

function renderBanner(sessionId: string, projectId: string | null, isMobile = false): string {
  return renderToStaticMarkup(createElement(FeatureBanner, { sessionId, projectId, isMobile }));
}

// Тег-открывашка span'а «где мы» (со style) и его ВИДИМОЕ содержимое — фраза так, как
// её видит человек (не aria-label: тот всегда несёт целую where, R-Q5D-C/c3).
function whereOpenTag(html: string): string {
  const m = html.match(/<span data-testid="feature-where"[^>]*>/);
  expect(m, 'нет «где мы»').not.toBeNull();
  return m![0];
}
function whereInner(html: string): string {
  const m = html.match(/<span data-testid="feature-where"[^>]*>([\s\S]*?)<\/span>/);
  expect(m, 'нет «где мы»').not.toBeNull();
  return m![1];
}

describe('FeatureBanner: рендер с данными списка (SSR)', () => {
  beforeEach(() => {
    __test.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    setAllFlags({});
  });

  it('идёт: тон run, «Производство» (без имён, если идущих нет), действие «Открыть страницу фичи», роль «Ш2 · бэк»', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-banner"');
    expect(html).toContain('data-banner-tone="run"');
    expect(html).toContain('Производство');
    expect(html).toContain('Открыть страницу фичи');
    expect(html).toContain('Ш2 · бэк');
    // Не-тревожное состояние — разбивка осталась (R-Q5D-C/c2): leading-слово в <b>
    // цвета textHeading (мутация: выбросить <b> из рендера run-ветки → краснеет).
    expect(whereInner(html), 'leading-слово не в <b> цвета textHeading').toContain(`<b style="color:${C.textHeading}">Производство</b>`);
  });

  // R-Q5D-C (c1/c3): фраза — ЦЕЛАЯ, по видимому содержимому span'а (не по aria-label):
  // без <b>, тон C.info на всей фразе. Мутация (в отчёте): выбросить leading-слово из
  // рендера тревожной ветки (: model.wherePost) → краснеет первый ассерт по видимому тексту.
  it('ждёт вас: тон wait (C.infoBg), «Производство · ждёт вас» целой фразой в тоне C.info (R-Q5D-C), «Ответить»', () => {
    seedIndex(feat('waiting', { attention: attention('wait') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="wait"');
    expect(html).toContain('var(--c-info-bg)');
    expect(whereInner(html), '«ждёт вас» не целая видимая фраза (или с <b>)').toBe('Производство · ждёт вас');
    expect(whereOpenTag(html), 'тон тревожной фразы не C.info').toContain(`color:${C.info}`);
    expect(html).toContain('Ответить');
  });

  it('стоит: тон stop (C.dangerBg), «стоит» целой фразой в тоне C.dangerText (R-Q5D-C), «Чат»', () => {
    seedIndex(feat('stopped', { attention: attention('stopped') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="stop"');
    expect(html).toContain('var(--c-danger-bg)');
    expect(whereInner(html), '«стоит» не целая видимая фраза (или с <b>)').toBe('стоит');
    expect(whereOpenTag(html), 'тон тревожной фразы не C.dangerText').toContain(`color:${C.dangerText}`);
    expect(html).toContain('Чат');
  });

  // Р-Q5D-C: вся тревожная фраза, включая имя, — тоном тревоги (главное замечание
  // RV-K-Q5D-B: «стоит · Денис» не должно дробиться на тёмное «стоит» и красный суффикс).
  it('стоит с персоной: «стоит · Денис» целой фразой в тоне C.dangerText (R-Q5D-C)', () => {
    mockPersonas.length = 0;
    mockPersonas.push({ id: 'p-denis', name: 'Денис' });
    seedIndex(feat('stopped', { attention: attention('stopped', { personaId: 'p-denis' }) }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(whereInner(html), '«стоит · Денис» не целая видимая фраза (или с <b>)').toBe('стоит · Денис');
    expect(whereOpenTag(html), 'тон тревожной фразы не C.dangerText').toContain(`color:${C.dangerText}`);
  });

  it('к мержу: тон wait, «Мерж · ждёт согласия» целой фразой в тоне C.info (R-Q5D-C), действие «Открыть» (НЕ «Мерж…»)', () => {
    seedIndex(feat('merge_pending', { attention: attention('merge') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="wait"');
    expect(whereInner(html), '«Мерж · ждёт согласия» не целая видимая фраза (или с <b>)').toBe('Мерж · ждёт согласия');
    expect(whereOpenTag(html), 'тон тревожной фразы не C.info').toContain(`color:${C.info}`);
    expect(html).toContain('Открыть');
    // «Мерж…» — только в попапе и на странице, НЕ на панели (задача, п.2).
    expect(html).not.toContain('Мерж…');
  });

  it('смержена: серая (C.bgInset), «смержена» (без даты, как в пилюле)', () => {
    seedIndex(feat('merged', { summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="closed"');
    expect(html).toContain('var(--c-bg-inset)');
    expect(html).toContain('>смержена<');
  });

  it('закрыта без мержа: «закрыта», тон closed', () => {
    seedIndex(feat('closed', { summary: null }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="closed"');
    expect(html).toContain('>закрыта<');
  });

  it('не начата: «Не начата»; простаивает: «Простаивает»; merging: «Мерж · идёт»', () => {
    seedIndex(feat('not_started'), link(), 'chat-exec', 'p1');
    expect(renderBanner('chat-exec', 'p1')).toContain('Не начата');
    seedIndex(feat('idle'), link(), 'chat-exec', 'p1');
    expect(renderBanner('chat-exec', 'p1')).toContain('Простаивает');
    seedIndex(feat('merging'), link(), 'chat-exec', 'p1');
    expect(renderBanner('chat-exec', 'p1')).toContain('Мерж · идёт');
  });

  it('чат без фичи: плашки нет (null)', () => {
    seedIndex(feat('running'), link(), 'other-chat', 'p1');
    expect(renderBanner('nope', 'p1')).toBe('');
  });

  it('оба флага выключены: плашки нет (null), даже если фича в сторе', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    setAllFlags({ 'turn-plan-feature-mode': false, 'features-overview': false });
    expect(renderBanner('chat-exec', 'p1')).toBe('');
  });

  it('точки: 8 data-testid feature-dot-*, «впереди» (pending) — <i>, не <button>', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    // Все 8 точек на месте (data-testid у каждой).
    for (const k of ['cut', 'design', 'plan', 'prod', 'review', 'qa', 'final', 'merge']) {
      expect(html).toContain(`data-testid="feature-dot-${k}"`);
    }
    // «Впереди» (pending) — <i>, не <button>: тег-открывашка сразу перед data-testid.
    const i = html.indexOf('data-testid="feature-dot-review"');
    expect(i, 'нет точки review').not.toBe(-1);
    expect(html.slice(Math.max(0, i - 20), i), 'pending review должен быть <i>').toContain('<i');
    // Текущий (active) — <button> (type="button" перед data-testid).
    const b = html.indexOf('data-testid="feature-dot-prod"');
    expect(html.slice(Math.max(0, b - 40), b), 'active prod должен быть <button>').toContain('<button');
  });

  // Минор R-DES-F: skipped-точка (design без задач, W4-DES-B) — декоративная, не кликабельна.
  // Мутация (в отчёте): удалить ветку 'skipped' в StageDotView → заливка станет
  // progressTrack, не bgInset → краснеет; сделать её <button> → краснеет проверка тега.
  it('design: skipped — точка <i> (не кнопка) с нейтральной заливкой C.bgInset', () => {
    // Фикстура rail() по контракту уже несёт design: skipped — проверяем её напрямую.
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    const i = html.indexOf('data-testid="feature-dot-design"');
    expect(i, 'нет точки design').not.toBe(-1);
    // Тег-открывашка сразу перед data-testid — <i>, не <button> (некликабельна).
    expect(html.slice(Math.max(0, i - 20), i), 'skipped design должен быть <i>').toContain('<i');
    // Заливка — C.bgInset (var(--c-bg-inset)), не дорожка pending.
    expect(html.slice(i, i + 200), 'skipped-точка залита C.bgInset').toContain(`background:${C.bgInset}`);
  });

  // R-Q5D-B (макет feature-queue-1.html:421-422, спека feature-queue-1.md:26): у фичи в
  // очереди активная точка приглушена (C.textMuted вместо цвета тона — одно состояние
  // выглядит одинаково в плашке группы и в рельсе чата), её aria — «Этап: {этап} · в
  // очереди N из M». Ручная пауза (holdReason=manual) — без приглушения и без суффикса.
  // Мутация (в отчёте): stageDotColor без ветки inQueue (тон точки) → краснеет первый
  // блок; убрать суффикс aria → краснеет assert с aria.
  it('очередь: активная точка приглушена (C.textMuted) с aria «· в очереди 2 из 3»; ручная пауза — цвет тона, aria без суффикса', () => {
    // Пауза с активной стадией tone wait (как у стоящей фичи): без очереди точка была бы C.info.
    const railWait = rail().map(s => (s.state === 'active' ? { ...s, tone: 'wait' as const } : s));
    seedIndex(feat('paused', { holdReason: 'queue', queue: { position: 2, length: 3 }, rail: railWait }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    const m = html.match(/<button[^>]*data-testid="feature-dot-prod"[^>]*>/);
    expect(m, 'нет активной точки prod').not.toBeNull();
    expect(m![0], 'aria активной точки без позиции очереди').toContain('aria-label="Этап: Производство · в очереди 2 из 3"');
    expect(m![0], 'активная точка очереди не приглушена (C.textMuted)').toContain(`background:${C.textMuted}`);
    // Ручная пауза — то же состояние рельсы, что прежде: цвет тона (wait → C.info), aria без суффикса.
    seedIndex(feat('paused', { holdReason: 'manual', rail: railWait }), link(), 'chat-exec', 'p1');
    const htmlManual = renderBanner('chat-exec', 'p1');
    const mManual = htmlManual.match(/<button[^>]*data-testid="feature-dot-prod"[^>]*>/);
    expect(mManual, 'нет активной точки prod (manual)').not.toBeNull();
    expect(mManual![0], 'manual-пауза не должна быть приглушена').toContain(`background:${C.info}`);
    expect(mManual![0], 'aria manual-паузы не должна нести очередь').toContain('aria-label="Этап: Производство"');
  });

  it('«· ещё N»: чат с двумя фичами → «· ещё 1» (порядок — от бэка)', () => {
    const f1 = feat('running', { keySessionId: 'chat-key' });
    const f2 = feat('running', { featureId: 'f2', title: 'Вторая фича', keySessionId: 'chat-key2' });
    const index: FeaturesIndex = {
      features: [f1, f2],
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

  // Зонд: чат с ТРЁМЯ фичами → «· ещё 2».
  it('зонд: чат с тремя фичами → «· ещё 2»', () => {
    const f1 = feat('running', { keySessionId: 'chat-key' });
    const f2 = feat('running', { featureId: 'f2', title: 'Вторая', keySessionId: 'chat-key2' });
    const f3 = feat('running', { featureId: 'f3', title: 'Третья', keySessionId: 'chat-key3' });
    const index: FeaturesIndex = {
      features: [f1, f2, f3],
      chats: { 'chat-exec': [
        { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
        { featureId: 'f2', kind: 'curator', taskRole: null, stepRef: null, lane: null },
        { featureId: 'f3', kind: 'curator', taskRole: null, stepRef: null, lane: null },
      ] },
    };
    __test.setIndex('p1', index);
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-more"');
    expect(html).toContain('· ещё 2');
  });

  // Зонд: архивный чат — баннер рисуется (архив не скрывает фичу).
  it('зонд: архивный чат — баннер рисуется (архив не скрывает)', () => {
    const f = feat('running', { keySessionId: 'chat-arch', keyArchived: true });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-arch': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    const html = renderBanner('chat-arch', 'p1');
    expect(html).toContain('data-testid="feature-banner"');
  });

  it('чип «ключевой»: font-weight 700 + accent/accentLight (макет .role.key)', () => {
    seedIndex(feat('running', { keySessionId: 'chat-key' }), link({ kind: 'orchestrator', stepRef: null, lane: null }), 'chat-key', 'p1');
    const html = renderBanner('chat-key', 'p1');
    expect(html).toContain('font-weight:700;color:var(--c-accent);background:var(--c-accent-light)');
  });

  // M1: обёртка = колонка чата, как у композера (ChatPanel:3109): width 100% +
  // maxWidth CHAT_MAX_W + 2×16 (982) + margin '0 auto' + поля 16 px.
  // border-box: контент = 982 − 32 = 950 = CHAT_MAX_W = колонка.
  // Точечно по style-атрибуту ИМЕННО обёртки: мутация margin '0 auto' → '0 0' красит.
  it('M1: обёртка — колонка чата (max-width 982px + margin 0 auto + padding 0 16px), как композер', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    const m = html.match(/<div data-testid="feature-banner-wrap" style="([^"]*)"/);
    expect(m, 'обёртка feature-banner-wrap с style').not.toBeNull();
    const style = m![1];
    expect(style).toContain('width:100%');
    expect(style).toContain('max-width:982px');
    expect(style).toContain('margin:0 auto');
    expect(style).toContain('padding:0 16px');
  });

  // M1 (геометрия): контент обёртки = CHAT_MAX_W (950) = колонка.
  // Читает ФАКТИЧЕСКИЕ стили смонтированной обёртки (max-width, padding, box-sizing)
  // и проверяет: border-box → content = maxWidth − 2×padding = CHAT_MAX_W.
  // Мутация +32 → +0 (maxWidth = 950): content = 950 − 32 = 918 ≠ 950 → red.
  // Мутация снять boxSizing:'border-box': content = 982 ≠ 950 → red.
  it('M1: геометрия — контент обёртки = CHAT_MAX_W (border-box: maxWidth − 2×padding)', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    const m = html.match(/<div data-testid="feature-banner-wrap" style="([^"]*)"/);
    expect(m, 'обёртка feature-banner-wrap с style').not.toBeNull();
    const style = m![1];
    // box-sizing: border-box — обязательное условие расчёта.
    expect(style, 'box-sizing: border-box').toContain('box-sizing:border-box');
    // max-width: 982px (CHAT_MAX_W + 2×16).
    const mwMatch = style.match(/max-width:(\d+)px/);
    expect(mwMatch, 'max-width в style').not.toBeNull();
    const maxWidth = parseInt(mwMatch![1], 10);
    // padding: 0 16px → 16px по горизонтали.
    const padMatch = style.match(/padding:0 (\d+)px/);
    expect(padMatch, 'padding в style').not.toBeNull();
    const pad = parseInt(padMatch![1], 10);
    // border-box: content = maxWidth − 2×padding = CHAT_MAX_W.
    const contentWidth = maxWidth - 2 * pad;
    expect(contentWidth, `контент ${contentWidth} ≠ CHAT_MAX_W ${CHAT_MAX_W}`).toBe(CHAT_MAX_W);
  });

  // Минор 1: aria-hidden на контейнере точек — только когда кликабельных точек НЕТ
  // (иначе интерактивные <button> внутри aria-hidden скрыты от скринридера, но
  // фокусируемы — нарушение ARIA).
  it('aria-hidden на точках: нет кликабельных (всё pending) → aria-hidden="true"; есть (done/active) → нет', () => {
    // Всё впереди (not_started): ни done, ни active — кликабельных нет.
    const allPending = feat('not_started', {
      rail: [
        { key: 'cut', state: 'pending', tone: null },
        { key: 'plan', state: 'pending', tone: null },
        { key: 'prod', state: 'pending', tone: null },
        { key: 'review', state: 'pending', tone: null },
        { key: 'qa', state: 'pending', tone: null },
        { key: 'final', state: 'pending', tone: null },
        { key: 'merge', state: 'pending', tone: null },
      ],
    });
    seedIndex(allPending, link(), 'chat-exec', 'p1');
    const html1 = renderBanner('chat-exec', 'p1');
    const m1 = html1.match(/<span data-testid="feature-dots"([^>]*)>/);
    expect(m1, 'контейнер точек').not.toBeNull();
    expect(m1![1], 'без кликабельных точек aria-hidden="true" обязателен').toContain('aria-hidden="true"');
    // Есть кликабельные (cut/plan done, prod active) — aria-hidden не ставим.
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html2 = renderBanner('chat-exec', 'p1');
    const m2 = html2.match(/<span data-testid="feature-dots"([^>]*)>/);
    expect(m2, 'контейнер точек').not.toBeNull();
    expect(m2![1], 'с кликабельными точками aria-hidden быть не должно').not.toContain('aria-hidden="true"');
  });

  // Ф-pr: progress-нода — ellipsis-рецепт (перенос из FeatureGroupHeader): на узкой плашке
  // с пятью корзинами («план частично» + четыре числа) строка не выходит за край; полная
  // строка — в title/aria-label. Мутация «снять ellipsis» (flex-shrink:0, без overflow) красит.
  it('Ф-pr: progress-нода — ellipsis-рецепт (flex-shrink:1 + min-width:0 + overflow:hidden + text-overflow:ellipsis) + title с полной строкой', () => {
    seedIndex(feat('running', { stepsAccepted: 1, stepsTotal: 7, stepsReview: 2, stepsWork: 1, stepsNotCreated: 3, planState: 'partial' }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    const m = html.match(/<span data-testid="feature-progress"([^>]*)>/);
    expect(m, 'progress-нода не найдена').not.toBeNull();
    const attrs = m![1];
    expect(attrs, 'flex-shrink:1 (узел сжимается)').toContain('flex-shrink:1');
    expect(attrs, 'min-width:0 (не держит минимальную ширину)').toContain('min-width:0');
    expect(attrs, 'overflow:hidden').toContain('overflow:hidden');
    expect(attrs, 'text-overflow:ellipsis').toContain('text-overflow:ellipsis');
    // Полная строка — в title (и aria-label): на узкой плашке текст обрезается, но строка доступна.
    expect(attrs, 'title с полной строкой').toContain('title="');
  });
});

// === 3: клик по точке → cc-open-feature (mount, реальный DOM) ===

describe('FeatureBanner: клик по точке → переход (mount)', () => {
  beforeEach(() => {
    installFakeDom();
    // useChatGutter (обёртка баннера) создаёт ResizeObserver — в node/fakeDom его нет:
    // заглушка, иначе mount упал бы на `new ResizeObserver`.
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
    __test.reset();
    runsTest.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    runsTest.reset();
    setAllFlags({});
    uninstallFakeDom();
  });

  it('клик по текущему этапу (prod) → cc-open-feature { featureId, fromChatId, fromChat, stage: prod }', async () => {
    const f = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    // Перехват window.dispatchEvent (fake window — no-op): записать cc-open-feature.
    const dispatched: { type: string; detail: unknown }[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
      (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; };

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const dot = findNodeByAttr(container, 'data-testid', 'feature-dot-prod');
    expect(dot, 'нет точки prod').not.toBeNull();
    const props = reactProps(dot!);
    expect(props?.onClick, 'у точки prod нет onClick').toBeDefined();
    act(() => { (props!.onClick as () => void)(); });

    const ev = dispatched.find(d => d.type === 'cc-open-feature');
    expect(ev, 'cc-open-feature не диспатчен').toBeDefined();
    expect(ev!.detail).toMatchObject({ featureId: 'f1', fromChatId: 'chat-exec', fromChat: true, stage: 'prod' });
    root.unmount();
  });

  it('«впереди» (pending) не кликабельно: <i> без onClick (клик не диспатчит cc-open-feature)', async () => {
    const f = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    const dispatched: { type: string }[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
      (e: Event) => { dispatched.push({ type: e.type }); return true; };

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // review — pending: тег <i>, без onClick (в отличие от done/active — <button> + onClick).
    const pending = findNodeByAttr(container, 'data-testid', 'feature-dot-review');
    expect(pending, 'нет точки review').not.toBeNull();
    expect(pending!.nodeName, 'pending должен быть <i>, не <button>').toBe('I');
    expect(reactProps(pending!)?.onClick, 'у pending не должно быть onClick').toBeUndefined();
    // Клик по pending (если бы onClick был) не диспатчит cc-open-feature.
    const onClick = reactProps(pending!)?.onClick as (() => void) | undefined;
    if (onClick) act(() => { onClick(); });
    expect(dispatched.some(d => d.type === 'cc-open-feature'), 'pending не должен диспатчить переход').toBe(false);
    root.unmount();
  });

  // M1 (mount): обёртка — колонка чата (width 100% / max-width 982px / margin '0 auto' /
  // padding '0 16px'), БЕЗ useChatGutter. border-box: content = 982−32 = 950 = колонка.
  // Точечно по style-объекту обёртки: мутация margin '0 auto' → '0 0' красит.
  it('M1: обёртка — колонка чата (margin 0 auto, max-width 982px, padding 0 16px), без useChatGutter', async () => {
    const f = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const wrap = findNodeByAttr(container, 'data-testid', 'feature-banner-wrap');
    expect(wrap, 'нет обёртки feature-banner-wrap').not.toBeNull();
    const st = (wrap! as unknown as { style: Record<string, string> }).style;
    expect(st['margin'], 'margin обёртки — 0 auto (как у композера)').toBe('0 auto');
    expect(st['maxWidth'], 'max-width обёртки — 982px (CHAT_MAX_W + 2×16)').toBe('982px');
    expect(st['padding'], 'поля обёртки — 0 16px').toBe('0 16px');
    expect(st['width']).toBe('100%');
    root.unmount();
  });

  // M2: главное действие — цель = ЧАТ ТРЕВОГИ (attention.sessionId), не ключевой чат.
  //   ждут в другом чате → openChatById(чат тревоги); ждут в этом чате → onFocusComposer.
  it('M2: waiting, тревога в другом чате → клик действия открывает чат тревоги (не страницу фичи)', async () => {
    const f = feat('waiting', { attention: attention('wait', { personaId: 'p-kira', sessionId: 'chat-other' }) });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;
    mockOpenChat.openChatById.mockClear();

    const dispatched: { type: string }[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
      (e: Event) => { dispatched.push({ type: e.type }); return true; };

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const btn = findNodeByAttr(container, 'data-testid', 'feature-action');
    expect(btn, 'нет кнопки главного действия').not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(mockOpenChat.openChatById, 'клик не открыл чат тревоги').toHaveBeenCalledTimes(1);
    expect(mockOpenChat.openChatById).toHaveBeenCalledWith('chat-other');
    expect(dispatched.some(d => d.type === 'cc-open-feature'), 'цель — чат тревоги, не страница фичи').toBe(false);
    root.unmount();
  });

  it('M2: waiting, ждут В ЭТОМ чате → фокус в композер (onFocusComposer), не чат/страница', async () => {
    const f = feat('waiting', { attention: attention('wait', { sessionId: 'chat-exec' }) });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;
    mockOpenChat.openChatById.mockClear();

    let composerFocus = 0;
    const { root, container } = mount(createElement(FeatureBanner, {
      sessionId: 'chat-exec', projectId: 'p1', isMobile: false,
      onFocusComposer: () => { composerFocus++; },
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const btn = findNodeByAttr(container, 'data-testid', 'feature-action');
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(composerFocus, 'фокус в композер не пришёл').toBe(1);
    expect(mockOpenChat.openChatById, 'в этом чате чат не открываем').not.toHaveBeenCalled();
    root.unmount();
  });

  it('M2: stopped, чат упавшего в другом чате → openChatById(чат тревоги)', async () => {
    const f = feat('stopped', { attention: attention('stopped', { personaId: 'p-denis', sessionId: 'chat-fallen' }) });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;
    mockOpenChat.openChatById.mockClear();

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const btn = findNodeByAttr(container, 'data-testid', 'feature-action');
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(mockOpenChat.openChatById).toHaveBeenCalledWith('chat-fallen');
    root.unmount();
  });

  it('M2: merge_pending → клик открывает страницу фичи (cc-open-feature), не чат', async () => {
    const f = feat('merge_pending', { attention: attention('merge', { sessionId: 'chat-other' }) });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;
    mockOpenChat.openChatById.mockClear();

    const dispatched: { type: string; detail?: unknown }[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
      (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; };

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const btn = findNodeByAttr(container, 'data-testid', 'feature-action');
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    const ev = dispatched.find(d => d.type === 'cc-open-feature');
    expect(ev, 'merge_pending → страница фичи').toBeDefined();
    expect(ev!.detail).toMatchObject({ featureId: 'f1', fromChatId: 'chat-exec', fromChat: true });
    expect(mockOpenChat.openChatById, 'merge_pending — не «чат тревоги»').not.toHaveBeenCalled();
    root.unmount();
  });

  // M3: проводка шторки через БАННЕР (не прямой проп runs): «где мы» → шторка с
  // НАСТОЯЩИМИ данными useFeatureRuns (features?sessionId=) — этапы, исполнители,
  // chatId/taskId доезжают до NavTargetButton (кнопки, не «чата нет»), модель — в чипе.
  it('M3: шторка через баннер — этапы, исполнители, chatId/taskId до NavTargetButton, модель в чипе', async () => {
    const f = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    // Runs — из НАСТОЯЩЕГО fetch (mock getFeatures → mockRuns), не прямой проп.
    const run: FeatureRun = {
      featureId: 'f1',
      state: 'running',
      epic: { id: 'f1', title: 'Моя фича' } as unknown as import('../../../types/featureRun').FeatureTask,
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0.5, ready: false,
      stages: [
        { key: 'cut', state: 'done', tone: null, passes: 1, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false, executors: [] },
        { key: 'plan', state: 'done', tone: null, passes: 1, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false, executors: [] },
        {
          key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: ['t1'],
          rounds: [], firstStartedAt: '2026-10-20T10:00:00Z', lastCompletedAt: null, approxMinutes: 40, overlapsActive: false,
          executors: [{
            personaId: 'p-kira', taskId: 't1', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк',
            status: 'running', chatId: 'chat-executor', chatArchived: false,
            modelLabel: 'Qwen 27B', modelFull: 'Qwen3.8-27B',
          }],
        },
        { key: 'review', state: 'pending', tone: null, passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false, executors: [] },
        { key: 'qa', state: 'pending', tone: null, passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false, executors: [] },
        { key: 'final', state: 'pending', tone: null, passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false, executors: [] },
        { key: 'merge', state: 'pending', tone: null, passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false, executors: [] },
      ],
    };
    mockRuns.length = 0;
    mockRuns.push(run);

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // «Где мы» → шторка (десктоп: раскрывающаяся панель).
    const where = findNodeByAttr(container, 'data-testid', 'feature-where');
    expect(where, 'нет «где мы»').not.toBeNull();
    act(() => { (reactProps(where!)!.onClick as () => void)(); });

    // Панель на месте (role=dialog).
    const panel = findNodeByAttr(container, 'data-testid', 'feature-sheet-panel');
    expect(panel, 'шторка (панель) не открылась').not.toBeNull();
    // Этапы доехали (7).
    for (const k of ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge']) {
      expect(findNodeByAttr(container, 'data-testid', `feature-sheet-stage-${k}`), `нет этапа ${k} в шторке`).not.toBeNull();
    }
    // Исполнитель: роль + чип модели (модель доехала).
    const role = findNodeByAttr(container, 'data-testid', 'feature-sheet-executor-role');
    expect(role, 'нет роли исполнителя в шторке').not.toBeNull();
    const chip = findNodeByAttr(container, 'data-testid', 'feature-sheet-executor-model');
    expect(chip, 'нет чипа модели в шторке').not.toBeNull();
    expect(textNodes(chip!).join(''), 'модель не доехала до чипа').toContain('Qwen 27B');
    // chatId/taskId доехали до NavTargetButton: КНОПКИ (не «чата нет»/отсутствие).
    // Мутация chatId=null → span «nav-target-no-chat» вместо кнопки → red; taskId=null →
    // кнопки задачи нет → red.
    const chatBtn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(chatBtn, 'chatId не доехал до NavTargetButton (нет кнопки чата)').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-no-chat'), '«чата нет» не должно быть').toBeNull();
    const taskBtn = findNodeByAttr(container, 'data-testid', 'nav-target-task');
    expect(taskBtn, 'taskId не доехал до NavTargetButton (нет кнопки задачи)').not.toBeNull();
    root.unmount();
  });

  // Минор: initialSel — тест через БАННЕР (порядок entries ≠ порядок runs), не на шторке.
  // Индекс: [f2, f1] (f2 ПЕРВОЙ в строке); runs (API): [f1, f2]. Баннер открывает idx=0 →
  // шторка обязана показать f2 (по порядку индекса), а НЕ f1 (по порядку runs).
  it('initialSel через баннер: порядок entries ≠ порядок runs → шторка показывает f2 (idx=0)', async () => {
    // Индекс: f2 первой (порядок строки), f1 второй.
    const f2 = feat('running', { featureId: 'f2', title: 'Вторая фича', keySessionId: 'chat-key2' });
    const f1 = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f2, f1],
      chats: { 'chat-exec': [
        { featureId: 'f2', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
        { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
      ] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    // Runs (API) — в ДРУГОМ порядке: f1 первая, f2 вторая.
    const mkRun = (id: string, title: string): FeatureRun => ({
      featureId: id,
      state: 'running',
      epic: { id, title } as unknown as import('../../../types/featureRun').FeatureTask,
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
      stages: [],
    });
    mockRuns.length = 0;
    mockRuns.push(mkRun('f1', 'Первая фича'), mkRun('f2', 'Вторая фича'));

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // «Где мы» → шторка.
    const where = findNodeByAttr(container, 'data-testid', 'feature-where');
    expect(where, 'нет «где мы»').not.toBeNull();
    act(() => { (reactProps(where!)!.onClick as () => void)(); });

    // Панель открыта; выбрана f2 (idx=0 по индексу), а НЕ f1 (idx=0 по runs).
    const panel = findNodeByAttr(container, 'data-testid', 'feature-sheet-panel');
    expect(panel, 'шторка (панель) не открылась').not.toBeNull();
    // aria-label панели = заголовок ВЫБРАННОЙ фичи: f2, не f1.
    expect(panel!.getAttribute('aria-label'), 'выбрана f1 (порядок runs), а не f2 (порядок индекса)').toBe('Вторая фича');
    root.unmount();
  });
});

// === R-Q5D-E: плашка берёт подпись из структурного API хелпера ===
// Потребитель не разбирает строку подписи и не держит своей копии формата «N из M»:
// жирное слово, суффикс и aria-суффикс приходят готовыми полями queueLabelParts
// (единственный источник — chatGroups). Мутация (в отчёте): вернуть разбор строки по
// разделителю и зашитое слово «В очереди» → сторож краснеет.

describe('FeatureBanner: R-Q5D-E — подпись из queueLabelParts', () => {
  beforeEach(() => {
    __test.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    setAllFlags({});
  });

  const queued = (queue: FeatureQueue | null) =>
    feat('paused', { holdReason: 'queue', queue });

  it('жирное слово и суффикс модели = queueLabelParts(queue).bold/.post', () => {
    const q: FeatureQueue = { position: 2, length: 3 };
    const parts = queueLabelParts(q);
    const m = featureBannerRow(queued(q), link(), 'chat-exec');
    expect(m.whereBold).toBe(parts.bold);
    expect(m.wherePost).toBe(parts.post);
    expect(m.where).toBe(`${parts.bold}${parts.post}`);
  });

  it('aria активной точки заканчивается на queueLabelParts(queue).ariaSuffix', () => {
    const q: FeatureQueue = { position: 2, length: 3 };
    seedIndex(queued(q), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    const m = html.match(/<button[^>]*data-testid="feature-dot-prod"[^>]*>/);
    expect(m, 'нет кнопки активной точки').not.toBeNull();
    expect(m![0]).toContain(`aria-label="Этап: Производство${queueLabelParts(q).ariaSuffix}"`);
  });

  it('в исходнике плашки нет разбора строки подписи и своей копии слова «В очереди»', () => {
    const src = readFileSync(fileURLToPath(new URL('./FeatureBanner.tsx', import.meta.url)), 'utf8');
    expect(src, 'плашка разбирает строку подписи по разделителю').not.toContain("indexOf(' · ')");
    expect(src, 'плашка берёт строку подписи вместо частей').not.toMatch(/\bqueueLabel\(/);
    expect(src, 'плашка зашивает своё слово «В очереди»').not.toMatch(/bold:\s*'В очереди'/);
  });
});
