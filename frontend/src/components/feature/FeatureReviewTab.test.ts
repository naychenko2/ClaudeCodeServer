// Тесты сборки вкладки «Разбор» одной страницей (FS-FR). Проверяется ТОЛЬКО сборка: порядок
// «итог → источник → якоря → шесть секций» по REVIEW_SECTIONS, прокрутка по клику якоря,
// пустые состояния, мобильная высота якорей. Тела секций тестируют их файлы (FS-FR0–FS-FR6).
// Внутри вкладки нет SegmentedControl/role=tablist; aria-pressed допустим только у чипов легенды
// карточки «Исход ревью» (FeatureReturnsOutcomeBar): «Разбор» — страница, не переключатель.
//
// Мутации (проверены прогоном в копии дерева, 11.11): (1) при stats=null рендерить секцию →
// краснеет ровно «stats=null → «Статистики пока нет», ни якорей, ни секций» (8 зелёных);
// (2) не передавать isMobile в ReviewAnchors → краснеет ровно «на 360 все шесть якорей —
// не ниже 40 px» (якорь 32 px, 8 зелёных); (3) убрать AccuracyBadges из сборки → краснеют
// «порядок страницы» и «источник и полнота цифр» (7 зелёных); (4) переставить volume перед
// time → краснеет ровно «порядок страницы» (8 зелёных); (5) вынести легенду «Исхода ревью»
// из карточки → краснеет ровно «aria-pressed — только у чипов легенды…» (8 зелёных, 11.11).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureReviewTab } from './FeatureReviewTab';
import { outcomeBarModel } from './FeatureReturnsOutcomeBar';
import { REVIEW_SECTIONS } from './FeatureReviewParts';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureStage } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

// === Мок окружения (переходы и подписей в этой задаче не проверяем — они в файлах секций) ===
const mockOpenTask = vi.hoisted(() => ({ calls: [] as unknown[] }));
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
const mockPersonas = vi.hoisted(() => [
  { id: 'p-kira', name: 'Кира', role: 'Frontend' },
  { id: 'p-denis', name: 'Денис', role: 'Backend' },
]);
vi.mock('../../lib/tasks', () => ({
  openTaskInSection: (t: unknown) => { mockOpenTask.calls.push(t); },
}));
vi.mock('../../lib/openChat', () => ({
  openChatById: (id: string) => { mockOpenChat.calls.push(id); return Promise.resolve(true); },
}));
// signalr: featurePage/лента тянут onMessage — подписки в тесте не нужны.
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return {
    ...orig,
    usePersonas: () => mockPersonas,
    getPersonaById: (id: string) => mockPersonas.find((p) => p.id === id),
  };
});
vi.mock('../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/models')>();
  return { ...orig, useModels: () => [] };
});

// === Фикстуры ===

function mkStage(over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key: 'prod', state: 'pending', tone: null, passes: 0, taskIds: [], runningSince: null,
    rounds: [], longestStepRef: null, firstStartedAt: null, lastCompletedAt: null, barEnd: null,
    approxMinutes: 0, overlapsActive: false, executors: [], ...over,
  };
}

// Фича в работе: prod активен, review завершён — enough для строки итога и всех секций.
function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 1, acceptedWithRemarks: 0, returned: 2, withdrawn: 0, loops: 2 },
    progress: 40, ready: false, closed: false,
    stages: [
      mkStage({ key: 'prod', state: 'active', tone: 'run', firstStartedAt: '2026-10-20T10:00:00Z', approxMinutes: 250, taskIds: ['t-prod'] }),
      mkStage({ key: 'review', state: 'done', firstStartedAt: '2026-10-20T11:20:00Z', lastCompletedAt: '2026-10-20T12:00:00Z', barEnd: '2026-10-20T12:00:00Z', approxMinutes: 40, taskIds: ['t-rev'] }),
    ],
    ...over,
  } as FeatureRun;
}

function mkTotals(over: Partial<FeatureTimeline['totals']> = {}): FeatureTimeline['totals'] {
  return {
    elapsedMinutes: 200, workWallMinutes: 120, agentsSumMinutes: 150, notWorkingMinutes: 0,
    waitingMinutes: 60, idleMinutes: 20, waitingCount: 1, longestWaitMinutes: 60,
    errorCount: 0, errorMinutes: 0, ...over,
  };
}

// Полная статистика открытой фичи: журнал с сегментами и «сейчас» (модель hasData=true).
function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T13:20:00Z',
      stages: [],
      segments: [
        { from: '2026-10-20T10:00:00Z', to: '2026-10-20T12:00:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
        { from: '2026-10-20T12:00:00Z', to: '2026-10-20T13:00:00Z', kind: 'waiting', track: 'main', stage: 'prod', taskId: null },
      ],
      current: { kind: 'waiting', since: '2026-10-20T13:20:00Z', taskId: 't-prod' },
      totals: mkTotals(),
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: {
      inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 500000, cacheCreationTokens: 100000,
      costUsd: 15.6, costPartial: false, hiddenCostModels: [], from: '2026-10-20T10:00:00Z',
    },
    ...over,
  } as FeatureStats;
}

// === Рендер ===

type AnyNode = {
  nodeType: number; childNodes: AnyNode[]; attributes?: Record<string, string>;
  getAttribute: (a: string) => string | null; style: Record<string, string>;
  scrollIntoView?: (opts?: unknown) => void;
};

const roots: { unmount: () => void }[] = [];

function renderTab(over: Partial<Parameters<typeof FeatureReviewTab>[0]> = {}): AnyNode {
  installFakeDom();
  const { root, container } = mount(createElement(FeatureReviewTab, {
    run: mkRun(), stats: mkStats(), brief: null, events: [], eventsTotal: 0,
    featureId: 'f-1', featureTitle: 'Фича', ...over,
  }));
  roots.push(root);
  // Прокрутка ищется через document.getElementById — в fakeDom он обходит body:
  // контейнер ставится в body, как в настоящем документе, иначе якорь не нашёл бы секцию.
  const doc = (globalThis as { document: { body: { appendChild: (n: unknown) => unknown } } }).document;
  doc.body.appendChild(container);
  return container as unknown as AnyNode;
}

// data-testid в порядке документа — проверить место блока (порядок сборки).
function testIdsInOrder(n: AnyNode): string[] {
  const out: string[] = [];
  const walk = (x: AnyNode) => {
    const id = x.attributes?.['data-testid'];
    if (id) out.push(id);
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return out;
}

// Значения атрибута у всех элементов поддерева (для «ни role=tablist»).
function attrDeep(n: AnyNode, attr: string): string[] {
  const out: string[] = [];
  if (n.nodeType === 1) {
    const v = n.getAttribute(attr);
    if (v != null) out.push(v);
  }
  for (const c of n.childNodes) out.push(...attrDeep(c, attr));
  return out;
}

// Узлы с aria-pressed и признак «внутри карточки «Исход ревью»» — для проверки, что
// aria-pressed на странице принадлежит только чипам легенды этой карточки.
function ariaPressedNodes(n: AnyNode, insideCard: boolean): { testid: string; insideCard: boolean }[] {
  const out: { testid: string; insideCard: boolean }[] = [];
  if (n.nodeType === 1) {
    const testid = n.attributes?.['data-testid'] ?? '';
    if (n.getAttribute('aria-pressed') != null) out.push({ testid, insideCard });
    insideCard = insideCard || testid === 'returns-outcome';
  }
  for (const c of n.childNodes) out.push(...ariaPressedNodes(c, insideCard));
  return out;
}

describe('FeatureReviewTab: сборка страницы', () => {
  beforeEach(() => {
    mockOpenTask.calls = [];
    mockOpenChat.calls = [];
  });
  afterEach(() => {
    // useNow («сейчас» в дорожках) и состояние строки итога живут на подписках: снимаем
    // монтирование, иначе интервалы переходят между тестами.
    for (const r of roots.splice(0)) act(() => { r.unmount(); });
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('порядок страницы: итог → источник → якоря → шесть секций в порядке REVIEW_SECTIONS', () => {
    const c = renderTab();
    const seq = testIdsInOrder(c);
    const want = ['feature-analysis-line', 'stats-accuracy', 'review-anchors', ...REVIEW_SECTIONS.map((s) => 'review-sec-' + s.id)];
    let prev = -1;
    for (const id of want) {
      const i = seq.indexOf(id);
      expect(i, `блок ${id} на странице`).toBeGreaterThanOrEqual(0);
      expect(i, `${id} — после предыдущего`).toBeGreaterThan(prev);
      prev = i;
    }
    // Секций ровно шесть: лишней («Статистики», «Чаты») здесь нет.
    expect(seq.filter((id) => id.startsWith('review-sec-')).length).toBe(6);
  });

  it('внутри вкладки нет role=tablist; aria-pressed — только у чипов легенды в карточке «Исход ревью»: это страница, не переключатель', () => {
    const c = renderTab();
    expect(attrDeep(c, 'role').filter((r) => r === 'tablist').length, 'role=tablist').toBe(0);
    const press = ariaPressedNodes(c, false);
    // Чипов ровно столько, сколько сегментов исхода (все три, даже с нулевым числом).
    expect(press.length, 'число aria-pressed').toBe(outcomeBarModel(mkRun().verdicts).segments.length);
    // Каждый принадлежит чипу returns-chip-* внутри карточки; вне карточки их нет.
    for (const p of press) {
      expect(p.insideCard, `aria-pressed вне карточки «Исход ревью» (${p.testid})`).toBe(true);
      expect(p.testid, 'aria-pressed на узле вне чипа легенды').toMatch(/^returns-chip-(accepted|remarks|returned)$/);
    }
  });

  it('якоря — по шесть и по ключам REVIEW_SECTIONS (testid и id секции совпадают)', () => {
    const c = renderTab();
    const row = findNodeByAttr(c, 'data-testid', 'review-anchors') as unknown as AnyNode;
    expect(row, 'ряд якорей').toBeTruthy();
    expect(row.childNodes.length).toBe(6);
    REVIEW_SECTIONS.forEach((s, i) => {
      const btn = row.childNodes[i]!;
      expect(btn.attributes?.['data-testid'], `якорь ${s.id}`).toBe('review-anchor-' + s.id);
      // Ключ якоря = ключ секции: якорь ведёт к rz-<ключ REVIEW_SECTIONS>.
      expect(findNodeByAttr(c, 'id', 'rz-' + s.id), `якорь прокрутки rz-${s.id}`).toBeTruthy();
    });
  });

  it('клик якоря «Расход» → scrollIntoView у #rz-spend ровно один раз; переходов больше нет', () => {
    const c = renderTab();
    const sec = findNodeByAttr(c, 'id', 'rz-spend') as unknown as AnyNode;
    expect(sec, 'секция «Расход»').toBeTruthy();
    const calls: unknown[] = [];
    sec.scrollIntoView = (opts?: unknown) => { calls.push(opts); };
    const btn = findNodeByAttr(c, 'data-testid', 'review-anchor-spend') as unknown as AnyNode;
    act(() => { (reactProps(btn)!.onClick as () => void)(); });
    expect(calls.length, 'прокрутка к #rz-spend').toBe(1);
    expect(calls[0], 'прокрутка к началу секции').toEqual({ block: 'start' });
    // Вкладка и стор не меняются: клик якоря не открывает ни задачу, ни чат.
    expect(mockOpenTask.calls.length, 'переход к задаче').toBe(0);
    expect(mockOpenChat.calls.length, 'переход к чату').toBe(0);
  });

  // Мутация (2): не передать isMobile в ReviewAnchors → этот тест краснеет (якорь 32 px).
  it('на 360 все шесть якорей — высотой не меньше 40 px (isMobile доходит до ReviewAnchors)', () => {
    const c = renderTab({ isMobile: true });
    const row = findNodeByAttr(c, 'data-testid', 'review-anchors') as unknown as AnyNode;
    expect(row.childNodes.length).toBe(6);
    for (const b of row.childNodes) {
      expect(b.style.minHeight, 'тач-цель якоря ниже 40 px').toBe('40px');
    }
    // Ряд прокручивается сам: страница не переполняется.
    expect(row.style.overflowX, 'ряд не прокручивается').toBe('auto');
  });

  it('без сигнала мобилы якорь остаётся кита sm (32) — высота решает переданный isMobile', () => {
    const c = renderTab({ isMobile: false });
    const row = findNodeByAttr(c, 'data-testid', 'review-anchors') as unknown as AnyNode;
    for (const b of row.childNodes) {
      expect(b.style.minHeight).toBe('32px');
    }
  });

  // Мутация (1): при stats=null рендерить якоря и секции → этот тест краснеет.
  it('stats=null → «Статистики пока нет», ни якорей, ни секций; строка итога остаётся', () => {
    const c = renderTab({ stats: null });
    const all = textNodes(c as never).join('');
    expect(all).toContain('Статистики пока нет');
    expect(findNodeByAttr(c as never, 'data-testid', 'feature-analysis-line'), 'строка итога').toBeTruthy();
    expect(findNodeByAttr(c as never, 'data-testid', 'review-anchors'), 'ряд якорей при stats=null').toBeNull();
    for (const s of REVIEW_SECTIONS) {
      expect(findNodeByAttr(c as never, 'data-testid', 'review-sec-' + s.id), `секция ${s.id} при stats=null`).toBeNull();
    }
  });

  it('модель без данных (ни сегментов, ни «сейчас») → «Считать пока нечего», ни якорей, ни секций', () => {
    const stats = mkStats();
    stats.timeline.segments = [];
    stats.timeline.current = null;
    const c = renderTab({ stats });
    const all = textNodes(c as never).join('');
    expect(all).toContain('Считать пока нечего');
    expect(findNodeByAttr(c as never, 'data-testid', 'review-anchors'), 'ряд якорей без данных').toBeNull();
    for (const s of REVIEW_SECTIONS) {
      expect(findNodeByAttr(c as never, 'data-testid', 'review-sec-' + s.id), `секция ${s.id} без данных`).toBeNull();
    }
  });

  it('источник и полнота цифр — та же строка, что на «Статистике» (AccuracyBadges)', () => {
    const c = renderTab();
    const badges = findNodeByAttr(c, 'data-testid', 'stats-accuracy') as unknown as AnyNode;
    expect(badges, 'строка источника').toBeTruthy();
    expect(textNodes(badges as never).join(''), 'фраза journal-источника').toBe('по журналу ходов');
    const truncated = renderTab({ stats: mkStats({ truncated: true }) });
    expect(findNodeByAttr(truncated as never, 'data-testid', 'stats-accuracy-truncated'), '«журнал обрезан»').toBeTruthy();
  });
});
