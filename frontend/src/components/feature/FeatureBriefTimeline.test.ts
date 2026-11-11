// Строка итогов карточки «Сводка» (SummaryRow из FeatureBriefTimeline) — рендерится ВНУТРИ
// карточки (FeatureBriefBlock → TotalsRow) на вкладке «Хронология». Полоска и ось «Жизни фичи»
// из сводки убраны (макет feature-summary-2) и живут на вкладке «Статистика».
// Рендер-тесты ЧЕРЕЗ страницу фичи (FeaturePage) с НАСТОЯЩИМ ответом `features/{id}`
// (mock api.tasks.getFeature → page.stats; без brief — карточка в состоянии «Сводка
// не загрузилась», полоска и итоги под ней рисуются — макет §3):
//   · stats=null и brief=null → ни блока, ни строки (без нулей);
//   · source=approx → «≈»;
//   · costPartial → цены нет;
//   · клик «Статистика ›» (brief-go-stats) → вкладка «Статистика»;
//   · нет «принято N из M» / «все готово» в строке итогов.
//
// Окружение node (без jsdom): fakeDom (mount) + mock api (tasks.getFeature) + управляемые
// сторы (featurePage, featuresIndex, featureBackNav) — тот же приём, что FeaturePage.test.ts.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { FeaturePage } from './FeaturePage';
import { SummaryRow } from './FeatureBriefTimeline';
import { __test as pageTest, setFeaturePageTab } from '../../lib/featurePage';
import { __test as indexTest } from '../../lib/featuresIndex';
import { __test as backNavTest } from '../../lib/featureBackNav';
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
    chats: { get: async (_id: string) => null },
    tasks: {
      featuresIndex: async () => mockIndex,
      getFeature: (_fid: string) => {
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

// Статистика для карточки «Сводка»: таймлайн (totals + current + segments) + расход.
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

// Типизованный шим window/document: ровно те методы, что использует сам тест
// (приём genPanelDismissed.test.ts — без any/Function).
type Evt = { type: string };
type Handler = (e: Evt) => void;

function render() {
  installFakeDom();
  const win = globalThis as unknown as {
    window: {
      addEventListener?: (t: string, f: Handler) => void;
      removeEventListener?: (t: string, f: Handler) => void;
      dispatchEvent?: (e: Evt) => boolean;
    };
    document: { querySelector?: (sel: string) => null };
    __winListeners: Record<string, Set<Handler>>;
  };
  if (!win.window.addEventListener) {
    const listeners: Record<string, Set<Handler>> = {};
    win.window.addEventListener = (t, f) => { (listeners[t] ??= new Set()).add(f); };
    win.window.removeEventListener = (t, f) => { listeners[t]?.delete(f); };
    win.window.dispatchEvent = (e) => { listeners[e.type]?.forEach(f => f(e)); return true; };
    win.__winListeners = listeners;
  }
  if (!win.document.querySelector) win.document.querySelector = () => null;
  const { container } = mount(createElement(FeaturePage, {
    featureId: 'f1', projectId: 'p1', isMobile: false, onBack: () => {},
  }));
  return container;
}

async function settle() {
  await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
}

describe('Таймлайн-элементы карточки «Сводка» через страницу фичи', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockGetFeature.pending = false;
    mockGetFeature.error = null;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
    setFeaturePageTab('timeline'); // дефолтная вкладка — «Хронология» (там карточка «Сводка»)
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 9. экспорт для блока сводки: строка итогов с первым фактом ===
  it('SummaryRow: lead («идёт N» / «в очереди N») идёт первым фактом; без lead — только данные stats', () => {
    const run = mkStats();
    const withLead = renderToStaticMarkup(createElement(SummaryRow, { stats: run, lead: { label: 'идёт', value: '1 ч 10 мин' } }));
    expect(withLead).toContain('mini-summary-row');
    expect(withLead.replace(/<[^>]+>/g, '').startsWith('идёт 1 ч 10 мин')).toBe(true);
    const without = renderToStaticMarkup(createElement(SummaryRow, { stats: run }));
    expect(without.replace(/<[^>]+>/g, '')).not.toContain('идёт');
  });
});
