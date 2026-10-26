// Модель дорожек «Хронологии» (GC-F2, план «Гант на Хронологии»). Чистые функции — в node, без DOM.
//
// Покрытие: ось и timeFrac (живая 0.86 / closed и merged 1), axisFrac (доли, clamp по окну);
// интервалы (порядок, паузы, totalMinutes, lastOutcome); состояния (done/live/wait/stalled;
// wait — по задаче из current.waiting, а не по стадии, замечание GC-R-F2 m3);
// зона «стоит» (только из attention.stopped; поиск этапа по taskId; фолбэк на текущий);
// approx и пустой словарь — разные состояния (замечание GC-R-F1); skipped; план «N/M»
// (prod/review из stepProgress, прочие из executors.status); возвраты (только prod);
// обрезка интервалов по границе окна (находка GC-R-B); закрытая фича — без live/stalled,
// даже когда бэк держит prod active из-за хвостов.
//
// Мутации (в отчёте): (1) timeFrac 1 → 0.86 у closed → краснеет «closed/merged: timeFrac 1»;
// (2) не обрезать интервал по окну → краснеет «обрезка по окну»; (3) пустой словарь
// считать approx → краснеет «пустой словарь — не approx»; (4) не искать стоящий этап по
// attention.taskId (всегда текущий) → краснеет «стоит по taskId»; (5) вернуть пометку wait
// по стадии (stage.key === waitKey) → краснеет «wait по задаче, а не по стадии».

import { describe, it, expect } from 'vitest';
import { buildStageLanes, axisFrac, asStageKey, type StageLanesModel, type StageLane } from './FeatureLaneModel';
import type { StepProgress } from '../../lib/featureProgress';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, StageKey, StageState } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline, StageInterval } from '../../types/featureStats';

// Фиксированное «сейчас» для детерминизма; конец оси совпадает с ним (окно живой фичи).
const NOW = Date.parse('2026-10-20T12:00:00Z');
const AXIS_FROM = '2026-10-20T00:00:00Z';
const AXIS_TO = '2026-10-20T12:00:00Z';

// === Фабрики данных (по образцу FeatureGanttModel.test.ts) ===

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

function mkExecutor(taskId: string, status: FeatureStageExecutor['status']): FeatureStageExecutor {
  return {
    personaId: null, taskId, taskRole: null, stepRef: null, lane: null, status,
    chatId: null, chatArchived: false, modelLabel: null, modelFull: null,
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
  axis: { from: string; to: string } = { from: AXIS_FROM, to: AXIS_TO },
): FeatureStats {
  const timeline: FeatureTimeline = {
    from: axis.from,
    to: axis.to,
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

function laneOf(model: StageLanesModel, key: StageKey): StageLane {
  const l = model.lanes[key];
  if (!l) throw new Error(`дорожки ${key} нет`);
  return l;
}

// === Ось и доли ===

describe('ось: время живой фичи занимает 0.86, закрытой — всю ось', () => {
  it('живая фича: timeFrac 0.86; axisFrac — доля времени, clamp по границам окна', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')] }), mkStats(), NOW);
    expect(model.axis).toEqual({ from: AXIS_FROM, to: AXIS_TO, timeFrac: 0.86 });
    const ax = model.axis!;
    // Середина 12-часового окна → половина времени → 0.43 от оси.
    expect(axisFrac('2026-10-20T06:00:00Z', ax)).toBeCloseTo(0.43, 10);
    expect(axisFrac(AXIS_FROM, ax)).toBe(0);
    expect(axisFrac(AXIS_TO, ax)).toBeCloseTo(0.86, 10);
    // За пределами окна — границы, не отрицательная и не «за окном» доля.
    expect(axisFrac('2026-10-19T00:00:00Z', ax)).toBe(0);
    expect(axisFrac('2026-10-21T00:00:00Z', ax)).toBeCloseTo(0.86, 10);
  });

  it('closed и merged: timeFrac 1 — будущего на оси нет (мутация 1 → 0.86 краснеет здесь)', () => {
    const closed = buildStageLanes(
      mkRun({ closed: true, state: 'closed', stages: [mkStage('prod', 'done')] }),
      mkStats(),
      NOW,
    );
    expect(closed.axis!.timeFrac).toBe(1);
    expect(axisFrac(AXIS_TO, closed.axis!)).toBe(1);
    // Смерженная с открытым эпиком (closed:false) — тоже закрытое состояние.
    const merged = buildStageLanes(
      mkRun({ state: 'merged', closed: false, stages: [mkStage('prod', 'active')] }),
      mkStats(),
      NOW,
    );
    expect(merged.axis!.timeFrac).toBe(1);
  });

  it('нет stats — оси нет', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')] }), null, NOW);
    expect(model.axis).toBeNull();
  });
});

// === Интервалы: порядок, паузы, суммы ===

describe('интервалы: порядок по старту, пауза до следующего, сумма минут', () => {
  it('пауза между интервалами → pauseAfterMinutes; totalMinutes — сумма; count', () => {
    const model = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active', { taskIds: ['t-1'] })] }),
      mkStats({
        prod: [
          mkInterval('2026-10-20T10:00:00Z', '2026-10-20T10:40:00Z', { taskIds: ['t-1'] }),
          mkInterval('2026-10-20T11:10:00Z', '2026-10-20T11:20:00Z', { taskIds: ['t-1'] }),
        ],
      }),
      NOW,
    );
    const prod = laneOf(model, 'prod');
    expect(prod.count).toBe(2);
    expect(prod.intervals[0]!.pauseAfterMinutes).toBe(30);
    // У последнего интервала паузы нет.
    expect(prod.intervals[1]!.pauseAfterMinutes).toBe(0);
    expect(prod.totalMinutes).toBe(50);
  });

  it('бэк не гарантирует порядок — модель сортирует; lastOutcome/taskIds проходят как есть', () => {
    const model = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active')] }),
      mkStats({
        prod: [
          mkInterval('2026-10-20T11:00:00Z', '2026-10-20T11:20:00Z', { lastOutcome: 'cancelled', taskIds: ['t-2'] }),
          mkInterval('2026-10-20T10:00:00Z', '2026-10-20T10:40:00Z', { taskIds: ['t-1'] }),
        ],
      }),
      NOW,
    );
    const prod = laneOf(model, 'prod');
    expect(prod.intervals.map(i => i.from)).toEqual(['2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z']);
    expect(prod.intervals[1]!.lastOutcome).toBe('cancelled');
    expect(prod.intervals[0]!.lastOutcome).toBeNull();
    expect(prod.intervals[0]!.taskIds).toEqual(['t-1']);
  });
});

// === Состояния интервалов ===

describe('состояния: live — живой хвост; wait — только из current.waiting', () => {
  it('закрытый интервал — done, живой (live) — live', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active')],
        now: [{ taskId: 't-1', stage: 'prod', title: 'модель' }],
      }),
      mkStats({
        prod: [
          mkInterval('2026-10-20T10:00:00Z', '2026-10-20T10:40:00Z'),
          mkInterval('2026-10-20T11:00:00Z', AXIS_TO, { live: true }),
        ],
      }),
      NOW,
    );
    expect(laneOf(model, 'prod').intervals.map(i => i.state)).toEqual(['done', 'live']);
  });

  it('current.kind waiting — wait у живого интервала ожидающей стадии, другие стадии live', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active', { taskIds: ['t-prod'] }), mkStage('qa', 'active', { taskIds: ['t-qa'] })],
        now: [
          { taskId: 't-qa', stage: 'qa', title: 'проверка' },
          { taskId: 't-prod', stage: 'prod', title: 'модель' },
        ],
      }),
      mkStats(
        {
          prod: [mkInterval('2026-10-20T11:00:00Z', AXIS_TO, { live: true, taskIds: ['t-prod'] })],
          qa: [mkInterval('2026-10-20T11:30:00Z', AXIS_TO, { live: true, taskIds: ['t-qa'] })],
        },
        { kind: 'waiting', since: '2026-10-20T11:00:00Z', taskId: 't-prod' },
      ),
      NOW,
    );
    expect(laneOf(model, 'prod').intervals[0]!.state).toBe('wait');
    expect(laneOf(model, 'qa').intervals[0]!.state).toBe('live');
  });

  it('wait по задаче, а не по стадии: параллельная задача той же стадии — live, ждущая — wait (мутация «по стадии» краснеет)', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active', { taskIds: ['t-wait', 't-work'] })],
      }),
      mkStats(
        {
          prod: [
            mkInterval('2026-10-20T11:00:00Z', AXIS_TO, { live: true, taskIds: ['t-work'] }),
            mkInterval('2026-10-20T11:30:00Z', AXIS_TO, { live: true, taskIds: ['t-wait'] }),
          ],
        },
        { kind: 'waiting', since: '2026-10-20T11:00:00Z', taskId: 't-wait' },
      ),
      NOW,
    );
    const prod = laneOf(model, 'prod');
    // Интервал параллельно работающей задачи той же стадии — не «ждёт».
    expect(prod.intervals[0]!.state).toBe('live');
    // «Ждёт» только интервал, содержащий ждущую задачу.
    expect(prod.intervals[1]!.state).toBe('wait');
  });

  it('current.taskId не опознан (id нет) — не помечаем нигде: живые интервалы остаются live', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active', { taskIds: ['t-prod'] }), mkStage('qa', 'active', { taskIds: ['t-qa'] })],
      }),
      mkStats(
        {
          prod: [mkInterval('2026-10-20T11:00:00Z', AXIS_TO, { live: true })],
          qa: [mkInterval('2026-10-20T11:30:00Z', AXIS_TO, { live: true })],
        },
        { kind: 'waiting', since: '2026-10-20T11:00:00Z', taskId: null },
      ),
      NOW,
    );
    // Неизвестно, кого ждать: молчаливая штриховка «ждёт» у всех стадий — ложь.
    expect(laneOf(model, 'prod').intervals[0]!.state).toBe('live');
    expect(laneOf(model, 'qa').intervals[0]!.state).toBe('live');
  });
});

// === Зона «стоит» ===

describe('стоит: только из attention.stopped, порог фронта не считает', () => {
  const stages = [
    mkStage('prod', 'active', { taskIds: ['t-prod'] }),
    mkStage('qa', 'active', { taskIds: ['t-qa'] }),
  ];

  it('stopped по taskId: stalled.from == attention.since, верх — «сейчас»; живой интервал — stalled', () => {
    const model = buildStageLanes(
      mkRun({
        stages,
        now: [{ taskId: 't-prod', stage: 'prod', title: 'модель', startedAt: '2026-10-20T10:00:00Z' }],
        attention: { kind: 'stopped', taskId: 't-prod', since: '2026-10-20T11:30:00Z', reason: 'нет времени' },
      }),
      mkStats({
        prod: [
          mkInterval('2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z', { taskIds: ['t-prod'] }),
          mkInterval('2026-10-20T11:30:00Z', AXIS_TO, { live: true, taskIds: ['t-prod'] }),
        ],
        qa: [mkInterval('2026-10-20T11:00:00Z', '2026-10-20T11:30:00Z', { live: true, taskIds: ['t-qa'] })],
      }),
      NOW,
    );
    const prod = laneOf(model, 'prod');
    expect(prod.stalled).toEqual({ from: '2026-10-20T11:30:00Z', to: new Date(NOW).toISOString() });
    expect(prod.intervals[1]!.state).toBe('stalled');
    // Другая стадия не стоит.
    const qa = laneOf(model, 'qa');
    expect(qa.stalled).toBeNull();
    expect(qa.intervals[0]!.state).toBe('live');
  });

  it('стоит по записи run.now: taskId стадии нет — поиск по now с этим taskId', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active'), mkStage('qa', 'active')],
        now: [
          { taskId: 't-qa', stage: 'qa', title: 'проверка' },
          { taskId: 't-prod', stage: 'prod', title: 'модель' },
        ],
        attention: { kind: 'stopped', taskId: 't-prod', since: '2026-10-20T11:30:00Z' },
      }),
      mkStats({ prod: [mkInterval('2026-10-20T11:30:00Z', AXIS_TO, { live: true })] }),
      NOW,
    );
    expect(laneOf(model, 'prod').stalled!.from).toBe('2026-10-20T11:30:00Z');
    expect(laneOf(model, 'prod').intervals[0]!.state).toBe('stalled');
    expect(laneOf(model, 'qa').stalled).toBeNull();
  });

  it('taskId нет вовсе — фолбэк на текущий этап (run.now[0])', () => {
    const model = buildStageLanes(
      mkRun({
        stages,
        now: [{ taskId: 't-qa', stage: 'qa', title: 'проверка' }],
        attention: { kind: 'stopped', since: '2026-10-20T11:30:00Z' },
      }),
      mkStats({ qa: [mkInterval('2026-10-20T11:30:00Z', AXIS_TO, { live: true })] }),
      NOW,
    );
    expect(laneOf(model, 'qa').stalled!.from).toBe('2026-10-20T11:30:00Z');
    expect(laneOf(model, 'prod').stalled).toBeNull();
  });

  it('attention.kind wait — НЕ «стоит»: зоны нет', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active')],
        attention: { kind: 'wait', since: '2026-10-20T11:30:00Z' },
      }),
      mkStats({ prod: [mkInterval('2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z')] }),
      NOW,
    );
    expect(laneOf(model, 'prod').stalled).toBeNull();
  });

  it('закрытой фиче — ни «стоит», ни live: хвосты держат prod active, но на оси история (done)', () => {
    const model = buildStageLanes(
      mkRun({
        closed: true,
        state: 'closed',
        stages: [mkStage('prod', 'active', { tone: 'run', taskIds: ['t-prod'] })],
        now: [{ taskId: 't-prod', stage: 'prod', title: 'хвост' }],
        attention: { kind: 'stopped', since: '2026-10-20T11:30:00Z' },
      }),
      mkStats({ prod: [mkInterval('2026-10-20T10:00:00Z', AXIS_TO, { live: true })] }),
      NOW,
    );
    const prod = laneOf(model, 'prod');
    expect(prod.stalled).toBeNull();
    expect(prod.intervals[0]!.state).toBe('done');
  });
});

// === approx и пустой словарь — разные состояния (замечание GC-R-F1) ===

describe('approx — только когда журнала нет; пустой словарь — журнал полный', () => {
  it('intervalsByStage null → approx, интервалов нет', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')] }), mkStats(null), NOW);
    const prod = laneOf(model, 'prod');
    expect(prod.approx).toBe(true);
    expect(prod.count).toBe(0);
    expect(prod.intervals).toEqual([]);
  });

  it('поля нет вовсе (старый ответ) — тоже approx', () => {
    const stats = mkStats({ prod: [mkInterval('2026-10-20T10:00:00Z', '2026-10-20T10:10:00Z')] });
    delete stats.timeline.intervalsByStage;
    const model = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')] }), stats, NOW);
    expect(laneOf(model, 'prod').approx).toBe(true);
  });

  it('пустой словарь {} — не approx: журнал полный, хода не было, этап честно пуст', () => {
    const model = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')] }), mkStats({}), NOW);
    const prod = laneOf(model, 'prod');
    expect(prod.approx).toBe(false);
    expect(prod.count).toBe(0);
    expect(prod.intervals).toEqual([]);
  });

  it('словарь без ключа стадии — этапу это не approx: работы этапа не было', () => {
    const model = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active'), mkStage('review', 'active')] }),
      mkStats({ review: [mkInterval('2026-10-20T11:00:00Z', '2026-10-20T11:20:00Z')] }),
      NOW,
    );
    expect(laneOf(model, 'prod').approx).toBe(false);
    expect(laneOf(model, 'prod').count).toBe(0);
    expect(laneOf(model, 'review').count).toBe(1);
  });
});

// === Этап «пропущен» и «нет в конвейере» ===

describe('skipped — «этапа не было»; absent — дорожки нет', () => {
  it('skipped: без интервалов и плана, даже если словарь бэка дал им интервалы; другие этапы целы', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [
          mkStage('design', 'skipped'),
          mkStage('prod', 'active', { taskIds: ['t-1'] }),
          mkStage('merge', 'absent'),
        ],
      }),
      mkStats({
        design: [mkInterval('2026-10-20T09:00:00Z', '2026-10-20T09:10:00Z')],
        prod: [mkInterval('2026-10-20T10:00:00Z', '2026-10-20T10:40:00Z')],
      }),
      NOW,
    );
    const design = laneOf(model, 'design');
    expect(design.skipped).toBe(true);
    expect(design.count).toBe(0);
    expect(design.intervals).toEqual([]);
    expect(design.totalMinutes).toBe(0);
    expect(design.plan).toBeNull();
    expect(laneOf(model, 'prod').skipped).toBe(false);
    expect(laneOf(model, 'prod').count).toBe(1);
    // absent — этой стадии в конвейере нет: дорожки нет вовсе.
    expect(model.lanes.merge).toBeUndefined();
  });
});

// === План «N/M» ===

describe('план: prod/review — из stepProgress; прочие — из executors.status', () => {
  const sp: StepProgress = {
    planState: 'cut', total: 20, accepted: 12, review: 2, work: 3, notCreated: 5, withdrawn: 0, unlinkedReworks: 0,
  };

  it('prod и review: accepted / work+review / notCreated', () => {
    const model = buildStageLanes(
      mkRun({ stages: [mkStage('prod', 'active'), mkStage('review', 'active')], stepProgress: sp }),
      mkStats(),
      NOW,
    );
    expect(laneOf(model, 'prod').plan).toEqual({ done: 12, inWork: 5, ahead: 5 });
    expect(laneOf(model, 'review').plan).toEqual({ done: 12, inWork: 5, ahead: 5 });
  });

  it('stepProgress нет (старый ответ) или пустой знаменатель — плана нет (null), не нули-фантазия', () => {
    const noSp = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')] }), mkStats(), NOW);
    expect(laneOf(noSp, 'prod').plan).toBeNull();
    const emptySp: StepProgress = {
      planState: 'pending', total: 0, accepted: 0, review: 0, work: 0, notCreated: 0, withdrawn: 0, unlinkedReworks: 0,
    };
    const m = buildStageLanes(mkRun({ stages: [mkStage('prod', 'active')], stepProgress: emptySp }), mkStats(), NOW);
    expect(laneOf(m, 'prod').plan).toBeNull();
  });

  it('cut/qa и прочие — по статусам исполнителей этапа', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [
          mkStage('cut', 'done', { executors: [mkExecutor('t-c1', 'done'), mkExecutor('t-c2', 'pending')] }),
          mkStage('qa', 'active', { executors: [mkExecutor('t-q1', 'running'), mkExecutor('t-q2', 'waiting'), mkExecutor('t-q3', 'stopped')] }),
          mkStage('merge', 'pending', { executors: [] }),
        ],
      }),
      mkStats(),
      NOW,
    );
    expect(laneOf(model, 'cut').plan).toEqual({ done: 1, inWork: 0, ahead: 1 });
    expect(laneOf(model, 'qa').plan).toEqual({ done: 0, inWork: 3, ahead: 0 });
    // Задач этапа нет — считать нечего.
    expect(laneOf(model, 'merge').plan).toBeNull();
  });
});

// === Возвраты: только «Производство» ===

describe('возвраты: флажки только на дорожке «Производства»', () => {
  const mkRound = (n: number, verdict: 'returned' | 'accepted_with_remarks' | 'accepted', completedAt: string | null) => ({
    n, verdict, personaId: 'p-gleb', completedAt, reason: 'не по спецификации', reviewChatId: 'c-review',
  });

  it('два закрытых возврата — два флажка по времени; «принят» и незакрытый виток — не флажок', () => {
    const model = buildStageLanes(
      mkRun({
        stages: [mkStage('prod', 'active'), mkStage('review', 'active')],
        stepChains: [{
          stepRef: 'Ш3', title: 'Модель', taskIds: ['t-1'], outcome: 'running', status: 'идёт',
          durationMinutes: 60,
          rounds: [
            mkRound(1, 'returned', '2026-10-20T09:00:00Z'),
            mkRound(2, 'accepted_with_remarks', '2026-10-20T10:00:00Z'),
            mkRound(3, 'accepted', '2026-10-20T11:00:00Z'),
            mkRound(4, 'returned', null),
          ],
          chatId: null,
        }],
      }),
      mkStats(),
      NOW,
    );
    const prod = laneOf(model, 'prod');
    expect(prod.returns.length).toBe(2);
    expect(prod.returns[0]!.at).toBe('2026-10-20T09:00:00Z');
    expect(prod.returns[0]!.stepRef).toBe('Ш3');
    // На других дорожках флажков нет.
    expect(laneOf(model, 'review').returns).toEqual([]);
  });
});

// === Обрезка по окну (находка GC-R-B) ===

describe('обрезка по границе окна: живой хвост не уезжает за край', () => {
  const MERGE = '2026-10-20T10:00:00Z';

  it('живой интервал за правым краем смерженной фичи — обрезан по окну; axisFrac ≤ 1', () => {
    const model = buildStageLanes(
      mkRun({ closed: true, state: 'closed', stages: [mkStage('prod', 'active', { tone: 'run' })] }),
      mkStats({ prod: [mkInterval('2026-10-20T09:30:00Z', '2026-10-20T11:00:00Z', { live: true })] }, null, { from: AXIS_FROM, to: MERGE }),
      NOW,
    );
    const prod = laneOf(model, 'prod');
    expect(prod.intervals[0]!.to).toBe(MERGE);
    expect(prod.totalMinutes).toBe(30);
    expect(axisFrac(prod.intervals[0]!.to, model.axis!)).toBe(1);
  });

  it('интервал целиком за окном — на дорожке не рисуется', () => {
    const model = buildStageLanes(
      mkRun({ closed: true, state: 'closed', stages: [mkStage('prod', 'active')] }),
      mkStats({ prod: [mkInterval('2026-10-20T10:30:00Z', '2026-10-20T11:00:00Z')] }, null, { from: AXIS_FROM, to: MERGE }),
      NOW,
    );
    expect(laneOf(model, 'prod').count).toBe(0);
  });
});

// === Приведение ключа стадии и пустой run ===

describe('мелочи контракта', () => {
  it('asStageKey: известный ключ — StageKey, неизвестный/пустой — null', () => {
    expect(asStageKey('prod')).toBe('prod');
    expect(asStageKey('merge')).toBe('merge');
    expect(asStageKey('nope')).toBeNull();
    expect(asStageKey(null)).toBeNull();
    expect(asStageKey(undefined)).toBeNull();
  });

  it('нет stages — пустых дорожек нет; axis из таймлайна при этом живёт', () => {
    const model = buildStageLanes(mkRun(), mkStats(), NOW);
    expect(Object.keys(model.lanes)).toEqual([]);
    expect(model.axis).not.toBeNull();
  });
});
