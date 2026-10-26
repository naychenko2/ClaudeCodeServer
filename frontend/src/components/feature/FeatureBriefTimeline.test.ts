// Таймлайн-элементы карточки «Сводка» (MiniTimeline/MiniTimelineAxis/SummaryRow из
// FeatureBriefTimeline) — рендерятся ВНУТРИ карточки (FeatureBriefBlock → LifeSection) на
// вкладке «Хронология». Файл переименован из FeatureMiniSummary.test.ts (F7): корень
// мини-сводки удалён, тест описывает элементы карточки, а не его.
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
import { MiniTimeline, MiniTimelineAxis, SummaryRow } from './FeatureBriefTimeline';
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
    setAllFlags({ 'features-overview': true });
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

  // === 1. stats=null и brief=null → ни блока, ни строки (без нулей) ===
  it('stats=null → блока и строки итогов нет (мутация «рисуем нули» красит)', async () => {
    mockGetFeature.result = pageData(null);
    const c = render();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-brief-block'), 'блока нет при stats=null и brief=null').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'mini-summary-row'), 'строки итогов нет при stats=null').toBeNull();
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

  // === 4. клик «Статистика ›» в хвосте карточки → вкладка «Статистика» ===
  it('клик «Статистика ›» → вкладка «Статистика» (мутация «другая вкладка» красит)', async () => {
    mockGetFeature.result = pageData(mkStats());
    const c = render();
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'brief-go-stats');
    expect(btn, 'кнопка «Статистика ›» в хвосте карточки').not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(pageTest.tab(), 'вкладка после клика').toBe('stats');
  });

  // === 5. нет «принято N из M» / «все готово» в строке итогов (прогресс — «Сделано» карточки, не строка) ===
  it('нет «принято N из M» / «все готово» в строке итогов', async () => {
    mockGetFeature.result = pageData(mkStats());
    const c = render();
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'mini-summary-row');
    expect(row, 'строка итогов есть в хвосте карточки').not.toBeNull();
    const all = textNodes(row!).join('');
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

  // === 7. строка итогов без «идёт N» / «шла N» (оно переехало в «Оценку») ===
  it('строка итогов не содержит «идёт» / «шла» (мутация «вернуть идёт N» красит)', async () => {
    mockGetFeature.result = pageData(mkStats());
    const c = render();
    await settle();
    const all = textNodes(findNodeByAttr(c, 'data-testid', 'mini-summary-row')!).join('');
    expect(all).not.toContain('идёт');
    expect(all).not.toContain('шла');
    expect(all, 'работа и ждёт остались').toContain('работа');
  });

  // === 8. нули длительностей в строке итогов не печатаются (порог Math.round>0, тот же,
  // что у shownDuration; дефект ef029aea, R-F12 п.3). Мутация «снять гейт» красит тест:
  // formatDuration(0) = «0 мин» → «работа 0 мин» / «не работали 0 мин» / «ждала вас 0 мин». ===
  it('нулевые длительности в строке итогов не печатаются (мутация «снять гейт» красит)', async () => {
    // workWallMinutes=0 — «работы» нет, остальное остаётся.
    const s1 = mkStats();
    s1.timeline = { ...s1.timeline, totals: { ...s1.timeline.totals, workWallMinutes: 0 } };
    mockGetFeature.result = pageData(s1);
    const c1 = render();
    await settle();
    const all1 = textNodes(findNodeByAttr(c1, 'data-testid', 'mini-summary-row')!).join('');
    expect(all1, 'нет «работа» при workWallMinutes=0').not.toContain('работа');
    expect(all1, 'остальное остаётся').toContain('ждала вас');

    // waitingMinutes=0 — «ждала вас» нет.
    const s2 = mkStats();
    s2.timeline = { ...s2.timeline, totals: { ...s2.timeline.totals, waitingMinutes: 0 } };
    mockGetFeature.result = pageData(s2);
    const c2 = render();
    await settle();
    const all2 = textNodes(findNodeByAttr(c2, 'data-testid', 'mini-summary-row')!).join('');
    expect(all2, 'нет «ждала вас» при waitingMinutes=0').not.toContain('ждала вас');
    expect(all2, '«работа» остаётся').toContain('работа');

    // notWorkingMinutes=0 (ветка без current и без waiting-данных) — «не работали» нет.
    const s3 = mkStats();
    s3.timeline = { ...s3.timeline, current: null, totals: { ...s3.timeline.totals, waitingMinutes: null, notWorkingMinutes: 0 } };
    mockGetFeature.result = pageData(s3);
    const c3 = render();
    await settle();
    const all3 = textNodes(findNodeByAttr(c3, 'data-testid', 'mini-summary-row')!).join('');
    expect(all3, 'нет «не работали» при notWorkingMinutes=0').not.toContain('не работали');
    expect(all3, '«работа» остаётся').toContain('работа');
  });

  // Все три нуля и нет расхода — выводить нечего: строка остаётся пустой (как сейчас
  // ведёт себя пустая строка итогов — без специальной логики скрытия).
  it('все нули и нет расхода — строка итогов пустая (без специальной логики скрытия)', async () => {
    const s = mkStats();
    s.timeline = { ...s.timeline, current: null, totals: { ...s.timeline.totals, workWallMinutes: 0, notWorkingMinutes: 0, waitingMinutes: null } };
    s.spend = null;
    mockGetFeature.result = pageData(s);
    const c = render();
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'mini-summary-row');
    expect(row, 'строка остаётся (пустой)').not.toBeNull();
    const all = textNodes(row!).join('');
    expect(all, 'ничего не печатается').not.toContain('работа');
    expect(all).not.toContain('не работали');
    expect(all).not.toContain('токенов');
  });

  // === 9. экспорты для блока сводки: полоска, ось, итоги ===
  // Closedность — переданная из общей модели состояния (R-F8): ось и полоска слушают
  // проп closed, а не сырой current — пробы ниже расцепляют их намеренно.
  it('экспортирует полоску, ось и итоги; ось: «сейчас» у идущей, «закрыта» у закрытой', () => {
    const run = mkStats();
    const html = renderToStaticMarkup(createElement(MiniTimelineAxis, { stats: run, closed: false }));
    expect(html).toContain('создана');
    expect(html).toContain('сейчас');
    const closed = mkStats();
    closed.timeline = { ...closed.timeline, current: null };
    const htmlClosed = renderToStaticMarkup(createElement(MiniTimelineAxis, { stats: closed, closed: true }));
    expect(htmlClosed).toContain('закрыта');
    expect(htmlClosed).not.toContain('сейчас');
    expect(renderToStaticMarkup(createElement(MiniTimeline, { stats: run, closed: false }))).toContain('mini-timeline');
    expect(renderToStaticMarkup(createElement(SummaryRow, { stats: run }))).toContain('mini-summary-row');
  });

  it('ось слушает переданный closed, а не сырой current (R-F8: closed — из общей модели)', () => {
    const run = mkStats(); // current != null
    const open = mkStats();
    open.timeline = { ...open.timeline, current: null };
    // модель сказала «закрыта» при живом current — ось закрывается;
    const htmlClosed = renderToStaticMarkup(createElement(MiniTimelineAxis, { stats: run, closed: true }));
    expect(htmlClosed, 'закрыта по переданной closed').toContain('закрыта');
    expect(htmlClosed).not.toContain('сейчас');
    // модель сказала «не закрыта» при пустом current — ось остаётся открытой.
    const htmlOpen = renderToStaticMarkup(createElement(MiniTimelineAxis, { stats: open, closed: false }));
    expect(htmlOpen, 'сейчас по переданной closed=false').toContain('сейчас');
    expect(htmlOpen).not.toContain('закрыта');
  });
});
