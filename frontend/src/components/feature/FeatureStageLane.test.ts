// Дорожка этапа «Хронологии» (GC-F5a). Рендер-тесты на НАСТОЯЩЕЙ модели buildStageLanes:
// компонент проверяется вместе с моделью, а не на руках нарисованных дорожках.
//
// Покрытие: 3 интервала → 3 button с ожидаемым aria-label и aria-haspopup; skipped →
// «этапа не было», 0 кнопок и точечная линия по всей дорожке; left/width интервалов и
// пауз — ровно axisFrac; цвета по состоянию (done/live/wait/stalled — по модели, не по
// догадке); риска C.danger только там, где lastOutcome; пауза — пунктир между интервалами,
// не после последнего; «↩» — только у prod; «ещё N» — только prod (продуктовое решение
// m1 GC-R-F2: проверяется на ТРЁХ этапах — prod, review и ещё одном; у «Ревью» плана нет,
// факт — «i из n» в aria-label); «впереди» — этап без работы, и его нет у закрытой фичи и
// при approx; зона «впереди»/«ещё N» начинается ровно на axis.timeFrac — там, где
// кончается живой интервал, и не наезжает на интервалы (время не показано дважды);
// минимальная ширина 3 px не сдвигает left; shтрихованная зона «стоит» — только
// когда на дорожке нет штрихованного интервала (не дважды); tabIndex и обработчики — пропсы.
// GC-F10c: пустая дорожка на закрытой оси — «этапа не было» (тот же вид, что у skipped),
// а не пустая полоса «как начатая»; на живой фиче та же дорожка остаётся «впереди»;
// на 360 (interactive=false) дорожка неинтерактивна и с подписью, и с интервалами.
//
// Мутации (в отчёте): (1) «ещё N» без условия prod → «план только у prod» красный;
// (2) done красить accent → «цвета по состоянию» красный; (3) не рисовать паузу →
// «пауза — пунктир» красный; (4) minWidth 0 → «минимальная ширина» красный;
// (5) «только prod» → «не review» → красный на третьем этапе (закрытие m5 GC-R-F5a);
// (6) сдвинуть зону «впереди» левее timeFrac → «геометрия зоны» красный (m6);
// (7) убрать точечную линию отсутствующего этапа → «линия отсутствия этапа» красный (m7);
// (8) убрать ветку noStage (showNoStage = lane.skipped) → «пустая дорожка закрытой оси» красный;
// (9) игнорировать interactive (всегда рендерить button) → «неинтерактивность на 360» красный.
//
// GC-F5aR2 (дефект a67f8057): на десктопе интервал — кнопка-зона касания (несёт aria,
// tabIndex, обработчики) + отдельный видимый слой полосы `lane-band-*` (pointer-events: none).
// Видимая геометрия (left/width, цвета, minWidth) не изменилась — эти проверки читают полосу.
// Покрытие: декоративные слои (пауза, линия/подпись «этапа не было», «↩», «стоит», «впереди»)
// — pointer-events: none; на плотном наборе из 49 интервалов: зона ≥ 24 px высотой,
// накрывает полосу, не шире половины зазора, зоны соседей не пересекаются. На 360 — без зоны.
// Мутации: (10) убрать pointer-events: none у паузы → «декоративные слои» красный;
// (11) зона высотой 10 → «высота зоны» красный; (12) дотянуть зону на весь зазор,
// а не на половину → «зоны соседей не пересекаются» красный.

import { act, createElement } from 'react';
import { describe, it, expect, afterEach } from 'vitest';
import { FeatureStageLane } from './FeatureStageLane';
import { buildStageLanes, axisFrac, type StageLanesModel } from './FeatureLaneModel';
import { formatClock, formatDateTime, formatDuration } from './FeatureStatsBits';
import type { FeatureRound, FeatureRun, FeatureStage, FeatureStageExecutor, StageKey, StageState } from '../../types/featureRun';
import type { StepProgress } from '../../lib/featureProgress';
import type { FeatureStats, FeatureTimeline, StageInterval } from '../../types/featureStats';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { C } from '../../lib/design';

// Фиксированное «сейчас» и окно 12 ч (как в FeatureLaneModel.test.ts): живая фича — timeFrac 0.86.
const NOW = Date.parse('2026-10-20T12:00:00Z');
const AXIS_FROM = '2026-10-20T00:00:00Z';
const AXIS_TO = '2026-10-20T12:00:00Z';

// === Фабрики данных (по образцу FeatureLaneModel.test.ts) ===

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

function mkStage(key: StageKey, state: StageState, over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key,
    state,
    passes: 0,
    taskIds: [],
    rounds: [],
    firstStartedAt: null,
    lastCompletedAt: null,
    barEnd: null,
    approxMinutes: 0,
    overlapsActive: false,
    executors: [],
    ...over,
  };
}

function mkInterval(from: string, to: string, over: Partial<StageInterval> = {}): StageInterval {
  return {
    from,
    to,
    minutes: Math.max(0, Math.round((new Date(to).getTime() - new Date(from).getTime()) / 60000)),
    live: false,
    lastOutcome: null,
    taskIds: [],
    turns: [],
    ...over,
  };
}

function mkStats(
  intervalsByStage: Partial<Record<StageKey, StageInterval[]>> | null = {},
  current: FeatureTimeline['current'] = null,
): FeatureStats {
  const timeline: FeatureTimeline = {
    from: AXIS_FROM,
    to: AXIS_TO,
    stages: [],
    segments: [],
    current,
    intervalsByStage,
    totals: {
      elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0,
      waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
      errorCount: null, errorMinutes: null,
    },
  };
  return { source: 'journal', truncated: false, timeline, breakdown: { personas: [], models: [], roles: [] }, longestNow: null, spend: null };
}

function mkStepProgress(over: Partial<StepProgress> = {}): StepProgress {
  return { planState: 'partial', total: 8, accepted: 3, review: 1, work: 2, notCreated: 3, withdrawn: 0, unlinkedReworks: 0, ...over };
}

// Задача этапа для planFromExecutors (прочие стадии, не prod/review): status решает корзину.
function mkExecutor(status: FeatureStageExecutor['status'], taskId: string): FeatureStageExecutor {
  return { personaId: null, taskId, taskRole: null, stepRef: null, lane: null, status, chatId: null, chatArchived: false, modelLabel: null, modelFull: null };
}

// Основной набор: «Производство» с 3 интервалами (done, done с cancelled, live) и паузами 60/120 мин.
function mainModel(): StageLanesModel {
  return buildStageLanes(
    mkRun({ stages: [mkStage('prod', 'active')] }),
    mkStats({
      prod: [
        mkInterval('2026-10-20T01:00:00Z', '2026-10-20T03:00:00Z'),
        mkInterval('2026-10-20T04:00:00Z', '2026-10-20T06:00:00Z', { lastOutcome: 'cancelled' }),
        mkInterval('2026-10-20T08:00:00Z', '2026-10-20T12:00:00Z', { live: true }),
      ],
    }),
    NOW,
  );
}

type Style = Record<string, string>;

function renderLane(model: StageLanesModel, key: StageKey, label: string, props: Record<string, unknown> = {}) {
  const lane = model.lanes[key];
  const axis = model.axis;
  if (!lane || !axis) throw new Error(`дорожки ${key} или оси нет`);
  installFakeDom();
  const { container } = mount(createElement(FeatureStageLane, { lane, axis, label, ...props }));
  return { container, lane, axis };
}

function styleOf(node: unknown): Style {
  return (node as { style: Style }).style;
}

// Все элементы дерева с тегом tag (uppercase nodeName в fakeDom) — для «ни одного <button>».
function countTag(root: unknown, tag: string): number {
  let n = 0;
  const walk = (x: { nodeType: number; nodeName: string; childNodes: unknown[] }) => {
    if (x.nodeType === 1 && x.nodeName === tag) n += 1;
    for (const c of x.childNodes) walk(c as never);
  };
  walk(root as never);
  return n;
}

afterEach(() => {
  uninstallFakeDom();
});

describe('FeatureStageLane: интервалы', () => {
  it('модель из 3 интервалов → 3 button с ожидаемым aria-label и aria-haspopup=dialog', () => {
    const { container, lane } = renderLane(mainModel(), 'prod', 'Производство');
    expect(lane.intervals.length).toBe(3);
    for (let i = 0; i < 3; i += 1) {
      const iv = lane.intervals[i]!;
      const b = findNodeByAttr(container, 'data-testid', `lane-interval-prod-${i}`);
      expect(b, `интервала ${i} нет`).not.toBeNull();
      const minutes = Math.round((Date.parse(iv.to) - Date.parse(iv.from)) / 60000);
      // Ожидаемая строка — теми же хелперами формата (TZ-независимо): «{Этап}, интервал i из n: …»
      const expected = `Производство, интервал ${i + 1} из 3: ${formatDateTime(iv.from)} – ${formatClock(iv.to)}, ${formatDuration(minutes)}`;
      expect(b!.getAttribute('aria-label'), `aria-label[${i}]`).toBe(expected);
      expect(b!.getAttribute('aria-haspopup')).toBe('dialog');
    }
    // Длительности в aria-label — реальные: 2 ч, 2 ч, 4 ч.
    expect(findNodeByAttr(container, 'data-testid', 'lane-interval-prod-0')!.getAttribute('aria-label')).toContain('2 ч');
    expect(findNodeByAttr(container, 'data-testid', 'lane-interval-prod-2')!.getAttribute('aria-label')).toContain('4 ч');
  });

  it('left/width видимой полосы каждого интервала — ровно axisFrac (±0.01 %); полоса — отдельный слой (GC-F5aR2)', () => {
    const { container, lane, axis } = renderLane(mainModel(), 'prod', 'Производство');
    for (let i = 0; i < lane.intervals.length; i += 1) {
      const iv = lane.intervals[i]!;
      // Геометрия полосы — на слое `lane-band-*`; сам `lane-interval-*` на десктопе — зона касания (ниже).
      const band = findNodeByAttr(container, 'data-testid', `lane-band-prod-${i}`);
      expect(band, `полосы интервала ${i} нет`).not.toBeNull();
      const s = styleOf(band);
      const left = axisFrac(iv.from, axis) * 100;
      const width = (axisFrac(iv.to, axis) - axisFrac(iv.from, axis)) * 100;
      expect(parseFloat(s.left!), `left[${i}]`).toBeCloseTo(left, 2);
      expect(parseFloat(s.width!), `width[${i}]`).toBeCloseTo(width, 2);
    }
  });

  it('цвета по состоянию из модели: done C.success, live C.accent, wait C.info, stalled — штриховка warning', () => {
    // done + live (без тревоги). Цвет — на видимой полосе (data-state — на зоне-интервале).
    const { container } = renderLane(mainModel(), 'prod', 'Производство');
    expect(styleOf(findNodeByAttr(container, 'data-testid', 'lane-band-prod-0')!).background).toBe(C.success);
    expect(styleOf(findNodeByAttr(container, 'data-testid', 'lane-band-prod-2')!).background).toBe(C.accent);

    // wait — по модели: живой интервал, чей taskIds содержит ждущую задачу из current.
    const waitModel = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active', { taskIds: ['t-w'] })] }),
      mkStats({ prod: [mkInterval('2026-10-20T08:00:00Z', '2026-10-20T12:00:00Z', { live: true, taskIds: ['t-w'] })] }, { kind: 'waiting', since: '2026-10-20T09:00:00Z', taskId: 't-w' }),
      NOW,
    );
    const { container: c2 } = renderLane(waitModel, 'prod', 'Производство');
    const waitBtn = findNodeByAttr(c2, 'data-testid', 'lane-interval-prod-0')!;
    expect(waitBtn.getAttribute('data-state')).toBe('wait');
    expect(styleOf(findNodeByAttr(c2, 'data-testid', 'lane-band-prod-0')!).background).toBe(C.info);

    // stalled — по модели: живой интервал стоящего этапа; зона «стоит» поверх него не рисуется
    // второй раз (тот же время не дважды).
    const stalledModel = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active', { taskIds: ['t-1'] })], attention: { kind: 'stopped', since: '2026-10-20T09:00:00Z', taskId: 't-1' } }),
      mkStats({ prod: [mkInterval('2026-10-20T08:00:00Z', '2026-10-20T12:00:00Z', { live: true, taskIds: ['t-1'] })] }),
      NOW,
    );
    const { container: c3 } = renderLane(stalledModel, 'prod', 'Производство');
    const stBtn = findNodeByAttr(c3, 'data-testid', 'lane-interval-prod-0')!;
    expect(stBtn.getAttribute('data-state')).toBe('stalled');
    const bg = styleOf(findNodeByAttr(c3, 'data-testid', 'lane-band-prod-0')!).background;
    expect(bg).toContain(C.warning);
    expect(bg).toContain(C.warningBg);
    expect(findNodeByAttr(c3, 'data-testid', 'lane-stalled-prod'), 'штриховка не дважды').toBeNull();
  });

  it('lastOutcome — риска C.danger только на конце интервала с плохим исходом', () => {
    const { container } = renderLane(mainModel(), 'prod', 'Производство');
    const risk = findNodeByAttr(container, 'data-testid', 'lane-risk-prod-1');
    expect(risk, 'риски у интервала с cancelled нет').not.toBeNull();
    expect(styleOf(risk).background).toBe(C.danger);
    expect(findNodeByAttr(container, 'data-testid', 'lane-risk-prod-0')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'lane-risk-prod-2')).toBeNull();
  });

  it('пауза между интервалами — пунктир textMuted; после последнего — нет', () => {
    const { container, lane, axis } = renderLane(mainModel(), 'prod', 'Производство');
    expect(lane.intervals[0]!.pauseAfterMinutes).toBe(60);
    const p0 = findNodeByAttr(container, 'data-testid', 'lane-pause-prod-0');
    expect(p0, 'пунктира паузы нет').not.toBeNull();
    const s = styleOf(p0);
    expect(s.borderTop).toBe(`1px dashed ${C.textMuted}`);
    expect(parseFloat(s.left!)).toBeCloseTo(axisFrac(lane.intervals[0]!.to, axis) * 100, 2);
    expect(parseFloat(s.width!)).toBeCloseTo((axisFrac(lane.intervals[1]!.from, axis) - axisFrac(lane.intervals[0]!.to, axis)) * 100, 2);
    expect(findNodeByAttr(container, 'data-testid', 'lane-pause-prod-1')).not.toBeNull();
    // Пауз ровно N-1: у последнего интервала их нет.
    expect(findNodeByAttr(container, 'data-testid', 'lane-pause-prod-2')).toBeNull();
  });

  it('минимальная ширина 3 px не сдвигает left: короткий интервал остаётся на своей позиции', () => {
    const model = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active')] }),
      mkStats({ prod: [mkInterval('2026-10-20T11:00:00Z', '2026-10-20T11:01:00Z')] }),
      NOW,
    );
    const { container, axis } = renderLane(model, 'prod', 'Производство');
    const s = styleOf(findNodeByAttr(container, 'data-testid', 'lane-band-prod-0')!);
    expect(parseFloat(s.left!)).toBeCloseTo(axisFrac('2026-10-20T11:00:00Z', axis) * 100, 2);
    expect(s.minWidth).toBe('3px');
  });
});

describe('FeatureStageLane: состояния этапа', () => {
  it('skipped → «этапа не было», 0 кнопок и точечная линия по всей дорожке (m7 GC-R-F5a)', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('final', 'skipped')] }), mkStats({}), NOW);
    const { container } = renderLane(model, 'final', 'Финальное ревью');
    expect(textNodes(container).join(' ')).toContain('этапа не было');
    // Ни одной кнопки-интервала на skipped-дорожке.
    for (let i = 0; i < 5; i += 1) expect(findNodeByAttr(container, 'data-testid', `lane-interval-final-${i}`)).toBeNull();
    // Точечная линия — маркер отсутствия этапа: 1 px dotted C.dashed по всей ширине дорожки.
    const line = findNodeByAttr(container, 'data-testid', 'lane-no-stage-line-final');
    expect(line, 'точечной линии отсутствия этапа нет').not.toBeNull();
    expect(styleOf(line).borderTop, 'линия не пунктирная C.dashed').toBe(`1px dotted ${C.dashed}`);
  });

  it('пустая дорожка на закрытой оси → «этапа не было», а не пустая полоса «как начатая» (GC-F10c)', () => {
    // Та же форма, что на срезе: смерженная фича, этап «Мерж» без интервалов (не skipped).
    const model = buildStageLanes(
      mkRun({ closed: true, state: 'merged', stages: [mkStage('merge', 'pending')] }),
      mkStats({}),
      NOW,
    );
    const { container, axis } = renderLane(model, 'merge', 'Мерж');
    expect(axis.timeFrac).toBe(1);
    expect(textNodes(container).join(' ')).toContain('этапа не было');
    // «впереди» на закрытой оси нет — будущего нет (тот же инвариант, что у зоны «впереди»).
    expect(textNodes(container).join(' ')).not.toContain('впереди');
    expect(findNodeByAttr(container, 'data-testid', 'lane-no-stage-merge'), 'подписи отсутствия этапа нет').not.toBeNull();
    const line = findNodeByAttr(container, 'data-testid', 'lane-no-stage-line-merge');
    expect(line, 'точечной линии нет').not.toBeNull();
    expect(styleOf(line).borderTop).toBe(`1px dotted ${C.dashed}`);
    // Пустая дорожка остаётся пустой: интервалов не придумано.
    for (let i = 0; i < 5; i += 1) expect(findNodeByAttr(container, 'data-testid', `lane-interval-merge-${i}`)).toBeNull();
  });

  it('живая фича: та же пустая дорожка — по-прежнему «впереди», не «не было» (GC-F10c)', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('merge', 'pending')] }), mkStats({}), NOW);
    const { container } = renderLane(model, 'merge', 'Мерж');
    expect(textNodes(container).join(' ')).toContain('впереди');
    expect(textNodes(container).join(' ')).not.toContain('не было');
  });

  it('approx (журнала нет) — пустая дорожка: ни интервалов, ни «впереди»', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')] }), mkStats(null), NOW);
    const { container, lane } = renderLane(model, 'prod', 'Производство');
    expect(lane.approx).toBe(true);
    expect(findNodeByAttr(container, 'data-testid', 'lane-interval-prod-0')).toBeNull();
    expect(textNodes(container).join(' ')).not.toContain('впереди');
  });

  it('этап без работы (pending) — контур «впереди» словом, без числа, от axis.timeFrac', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('merge', 'pending')] }), mkStats({}), NOW);
    const { container, axis } = renderLane(model, 'merge', 'Мерж');
    const ahead = findNodeByAttr(container, 'data-testid', 'lane-ahead-merge');
    expect(ahead, 'контура «впереди» нет').not.toBeNull();
    expect(textNodes(ahead!).join('')).toBe('впереди');
    // Зона начинается ровно на axis.timeFrac — за живой частью оси она, а не поверх неё.
    expect(parseFloat(styleOf(ahead!).left!), 'зона «впереди» не на axis.timeFrac').toBeCloseTo(axis.timeFrac * 100, 2);
  });

  it('зона «ещё N» начинается ровно на axis.timeFrac — на конце живого интервала, и не наезжает на интервалы (m6 GC-R-F5a)', () => {
    // Живой интервал, дотянутый до «сейчас» (= правый край живой части оси).
    const model = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active')], stepProgress: mkStepProgress() }),
      mkStats({ prod: [mkInterval('2026-10-20T08:00:00Z', '2026-10-20T12:00:00Z', { live: true })] }),
      NOW,
    );
    const { container, lane, axis } = renderLane(model, 'prod', 'Производство');
    const zone = findNodeByAttr(container, 'data-testid', 'lane-ahead-prod');
    expect(zone, 'зоны «ещё N» нет').not.toBeNull();
    // Начало зоны = timeFrac = axisFrac конца живого интервала: зона стартует там, где
    // интервал кончается. Сдвиг влево = зона поверх интервала = время дважды → красный.
    const zoneLeft = parseFloat(styleOf(zone).left!);
    expect(zoneLeft, 'зона не на axis.timeFrac').toBeCloseTo(axis.timeFrac * 100, 2);
    const liveEnd = axisFrac(lane.intervals[lane.intervals.length - 1]!.to, axis) * 100;
    expect(zoneLeft, 'зона не на конце живого интервала').toBeCloseTo(liveEnd, 2);
    // Ни один интервал не заезжает под зону: правый край видимой полосы каждого — не правее
    // начала зоны (полоса — слой lane-band-*, GC-F5aR2).
    for (let i = 0; i < lane.intervals.length; i += 1) {
      const s = styleOf(findNodeByAttr(container, 'data-testid', `lane-band-prod-${i}`)!);
      expect(parseFloat(s.left!) + parseFloat(s.width!), `интервал ${i} под зоной`).toBeLessThanOrEqual(zoneLeft + 1e-6);
    }
  });

  it('у закрытой фичи зоны «впереди» нет (timeFrac = 1)', () => {
    const model = buildStageLanes(
      mkRun({ closed: true, state: 'merged', stages: [mkStage('merge', 'pending')] }),
      mkStats({}),
      NOW,
    );
    const { container } = renderLane(model, 'merge', 'Мерж');
    expect(findNodeByAttr(container, 'data-testid', 'lane-ahead-merge')).toBeNull();
    expect(textNodes(container).join(' ')).not.toContain('впереди');
  });

  it('зона «стоит» отдельным элементом — только когда нет штрихованного интервала (начатая задача без живого хода)', () => {
    const model = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active', { taskIds: ['t-1'] })], attention: { kind: 'stopped', since: '2026-10-20T09:00:00Z', taskId: 't-1' } }),
      mkStats({ prod: [mkInterval('2026-10-20T01:00:00Z', '2026-10-20T03:00:00Z', { taskIds: ['t-1'] })] }),
      NOW,
    );
    const { container, axis } = renderLane(model, 'prod', 'Производство');
    const zone = findNodeByAttr(container, 'data-testid', 'lane-stalled-prod');
    expect(zone, 'зоны «стоит» нет').not.toBeNull();
    const bg = styleOf(zone).background;
    expect(bg).toContain(C.warning);
    expect(bg).toContain(C.warningBg);
    expect(parseFloat(styleOf(zone).left!)).toBeCloseTo(axisFrac('2026-10-20T09:00:00Z', axis) * 100, 2);
  });
});

describe('FeatureStageLane: план и возвраты', () => {
  it('«ещё N» — только у «Производства»: ни у «Ревью», ни у любого другого этапа (решение m1 GC-R-F2, m5 GC-R-F5a)', () => {
    const run = mkRun({
      stages: [
        mkStage('prod', 'active'),
        mkStage('review', 'active'),
        // Третий этап — не 'review' и не 'prod', работа уже шла (интервал есть) и у стадии
        // есть план (pending-задача → plan.ahead > 0). Именно на нём краснеет проба
        // «только prod → не review»: без него правило проверялось бы только против «Ревью».
        mkStage('merge', 'active', { executors: [mkExecutor('pending', 't-m')] }),
      ],
      stepProgress: mkStepProgress(),
    });
    const model = buildStageLanes(
      run,
      mkStats({
        prod: [mkInterval('2026-10-20T01:00:00Z', '2026-10-20T03:00:00Z')],
        review: [mkInterval('2026-10-20T04:00:00Z', '2026-10-20T06:00:00Z')],
        merge: [mkInterval('2026-10-20T07:00:00Z', '2026-10-20T07:30:00Z')],
      }),
      NOW,
    );
    // Модель считает plan для всех трёх стадий — решение про показ принадлежит рендеру.
    expect(model.lanes.prod!.plan).not.toBeNull();
    expect(model.lanes.review!.plan).not.toBeNull();
    expect(model.lanes.merge!.plan!.ahead).toBe(1);

    const { container } = renderLane(model, 'prod', 'Производство');
    const more = findNodeByAttr(container, 'data-testid', 'lane-ahead-prod');
    expect(more, 'контура «ещё N» у prod нет').not.toBeNull();
    expect(textNodes(more!).join('')).toBe('ещё 3');

    const { container: cRev } = renderLane(model, 'review', 'Ревью');
    expect(findNodeByAttr(cRev, 'data-testid', 'lane-ahead-review'), 'у ревью плановый контур есть').toBeNull();
    const revTexts = textNodes(cRev).join(' ');
    expect(revTexts).not.toContain('ещё');
    expect(revTexts).not.toContain('3/8');
    // Факт у «Ревью» — в aria-label: «интервал 1 из 1».
    expect(findNodeByAttr(cRev, 'data-testid', 'lane-interval-review-0')!.getAttribute('aria-label')).toContain('интервал 1 из 1');

    // Третий этап: модель план посчитала, интервал начат — и на дорожке всё равно нет
    // планового контура: «ещё N» не едет ни на одну стадию кроме «Производства».
    const { container: cMer } = renderLane(model, 'merge', 'Мерж');
    expect(findNodeByAttr(cMer, 'data-testid', 'lane-ahead-merge'), 'у мержа плановый контур есть').toBeNull();
    expect(textNodes(cMer).join(' ')).not.toContain('ещё');
  });

  it('«↩» возврата — над дорожкой prod в момент вердикта; на прочих этапах — нет', () => {
    const round: FeatureRound = { n: 1, verdict: 'returned', completedAt: '2026-10-20T05:00:00Z', reviewChatId: null };
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active'), mkStage('review', 'active')],
        stepChains: [{ stepRef: 'ST-1', title: 'шаг', taskIds: ['t-1'], outcome: 'returned', status: '', durationMinutes: 0, rounds: [round], chatId: null }],
      }),
      mkStats({
        prod: [mkInterval('2026-10-20T01:00:00Z', '2026-10-20T03:00:00Z')],
        review: [mkInterval('2026-10-20T04:00:00Z', '2026-10-20T06:00:00Z')],
      }),
      NOW,
    );
    const { container, axis } = renderLane(model, 'prod', 'Производство');
    const flag = findNodeByAttr(container, 'data-testid', 'lane-return-prod-0');
    expect(flag, 'флажка возврата нет').not.toBeNull();
    expect(textNodes(flag!).join('')).toBe('↩');
    expect(parseFloat(styleOf(flag).left!)).toBeCloseTo(axisFrac('2026-10-20T05:00:00Z', axis) * 100, 2);

    const { container: cRev } = renderLane(model, 'review', 'Ревью');
    expect(findNodeByAttr(cRev, 'data-testid', 'lane-return-review-0')).toBeNull();
  });
});

describe('FeatureStageLane: неинтерактивность на 360 (критерий c4 GC-F6, держится и в GC-F10c)', () => {
  it('interactive=false: интервалы — не кнопки (div role=img с aria-label), на дорожке ни одного <button>', () => {
    const { container, lane } = renderLane(mainModel(), 'prod', 'Производство', { interactive: false });
    expect(lane.intervals.length).toBe(3);
    for (let i = 0; i < 3; i += 1) {
      const el = findNodeByAttr(container, 'data-testid', `lane-interval-prod-${i}`)!;
      expect(el.nodeName, `интервал ${i} — не div`).toBe('DIV');
      expect(el.getAttribute('role')).toBe('img');
      expect(el.getAttribute('aria-label'), 'aria-label интервала нет').toContain('интервал');
    }
    expect(countTag(container, 'BUTTON'), 'на неинтерактивной дорожке есть кнопка').toBe(0);
  });

  it('interactive=false: пустая дорожка закрытой оси — подпись «этапа не было» и ни одного контрола', () => {
    const model = buildStageLanes(
      mkRun({ closed: true, state: 'merged', stages: [mkStage('merge', 'pending')] }),
      mkStats({}),
      NOW,
    );
    const { container } = renderLane(model, 'merge', 'Мерж', { interactive: false });
    expect(textNodes(container).join(' ')).toContain('этапа не было');
    expect(countTag(container, 'BUTTON'), 'на пустой неинтерактивной дорожке есть контрол').toBe(0);
  });
});

describe('FeatureStageLane: поведение через пропсы', () => {
  it('roving tabIndex: один интервал с tabIndex=0 (по умолчанию первый), остальные -1; tabIndexAt переносит точку', () => {
    const { container } = renderLane(mainModel(), 'prod', 'Производство');
    expect(reactProps(findNodeByAttr(container, 'data-testid', 'lane-interval-prod-0')!)!.tabIndex).toBe(0);
    expect(reactProps(findNodeByAttr(container, 'data-testid', 'lane-interval-prod-1')!)!.tabIndex).toBe(-1);

    const { container: c2 } = renderLane(mainModel(), 'prod', 'Производство', { tabIndexAt: 1 });
    expect(reactProps(findNodeByAttr(c2, 'data-testid', 'lane-interval-prod-0')!)!.tabIndex).toBe(-1);
    expect(reactProps(findNodeByAttr(c2, 'data-testid', 'lane-interval-prod-1')!)!.tabIndex).toBe(0);
  });

  it('expandedAt — aria-expanded только у названного интервала', () => {
    const { container } = renderLane(mainModel(), 'prod', 'Производство', { expandedAt: 1 });
    expect(findNodeByAttr(container, 'data-testid', 'lane-interval-prod-0')!.getAttribute('aria-expanded')).toBe('false');
    expect(findNodeByAttr(container, 'data-testid', 'lane-interval-prod-1')!.getAttribute('aria-expanded')).toBe('true');
  });

  it('обработчики — пропсы родителя: клик и наведение вызывают их с индексом, своего состояния нет', () => {
    const calls: Array<string> = [];
    const model = mainModel();
    const lane = model.lanes['prod']!;
    const axis = model.axis!;
    installFakeDom();
    const { container } = mount(createElement(FeatureStageLane, {
      lane, axis, label: 'Производство',
      onSelect: (i: number) => { calls.push(`select:${i}`); },
      onHover: (i: number | null) => { calls.push(`hover:${i}`); },
    }));
    const b1 = findNodeByAttr(container, 'data-testid', 'lane-interval-prod-1')!;
    const p = reactProps(b1)!;
    act(() => { (p.onClick as () => void)(); });
    act(() => { (p.onMouseEnter as () => void)(); });
    act(() => { (p.onMouseLeave as () => void)(); });
    expect(calls).toEqual(['select:1', 'hover:1', 'hover:null']);
  });
});

// === GC-F5aR2 (дефект a67f8057) ===
//
// Фикстура «всё сразу»: пауза между интервалами, флажок «↩» (returned-раунд), зона «стоит»
// (attention stopped при закрытых интервалах — не штрихованных) и контур «ещё N» (plan.ahead).
// Все эти слои неинтерактивны и обязаны быть прозрачны для указателя.
function decorModel(): StageLanesModel {
  const round: FeatureRound = { n: 1, verdict: 'returned', completedAt: '2026-10-20T05:00:00Z', reviewChatId: null };
  return buildStageLanes(
    mkRun({
      stages: [mkStage('prod', 'active', { taskIds: ['t-1'] })],
      attention: { kind: 'stopped', since: '2026-10-20T09:00:00Z', taskId: 't-1' },
      stepChains: [{ stepRef: 'ST-1', title: 'шаг', taskIds: ['t-1'], outcome: 'returned', status: '', durationMinutes: 0, rounds: [round], chatId: null }],
      stepProgress: mkStepProgress(),
    }),
    mkStats({
      prod: [
        mkInterval('2026-10-20T01:00:00Z', '2026-10-20T03:00:00Z', { taskIds: ['t-1'] }),
        mkInterval('2026-10-20T04:00:00Z', '2026-10-20T06:00:00Z'),
      ],
    }),
    NOW,
  );
}

// Плотный набор: 49 интервалов по 8 мин с зазорами 6 мин — соседние зоны почти касаются
// середины зазора, и границы измеримы по left/width.
function denseModel(): StageLanesModel {
  const iso = (min: number): string => new Date(Date.parse('2026-10-20T00:00:00Z') + min * 60000).toISOString().replace('.000Z', 'Z');
  const intervals: StageInterval[] = [];
  for (let i = 0; i < 49; i += 1) intervals.push(mkInterval(iso(10 + i * 14), iso(18 + i * 14)));
  return buildStageLanes(
    mkRun({ stages: [mkStage('prod', 'active')] }),
    mkStats({ prod: intervals }),
    NOW,
  );
}

describe('FeatureStageLane: зона касания и слои указателя (дефект a67f8057, GC-F5aR2)', () => {
  it('декоративные слои не перехватывают указатель: пауза, линия/подпись «этапа не было», «↩», «стоит», «впереди», полоса — pointer-events: none', () => {
    const { container } = renderLane(decorModel(), 'prod', 'Производство');
    // Фикстура собрана так, чтобы все слои действительно были на дорожке.
    expect(findNodeByAttr(container, 'data-testid', 'lane-pause-prod-0'), 'паузы нет').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'lane-return-prod-0'), 'флажка нет').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'lane-stalled-prod'), 'зоны «стоит» нет').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'lane-ahead-prod'), 'контура «ещё N» нет').not.toBeNull();
    expect(styleOf(findNodeByAttr(container, 'data-testid', 'lane-pause-prod-0')!).pointerEvents, 'пауза перехватывает указатель').toBe('none');
    expect(styleOf(findNodeByAttr(container, 'data-testid', 'lane-return-prod-0')!).pointerEvents, 'флажок перехватывает указатель').toBe('none');
    expect(styleOf(findNodeByAttr(container, 'data-testid', 'lane-stalled-prod')!).pointerEvents, 'зона «стоит» перехватывает указатель').toBe('none');
    expect(styleOf(findNodeByAttr(container, 'data-testid', 'lane-ahead-prod')!).pointerEvents, 'контур «ещё N» перехватывает указатель').toBe('none');
    // Видимая полоса прозрачна для указателя: сквозь неё указатель доходит до зоны.
    expect(styleOf(findNodeByAttr(container, 'data-testid', 'lane-band-prod-0')!).pointerEvents, 'полоса перехватывает указатель').toBe('none');

    // «Этапа не было»: линия и подпись — тот же класс декоративных слоёв (отдельная фикстура).
    const skipped = buildStageLanes(mkRun({ stages: [mkStage('final', 'skipped')] }), mkStats({}), NOW);
    const { container: c2 } = renderLane(skipped, 'final', 'Финальное ревью');
    expect(styleOf(findNodeByAttr(c2, 'data-testid', 'lane-no-stage-line-final')!).pointerEvents, 'линия перехватывает указатель').toBe('none');
    expect(styleOf(findNodeByAttr(c2, 'data-testid', 'lane-no-stage-final')!).pointerEvents, 'подпись перехватывает указатель').toBe('none');
  });

  it('плотный набор из 49 интервалов: зона ≥ 24 px высотой, накрывает полосу, не шире половины зазора, зоны соседей не пересекаются', () => {
    const model = denseModel();
    const { container, lane, axis } = renderLane(model, 'prod', 'Производство');
    expect(lane.intervals.length).toBe(49);
    const zone: Array<{ l: number; w: number; h: number }> = [];
    const band: Array<{ l: number; w: number }> = [];
    for (let i = 0; i < 49; i += 1) {
      const zs = styleOf(findNodeByAttr(container, 'data-testid', `lane-interval-prod-${i}`)!);
      const bs = styleOf(findNodeByAttr(container, 'data-testid', `lane-band-prod-${i}`)!);
      zone.push({ l: parseFloat(zs.left!), w: parseFloat(zs.width!), h: parseFloat(zs.height!) });
      band.push({ l: parseFloat(bs.left!), w: parseFloat(bs.width!) });
    }
    for (let i = 0; i < 49; i += 1) {
      const iv = lane.intervals[i]!;
      // Видимая полоса — ровно axisFrac: видимая геометрия не изменилась.
      expect(band[i]!.l, `полоса left[${i}]`).toBeCloseTo(axisFrac(iv.from, axis) * 100, 2);
      expect(band[i]!.w, `полоса width[${i}]`).toBeCloseTo((axisFrac(iv.to, axis) - axisFrac(iv.from, axis)) * 100, 2);
      const z = zone[i]!;
      const bl = band[i]!.l;
      const bw = band[i]!.w;
      // Высота зоны — не меньше строки дорожки (мутация «зона 10 px» → краснеет).
      expect(z.h, `высота зоны[${i}]`).toBeGreaterThanOrEqual(24);
      // Зона накрывает полосу (не уже видимой полосы).
      expect(z.l, `зона левее полосы[${i}]`).toBeLessThanOrEqual(bl + 1e-6);
      expect(z.l + z.w, `зона не накрывает полосу[${i}]`).toBeGreaterThanOrEqual(bl + bw - 1e-6);
      // Не шире половины зазора до соседа (для первого/последнего — до края дорожки).
      const prevEnd = i === 0 ? 0 : band[i - 1]!.l + band[i - 1]!.w;
      const nextStart = i === 48 ? 100 : band[i + 1]!.l;
      expect(z.l, `зона шире половины левого зазора[${i}]`).toBeGreaterThanOrEqual(bl - (bl - prevEnd) / 2 - 1e-6);
      expect(z.l + z.w, `зона шире половины правого зазора[${i}]`).toBeLessThanOrEqual(bl + bw + (nextStart - bl - bw) / 2 + 1e-6);
      // Зоны соседних интервалов не пересекаются (мутация «дотянуть на весь зазор» → краснеет).
      if (i < 48) expect(z.l + z.w, `зона ${i} перекрывает зону ${i + 1}`).toBeLessThanOrEqual(zone[i + 1]!.l + 1e-6);
    }
  });

  it('на 360 отдельной зоны нет: интервал остаётся одним неинтерактивным элементом (div role=img)', () => {
    const { container } = renderLane(mainModel(), 'prod', 'Производство', { interactive: false });
    expect(findNodeByAttr(container, 'data-testid', 'lane-band-prod-0'), 'на 360 появился отдельный слой полосы').toBeNull();
    const el = findNodeByAttr(container, 'data-testid', 'lane-interval-prod-0')!;
    expect(el.nodeName).toBe('DIV');
    expect(el.getAttribute('role')).toBe('img');
  });
});
