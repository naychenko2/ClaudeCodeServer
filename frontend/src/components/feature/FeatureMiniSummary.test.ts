// Мини-сводка времени на вкладке «Хронология» (волна 3, Ф8, макет features-overview-3-final).
// Рендер-тесты ЧЕРЕЗ страницу фичи (FeaturePage) с НАСТОЯЩИМ ответом `features/{id}`
// (mock api.tasks.getFeature → page.stats):
//   · stats=null → строки нет (без нулей);
//   · source=approx → «≈»;
//   · costPartial → цены нет;
//   · клик «Статистика ›» → вкладка «Статистика»;
//   · нет «принято N из M» / «все готово».
//
// Окружение node (без jsdom): fakeDom (mount) + mock api (tasks.getFeature) + управляемые
// сторы (featurePage, featuresIndex, featureBackNav) — тот же приём, что FeaturePage.test.ts.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePage } from './FeaturePage';
import { __test as pageTest, setFeaturePageTab } from '../../lib/featurePage';
import { __test as indexTest } from '../../lib/featuresIndex';
import { __test as backNavTest } from '../../lib/featureBackNav';
import { setAllFlags } from '../../lib/featureFlags';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureIndexFeature, FeaturesIndex } from '../../types/featureIndex';
import type { FeaturePage as FeaturePageData } from '../../types/featureIndex';
import type { FeatureStats } from '../../types/featureStats';

// === Управляемые ответы ===
const mockIndex = vi.hoisted(() => ({
  features: [] as FeatureIndexFeature[],
  chats: {} as Record<string, import('../../types/featureIndex').FeatureChatLink[]>,
}));
const mockGetFeature = vi.hoisted(() => ({
  pending: false,
  result: null as FeaturePageData | null,
  error: null as (Error & { status?: number }) | null,
}));

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async (id: string) => null },
    tasks: {
      featuresIndex: async () => mockIndex,
      getFeature: (fid: string) => {
        if (mockGetFeature.pending) return new Promise<FeaturePageData>(() => {});
        if (mockGetFeature.error) return Promise.reject(mockGetFeature.error);
        return Promise.resolve(mockGetFeature.result!);
      },
      mergeFeature: async () => null,
    },
  },
}));
vi.mock('../../lib/personas', () => ({
  // ensurePersonasLoaded — no-op: в node-окружении тянет signalr/localStorage.
  ensurePersonasLoaded: () => Promise.resolve(),
  getPersonaById: (id: string) => ({ id, name: 'Персона' }),
  usePersonas: () => [],
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/models', () => ({
  useModels: () => [],
  modelLabel: (v: string | null | undefined) => v ?? '',
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));

// Фиксированное «сейчас».
const NOW = new Date('2026-10-19T12:00:00Z').getTime();

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: [
      { key: 'cut', state: 'done', tone: null },
      { key: 'plan', state: 'done', tone: null },
      { key: 'prod', state: 'active', tone: 'run' },
      { key: 'review', state: 'pending', tone: null },
      { key: 'qa', state: 'pending', tone: null },
      { key: 'final', state: 'pending', tone: null },
      { key: 'merge', state: 'pending', tone: null },
    ],
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chat-key', keyArchived: false, keyProjectId: 'p1', chatCount: 2,
    branch: 'feat/my-feature', createdAt: '2026-10-18T06:00:00Z', taskCount: 5,
    ...over,
  };
}

function pageData(stats: FeatureStats | null): FeaturePageData {
  return {
    run: {
      featureId: 'f1',
      epic: { id: 'f1', title: 'Моя фича' },
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    },
    events: [], eventsTotal: 0,
    stats: stats ?? undefined,
  };
}

// Статистика для мини-сводки: таймлайн (totals + current + segments) + расход.
function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-19T17:20:00Z',
      to: '2026-10-20T12:30:00Z',
      stages: [
        { key: 'cut', startedAt: '2026-10-19T17:20:00Z', barEnd: '2026-10-19T17:55:00Z' },
        { key: 'plan', startedAt: '2026-10-19T17:58:00Z', barEnd: '2026-10-19T18:30:00Z' },
        { key: 'prod', startedAt: '2026-10-20T10:05:00Z', barEnd: null },
      ],
      segments: [
        { from: '2026-10-19T17:20:00Z', to: '2026-10-19T17:55:00Z', kind: 'work', track: 'main', stage: 'cut', taskId: 't-cut' },
        { from: '2026-10-20T10:05:00Z', to: '2026-10-20T11:00:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
      ],
      current: { kind: 'waiting', since: '2026-10-20T12:15:00Z', taskId: 't-prod' },
      totals: {
        elapsedMinutes: 900, workWallMinutes: 300, agentsSumMinutes: 520, notWorkingMinutes: 600,
        waitingMinutes: 120, idleMinutes: 200, waitingCount: 3, longestWaitMinutes: 60,
        errorCount: 2, errorMinutes: 80,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: {
      inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 0, cacheCreationTokens: 0,
      costUsd: 15.6, costPartial: false, hiddenCostModels: [],
      from: '2026-10-19T17:20:00Z',
      shared: null,
    },
    ...over,
  };
}

function render() {
  installFakeDom();
  const win = globalThis as any;
  const doc = (globalThis as any).document;
  if (!win.window.addEventListener) {
    const listeners: Record<string, Set<Function>> = {};
    win.window.addEventListener = (t: string, f: Function) => { (listeners[t] ??= new Set()).add(f); };
    win.window.removeEventListener = (t: string, f: Function) => { listeners[t]?.delete(f); };
    win.window.dispatchEvent = (e: { type: string }) => { listeners[e.type]?.forEach(f => f(e)); return true; };
    win.__winListeners = listeners;
  }
  if (!doc.querySelector) doc.querySelector = () => null;
  const { container } = mount(createElement(FeaturePage, {
    featureId: 'f1', projectId: 'p1', isMobile: false, onBack: () => {},
  }));
  return container;
}

async function settle() {
  await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
}

describe('FeatureMiniSummary (Ф8) через страницу фичи', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    setAllFlags({ 'features-overview': true });
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockGetFeature.pending = false;
    mockGetFeature.error = null;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
    setFeaturePageTab('timeline'); // дефолтная вкладка — «Хронология» (там мини-сводка)
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. stats=null → строки нет (без нулей) ===
  it('stats=null → мини-сводки нет (мутация «рисуем нули» красит)', async () => {
    mockGetFeature.result = pageData(null);
    const c = render();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-mini-summary'), 'строки нет при stats=null').toBeNull();
  });

  // === 2. source=approx → «≈» ===
  it('source=approx → «работа ≈ …» (мутация «без ≈» красит)', async () => {
    mockGetFeature.result = pageData(mkStats({ source: 'approx' }));
    const c = render();
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'mini-summary-row');
    expect(row, 'строка итогов').not.toBeNull();
    expect(textNodes(row!).join(''), '«≈» перед работой при approx').toContain('≈');
  });

  // === 3. costPartial → цены нет ===
  it('costPartial → цены нет в строке (мутация «цена при costPartial» красит)', async () => {
    const s = mkStats();
    s.spend = { ...s.spend!, costPartial: true, hiddenCostModels: ['Qwen'] };
    mockGetFeature.result = pageData(s);
    const c = render();
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'mini-summary-row')!;
    // costUsd=15.6, но costPartial → цены нет (не «$15,60»).
    expect(textNodes(row).join(''), 'цены нет при costPartial').not.toContain('$15,60');
  });

  // === 4. клик «Статистика ›» → вкладка «Статистика» ===
  it('клик «Статистика ›» → вкладка «Статистика» (мутация «другая вкладка» красит)', async () => {
    mockGetFeature.result = pageData(mkStats());
    const c = render();
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'mini-summary-go-stats');
    expect(btn, 'кнопка «Статистика ›»').not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(pageTest.tab(), 'вкладка после клика').toBe('stats');
  });

  // === 5. нет «принято N из M» / «все готово» ===
  it('нет «принято N из M» / «все готово» в мини-сводке', async () => {
    mockGetFeature.result = pageData(mkStats());
    const c = render();
    await settle();
    const mini = findNodeByAttr(c, 'data-testid', 'feature-page-mini-summary');
    expect(mini, 'мини-сводка есть').not.toBeNull();
    const all = textNodes(mini!).join('');
    expect(all, 'нет «принято N из M»').not.toContain('принято');
    expect(all, 'нет «все готово»').not.toContain('все готово');
  });

  // === 6. текущее ожидание (approx) → «ждёт вас» БЕЗ числа: длительность — не поле бэка,
  // на клиенте не вычисляем (минор: «фронт не пересчитывает»). ===
  it('текущее ожидание (approx) → «ждёт вас» без числа (N не вычисляется на клиенте)', async () => {
    mockGetFeature.result = pageData(mkStats({ source: 'approx' }));
    const c = render();
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'mini-summary-row')!;
    const all = textNodes(row).join('');
    expect(all, 'есть «ждёт вас»').toContain('ждёт вас');
    // Мутация «ждёт вас 45 мин» (вычисленное число) красит: после «ждёт вас» нет цифры.
    expect(all, 'после «ждёт вас» нет вычисленного числа минут').not.toMatch(/ждёт вас\s+\d/);
  });
});
