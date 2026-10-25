// Модель ступеней Ганта «Статистики» (волна 4, W4-4F1, спецификация features-overview-4-spec.md §6A).
// Чистые функции — в node, без DOM.
//
// Покрытие: обычный путь (строки, длительности, разбивка, сегменты), возвраты (два возврата
// одного шага — два флажка и счётчик; «принят» и незакрытый виток — не флажок; qa/final —
// считаются), «зависла» (stopped — хвост есть, wait — нет), активный этап (пилюля с
// длительностью; закрытая фича — без пилюли), пустая фича, «самый долгий» (один этап —
// не подсвечиваем, равенство — первый по порядку).
//
// Мутации (в отчёте): (1) убрать «waiting» из разбивки → краснеет «обычный путь»;
// (2) не фильтровать вердикт «accepted» → краснеет «возвраты»; (3) штриховать и при
// «wait» → краснеет «зависла»; (4) пилюлю давать и закрытой фиче → краснеет «активный
// этап»; (5) убрать поиск хвоста по attention.taskId → краснеет «зависла по taskId»;
// (6) вернуть hasData по строкам run.stages → краснеет «hasData — по таймлайну»;
// (7) давать строку «тихо» при любом состоянии → краснеет «не тихо — строки нет»;
// (8) не гасить «сейчас» в quiet → краснеет «в тихой — маркера „сейчас“ нет»;
// (9) разная арифметика длительности в двух местах (ceil в модели) → краснеет
// «длительность одна».

import { describe, it, expect } from 'vitest';
import { buildGanttModel, type GanttModel, type GanttRow } from './FeatureGanttModel';
import { analysisLineState } from './FeatureAnalysisModel';
import { formatDuration } from './FeatureStatsBits';
import type { FeatureRun, FeatureStage, StageKey, StageState } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

// Фиксированное «сейчас» для детерминизма.
const NOW = Date.parse('2026-10-20T12:00:00Z');

// === Хелперы данных ===

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

function mkStage(key: StageKey, state: StageState, minutes: number, over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key,
    state,
    passes: 0,
    taskIds: [],
    rounds: [],
    firstStartedAt: null,
    lastCompletedAt: null,
    barEnd: null,
    approxMinutes: minutes,
    overlapsActive: false,
    executors: [],
    ...over,
  };
}

type Seg = { from: string; to: string; kind: 'work' | 'waiting' | 'idle' | 'error' | 'notWorking'; track: 'main' | 'review'; stage: string; taskId: string | null };

function seg(from: string, to: string, kind: Seg['kind'], stage: string, track: 'main' | 'review' = 'main'): Seg {
  return { from, to, kind, track, stage, taskId: null };
}

function mkStats(segments: Seg[]): FeatureStats {
  const timeline: FeatureTimeline = {
    from: '2026-10-19T17:00:00Z',
    to: '2026-10-20T12:00:00Z',
    stages: [],
    segments,
    current: null,
    totals: {
      elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0,
      waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
      errorCount: null, errorMinutes: null,
    },
  };
  return { source: 'approx', truncated: false, timeline, breakdown: { personas: [], models: [], roles: [] }, longestNow: null, spend: null };
}

function rowOf(model: GanttModel, key: StageKey): GanttRow {
  const r = model.rows.find(x => x.key === key);
  if (!r) throw new Error(`строки ${key} нет`);
  return r;
}

// === Обычный путь ===

describe('обычный путь: строки этапов, длительности, разбивка, сегменты', () => {
  const model = buildGanttModel(
    mkRun({
      stages: [
        mkStage('cut', 'done', 35, { firstStartedAt: '2026-10-19T17:00:00Z', lastCompletedAt: '2026-10-19T17:35:00Z', barEnd: '2026-10-19T17:35:00Z' }),
        mkStage('plan', 'done', 32),
        mkStage('prod', 'active', 55, { firstStartedAt: '2026-10-20T10:00:00Z', tone: 'run' }),
        mkStage('review', 'pending', 0),
        mkStage('merge', 'absent', 0),
      ],
    }),
    mkStats([
      seg('2026-10-19T17:00:00Z', '2026-10-19T17:35:00Z', 'work', 'cut'),
      seg('2026-10-20T10:00:00Z', '2026-10-20T10:40:00Z', 'work', 'prod'),
      seg('2026-10-20T10:40:00Z', '2026-10-20T10:50:00Z', 'waiting', 'prod'),
      seg('2026-10-20T10:50:00Z', '2026-10-20T10:55:00Z', 'idle', 'prod'),
      // Ревью-трек — отдельный вид сегмента, в разбивку «работа · ожидание · простой» не входит.
      seg('2026-10-20T10:00:00Z', '2026-10-20T10:20:00Z', 'work', 'review', 'review'),
    ]),
    NOW,
  );

  it('этапа «не было» — строки нет; остальные — в порядке конвейера', () => {
    expect(model.hasData).toBe(true);
    expect(model.rows.map(r => r.key)).toEqual(['cut', 'plan', 'prod', 'review']);
  });

  it('разбивка «работа · ожидание · простой» по минутам main-сегментов', () => {
    const prod = rowOf(model, 'prod');
    expect(prod.minutes).toBe(55);
    expect(prod.breakdown).toEqual({ work: 40, waiting: 10, idle: 5, error: 0, notWorking: 0 });
    // Сегменты полосы: main + review, в хронологическом порядке, минуты посчитаны.
    expect(prod.segments.length).toBe(3);
    expect(prod.segments.map(s => s.kind)).toEqual(['work', 'waiting', 'idle']);
    expect(prod.segments[0]!.minutes).toBe(40);
    // Этап без сегментов — разбивки нет (явный null), а не нули-фантазия.
    expect(rowOf(model, 'plan').breakdown).toBeNull();
  });

  it('error и notWorking — явные части разбивки: сумма равна длине этапа (не теряются)', () => {
    const model = buildGanttModel(
      mkRun({ stages: [mkStage('prod', 'active', 75)] }),
      mkStats([
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:40:00Z', 'work', 'prod'),
        seg('2026-10-20T10:40:00Z', '2026-10-20T10:50:00Z', 'waiting', 'prod'),
        seg('2026-10-20T10:50:00Z', '2026-10-20T10:55:00Z', 'idle', 'prod'),
        seg('2026-10-20T10:55:00Z', '2026-10-20T11:05:00Z', 'error', 'prod'),
        seg('2026-10-20T11:05:00Z', '2026-10-20T11:15:00Z', 'notWorking', 'prod'),
      ]),
      NOW,
    );
    const prod = rowOf(model, 'prod');
    expect(prod.breakdown).toEqual({ work: 40, waiting: 10, idle: 5, error: 10, notWorking: 10 });
    // Сумма частей равна сумме минут всех main-сегментов строки.
    const b = prod.breakdown!;
    const mainMinutes = prod.segments.filter(s => s.kind !== 'review').reduce((a, s) => a + s.minutes, 0);
    expect(b.work + b.waiting + b.idle + b.error + b.notWorking).toBe(mainMinutes);
  });

  it('самый долгий — с максимальным approxMinutes; один этап — не подсвечиваем', () => {
    expect(rowOf(model, 'prod').isLongest).toBe(true);
    expect(model.rows.filter(r => r.isLongest).length).toBe(1);
    const single = buildGanttModel(
      mkRun({ stages: [mkStage('prod', 'active', 50)] }),
      mkStats([]),
      NOW,
    );
    expect(rowOf(single, 'prod').isLongest).toBe(false);
  });

  it('ось — из таймлайна; без stats — оси нет и hasData false (данных таймлайна нет)', () => {
    expect(model.axis).toEqual({ from: '2026-10-19T17:00:00Z', to: '2026-10-20T12:00:00Z' });
    const noStats = buildGanttModel(mkRun({ stages: [mkStage('prod', 'active', 10)] }), null, NOW);
    expect(noStats.axis).toBeNull();
    // hasData — та же семантика, что у Stats (сегменты или current), а не по строкам run.stages.
    expect(noStats.hasData).toBe(false);
    // Одна ось без сегментов и без current — данных нет (как blocks=[] && current=null в Stats).
    const onlyAxis = buildGanttModel(mkRun({ stages: [mkStage('prod', 'active', 10)] }), mkStats([]), NOW);
    expect(onlyAxis.hasData).toBe(false);
    // Есть current при пустых сегментах — данные есть; у закрытой фичи current не считается.
    const withCur = mkStats([]);
    withCur.timeline.current = { kind: 'waiting', since: '2026-10-20T11:00:00Z', taskId: null };
    const stages = [mkStage('prod', 'active', 10)];
    expect(buildGanttModel(mkRun({ stages }), withCur, NOW).hasData).toBe(true);
    expect(buildGanttModel(mkRun({ stages, closed: true }), withCur, NOW).hasData).toBe(false);
  });
});

// === Край полосы (W5-DEF-хвост п.1): полоса читает barEnd, не конец стадии ===

describe('край полосы: to — barEnd (отдельное поле), не lastCompletedAt', () => {
  // Мутация: читать lastCompletedAt вместо barEnd (старое поведение) — первая проверка
  // краснеет: у идущей стадии конца стадии нет, полоса бы пропала при минутах.
  it('идущая стадия: to = barEnd; закрытая: to = barEnd (совпадает с концом)', () => {
    const model = buildGanttModel(
      mkRun({
        stages: [
          mkStage('prod', 'active', 55, { firstStartedAt: '2026-10-20T10:00:00Z', tone: 'run', barEnd: '2026-10-20T11:00:00Z' }),
          mkStage('review', 'done', 40, { firstStartedAt: '2026-10-20T11:20:00Z', lastCompletedAt: '2026-10-20T12:00:00Z', barEnd: '2026-10-20T12:00:00Z' }),
        ],
      }),
      mkStats([]),
      NOW,
    );
    expect(rowOf(model, 'prod').from).toBe('2026-10-20T10:00:00Z');
    expect(rowOf(model, 'prod').to, 'полоса читает поле края, а не конец стадии').toBe('2026-10-20T11:00:00Z');
    expect(rowOf(model, 'review').to).toBe('2026-10-20T12:00:00Z');
  });
});

// === Возвраты ===

describe('возвраты: флажки на «Производстве», счётчик и честная деградация', () => {
  const mkRound = (n: number, verdict: 'returned' | 'accepted_with_remarks' | 'accepted', completedAt: string | null) => ({
    n,
    verdict,
    personaId: 'p-gleb',
    completedAt,
    reason: 'не по спецификации',
    reviewChatId: 'c-review',
  });

  it('два возврата одного шага — два флажка и счётчик 2 (тест спецификации)', () => {
    const model = buildGanttModel(
      mkRun({
        stages: [mkStage('prod', 'active', 60)],
        stepChains: [
          {
            stepRef: 'Ш3',
            title: 'Модель',
            taskIds: ['t-1', 't-2'],
            outcome: 'running',
            status: 'идёт',
            durationMinutes: 60,
            rounds: [
              mkRound(1, 'returned', '2026-10-20T09:00:00Z'),
              mkRound(2, 'accepted_with_remarks', '2026-10-20T10:00:00Z'),
              mkRound(3, 'accepted', '2026-10-20T11:00:00Z'),
              mkRound(4, 'returned', null), // незакрытый виток — не флажок
            ],
            chatId: null,
          },
        ],
      }),
      mkStats([]),
      NOW,
    );
    const prod = rowOf(model, 'prod');
    expect(prod.returns.length).toBe(2);
    expect(prod.returnCount).toBe(2);
    expect(model.returnTotal).toBe(2);
    // Порядок — по времени возврата; шаг и чат — из цепочки.
    expect(prod.returns[0]!.at).toBe('2026-10-20T09:00:00Z');
    expect(prod.returns[0]!.stepRef).toBe('Ш3');
    expect(prod.returns[0]!.reviewChatId).toBe('c-review');
  });

  it('витки стадий qa/final считаются, других стадий — нет', () => {
    const mkR = (n: number) => ({ n, verdict: 'returned' as const, completedAt: '2026-10-20T08:00:00Z', reviewChatId: null });
    const model = buildGanttModel(
      mkRun({
        stages: [
          mkStage('prod', 'active', 60),
          mkStage('qa', 'active', 20, { rounds: [mkR(1)] }),
          mkStage('review', 'active', 10, { rounds: [mkR(2)] }), // review-стадия: витки живут в цепочках
        ],
      }),
      mkStats([]),
      NOW,
    );
    expect(model.returnTotal).toBe(1);
    expect(rowOf(model, 'prod').returns[0]!.stepRef).toBeNull(); // у qa-витка шага в данных нет
  });

  it('строки «Производства» нет — флажек нет, но returnTotal для легенды остаётся', () => {
    const model = buildGanttModel(
      mkRun({
        stages: [mkStage('plan', 'done', 10)],
        stepChains: [{ stepRef: 'Ш1', title: 'x', taskIds: [], outcome: 'returned', status: 'вернул', durationMinutes: 0, rounds: [{ n: 1, verdict: 'returned', completedAt: '2026-10-20T08:00:00Z', reviewChatId: null }], chatId: null }],
      }),
      mkStats([]),
      NOW,
    );
    expect(model.returnTotal).toBe(1);
    expect(model.rows.every(r => r.returns.length === 0)).toBe(true);
  });
});

// === «Зависла» ===

describe('зависла: штриховка только при явном stopped', () => {
  const base = mkRun({
    stages: [mkStage('prod', 'active', 60, { firstStartedAt: '2026-10-20T10:00:00Z' })],
    now: [{ taskId: 't-1', stage: 'prod', stepRef: 'Ш3', title: 'Модель', startedAt: '2026-10-20T11:00:00Z', linkedSessionId: 'c-run' }],
  });

  it('attention.kind === "stopped" — хвост на строке текущего этапа (тест спецификации)', () => {
    const model = buildGanttModel(
      { ...base, attention: { kind: 'stopped', since: '2026-10-20T11:30:00Z', reason: 'нет времени', linkedSessionId: 'c-run' } },
      mkStats([]),
      NOW,
    );
    const prod = rowOf(model, 'prod');
    expect(prod.stalledSince).toBe('2026-10-20T11:30:00Z');
    expect(prod.stalledReason).toBe('нет времени');
    expect(prod.stalledActionChatId).toBe('c-run');
    expect(model.hasStalled).toBe(true);
  });

  it('attention.taskId — хвост на этапе остановившейся задачи, а не на текущей строке', () => {
    // Два now (qa новее — «текущая» строка qa), тревога указывает на задачу prod.
    const model = buildGanttModel(
      mkRun({
        stages: [
          mkStage('prod', 'active', 60, { firstStartedAt: '2026-10-20T10:00:00Z', taskIds: ['t-prod'] }),
          mkStage('qa', 'active', 20, { taskIds: ['t-qa'] }),
        ],
        now: [
          { taskId: 't-qa', stage: 'qa', title: 'Проверка', startedAt: '2026-10-20T11:40:00Z' },
          { taskId: 't-prod', stage: 'prod', title: 'Модель', startedAt: '2026-10-20T11:00:00Z' },
        ],
        attention: { kind: 'stopped', taskId: 't-prod', since: '2026-10-20T11:30:00Z', reason: 'нет времени', linkedSessionId: 'c-prod' },
      }),
      mkStats([]),
      NOW,
    );
    const prod = rowOf(model, 'prod');
    const qa = rowOf(model, 'qa');
    expect(prod.isCurrent).toBe(false);
    expect(prod.stalledSince).toBe('2026-10-20T11:30:00Z');
    expect(prod.stalledReason).toBe('нет времени');
    expect(prod.stalledActionChatId).toBe('c-prod');
    expect(qa.stalledSince).toBeNull();
    expect(model.hasStalled).toBe(true);
  });

  it('стадии с taskId нет — поиск по записи now с этим taskId', () => {
    const model = buildGanttModel(
      mkRun({
        stages: [mkStage('prod', 'active', 60), mkStage('qa', 'active', 20)],
        now: [
          { taskId: 't-qa', stage: 'qa', title: 'Проверка', startedAt: '2026-10-20T11:40:00Z' },
          { taskId: 't-prod', stage: 'prod', title: 'Модель', startedAt: '2026-10-20T11:00:00Z' },
        ],
        attention: { kind: 'stopped', taskId: 't-prod', since: '2026-10-20T11:30:00Z' },
      }),
      mkStats([]),
      NOW,
    );
    expect(rowOf(model, 'prod').stalledSince).toBe('2026-10-20T11:30:00Z');
    expect(rowOf(model, 'qa').stalledSince).toBeNull();
  });

  it('ни стадии, ни записи now с taskId — фолбэк: хвост на текущей строке', () => {
    const model = buildGanttModel(
      mkRun({
        stages: [mkStage('prod', 'active', 60, { firstStartedAt: '2026-10-20T10:00:00Z' })],
        now: [{ taskId: 't-1', stage: 'prod', title: 'Модель', startedAt: '2026-10-20T11:00:00Z' }],
        attention: { kind: 'stopped', taskId: 't-unknown', since: '2026-10-20T11:30:00Z' },
      }),
      mkStats([]),
      NOW,
    );
    expect(rowOf(model, 'prod').stalledSince).toBe('2026-10-20T11:30:00Z');
    expect(model.hasStalled).toBe(true);
  });

  it('attention.kind === "wait" — НЕ «зависла»: штриховки нет (тест спецификации)', () => {
    const model = buildGanttModel(
      { ...base, attention: { kind: 'wait', since: '2026-10-20T11:30:00Z' } },
      mkStats([]),
      NOW,
    );
    expect(rowOf(model, 'prod').stalledSince).toBeNull();
    expect(model.hasStalled).toBe(false);
  });

  it('закрытой фиче — ни «сейчас», ни «зависла»; самый долгий и флажки остаются', () => {
    const model = buildGanttModel(
      mkRun({
        closed: true,
        stages: [mkStage('prod', 'done', 60)],
        stepChains: [{ stepRef: 'Ш1', title: 'x', taskIds: [], outcome: 'accepted', status: 'принят', durationMinutes: 60, rounds: [{ n: 1, verdict: 'returned', completedAt: '2026-10-20T08:00:00Z', reviewChatId: null }], chatId: null }],
        attention: { kind: 'stopped', since: '2026-10-20T09:00:00Z' },
      }),
      mkStats([]),
      NOW,
    );
    expect(model.closed).toBe(true);
    expect(model.rows.every(r => !r.isCurrent && r.nowPill === null && r.stalledSince === null)).toBe(true);
    expect(model.returnTotal).toBe(1);
    expect(rowOf(model, 'prod').returns.length).toBe(1);
  });
});

describe('смерженная фича с открытым эпиком: «сейчас» и тревога гаснут по state', () => {
  it('merged + closed:false — ни isCurrent, ни пилюли, ни хвоста «зависла»', () => {
    const model = buildGanttModel(
      mkRun({
        state: 'merged',
        closed: false,
        stages: [mkStage('prod', 'active', 60, { tone: 'run' })],
        now: [{ taskId: 't-1', stage: 'prod', stepRef: 'Ш3', title: 'Модель', startedAt: '2026-10-20T11:30:00Z' }],
        attention: { kind: 'stopped', since: '2026-10-20T09:00:00Z' },
      }),
      mkStats([]),
      NOW,
    );
    expect(model.closed).toBe(false);
    expect(model.rows.every(r => !r.isCurrent && r.nowPill === null && r.stalledSince === null)).toBe(true);
    expect(model.hasStalled).toBe(false);
  });
});

// === Активный этап ===

describe('активный этап: пилюля «сейчас»', () => {
  it('run.now есть — точка, пилюля и длительность от startedAt', () => {
    const model = buildGanttModel(
      mkRun({
        stages: [mkStage('prod', 'active', 60, { tone: 'run' })],
        now: [{ taskId: 't-1', stage: 'prod', stepRef: 'Ш3', title: 'Модель', startedAt: '2026-10-20T11:30:00Z' }],
      }),
      mkStats([]),
      NOW,
    );
    const prod = rowOf(model, 'prod');
    expect(prod.isCurrent).toBe(true);
    expect(prod.nowPill).toEqual({
      stageKey: 'prod',
      stepRef: 'Ш3',
      title: 'Модель',
      startedAt: '2026-10-20T11:30:00Z',
      durationMinutes: 30,
    });
    // Без startedAt — только «{Ш3}»: длительности нет.
    const noStart = buildGanttModel(
      mkRun({
        stages: [mkStage('prod', 'active', 60)],
        now: [{ taskId: 't-1', stage: 'prod', title: 'x' }],
      }),
      mkStats([]),
      NOW,
    );
    expect(rowOf(noStart, 'prod').nowPill!.durationMinutes).toBeNull();
  });

  it('run.now нет (ничего не идёт) — ни точки, ни пилюли', () => {
    const model = buildGanttModel(
      mkRun({ stages: [mkStage('prod', 'done', 60)] }),
      mkStats([]),
      NOW,
    );
    expect(model.rows.every(r => !r.isCurrent && r.nowPill === null)).toBe(true);
  });
});

// === Пустая фича ===

describe('пустая фича', () => {
  it('нет этапов и нет stats — hasData false, пустое состояние вкладки', () => {
    const model = buildGanttModel(mkRun(), null, NOW);
    expect(model.hasData).toBe(false);
    expect(model.rows).toEqual([]);
    expect(model.axis).toBeNull();
    expect(model.returnTotal).toBe(0);
    expect(model.hasStalled).toBe(false);
  });
});

// === Строка «тихо» на Ганте (W5-F3b, спецификация features-overview-5.md §«Точка 6») ===
// Состояние — из общей analysisLineState: модель «тихо» сама не пересчитывает.
describe('строка «тихо»: состояние из analysisLineState', () => {
  it('«тихо»: from — lastActivityAt, минуты — до «сейчас» (12:00 19.10 → 12:00 20.10 = 1440)', () => {
    const model = buildGanttModel(
      mkRun({ quiet: true, lastActivityAt: '2026-10-19T12:00:00Z', stages: [mkStage('prod', 'done', 60)] }),
      mkStats([]),
      NOW,
    );
    expect(model.quiet).toEqual({ from: '2026-10-19T12:00:00Z', minutes: 1440 });
  });

  it('мутация: не тихая фича — строки нет (тот же run без quiet)', () => {
    const model = buildGanttModel(
      mkRun({ stages: [mkStage('prod', 'done', 60)] }),
      mkStats([]),
      NOW,
    );
    expect(model.quiet).toBeNull();
  });

  it('«stuck» побеждает «тихо»: есть staleAlertedAt — тихой строки нет, «сейчас» остаётся', () => {
    const model = buildGanttModel(
      mkRun({
        quiet: true,
        lastActivityAt: '2026-10-19T12:00:00Z',
        stages: [mkStage('prod', 'active', 60)],
        now: [{ taskId: 't-1', stage: 'prod', title: 'x', startedAt: '2026-10-20T11:00:00Z', staleAlertedAt: '2026-10-20T11:30:00Z' }],
      }),
      mkStats([]),
      NOW,
    );
    expect(model.quiet).toBeNull();
    expect(rowOf(model, 'prod').isCurrent).toBe(true);
  });

  it('в тихой — маркера «сейчас» на текущем этапе нет (текущего этапа нет)', () => {
    const model = buildGanttModel(
      mkRun({
        quiet: true,
        lastActivityAt: '2026-10-19T12:00:00Z',
        stages: [mkStage('prod', 'active', 60, { tone: 'run' })],
        now: [{ taskId: 't-1', stage: 'prod', title: 'x', startedAt: '2026-10-20T11:00:00Z' }],
      }),
      mkStats([]),
      NOW,
    );
    expect(model.quiet).not.toBeNull();
    expect(model.rows.every(r => !r.isCurrent && r.nowPill === null)).toBe(true);
  });

  it('нет lastActivityAt — ни даты, ни длительности (явный null, не выдуманное число)', () => {
    const model = buildGanttModel(
      mkRun({ quiet: true, stages: [mkStage('prod', 'done', 60)] }),
      mkStats([]),
      NOW,
    );
    expect(model.quiet).toEqual({ from: null, minutes: null });
  });

  // W5-R-F3b, п.1: длительность — один общий помощник (FeatureStatsBits.minutesSince).
  // Мутация: вернуть в модели ceil вместо общего round — краснеет: 100.4 мин —
  // round 100 («1 ч 40 мин»), ceil 101 («1 ч 41 мин») — бейдж и колонка расходятся.
  it('длительность одна: бейдж строки анализа = правая колонка Ганта (round, не ceil)', () => {
    const run = mkRun({
      quiet: true,
      // Ровно 100.4 мин до NOW: round и ceil дают разные минуты — именно тот случай,
      // где две арифметики расходятся («тихо 1 ч 40 мин» против «1 ч 41 мин»).
      lastActivityAt: new Date(NOW - 100.4 * 60000).toISOString(),
      stages: [mkStage('prod', 'done', 60)],
    });
    const model = buildGanttModel(run, mkStats([]), NOW);
    expect(model.quiet!.minutes).toBe(100);
    const state = analysisLineState(run, NOW);
    expect(state.key).toBe('quiet');
    expect(
      state.badge!.text,
      'бейдж анализа и правая колонка Ганта — одна и та же длительность',
    ).toBe('тихо ' + formatDuration(model.quiet!.minutes));
  });
});

// === Этап «пропущен» (W4-DES-B: design без задач) ===
// Мутация: не обнулять minutes/segments при skipped (брать значения бэка как есть) —
// краснеет «skipped — без минут, границ и сегментов».
describe('skipped: строка «этапа не было», без минут', () => {
  it('skipped-этап остаётся строкой, но без минут, границ, сегментов и разбивки', () => {
    const model = buildGanttModel(
      mkRun({
        stages: [
          mkStage('cut', 'done', 35, { firstStartedAt: '2026-10-19T17:00:00Z', lastCompletedAt: '2026-10-19T17:35:00Z', barEnd: '2026-10-19T17:35:00Z' }),
          // Бэк может принести approxMinutes и даже границы — модель их обнуляет явно.
          mkStage('design', 'skipped', 7, { firstStartedAt: '2026-10-19T17:36:00Z', lastCompletedAt: '2026-10-19T17:43:00Z', barEnd: '2026-10-19T17:43:00Z' }),
          mkStage('prod', 'active', 55, { firstStartedAt: '2026-10-20T10:00:00Z' }),
        ],
      }),
      mkStats([
        seg('2026-10-19T17:00:00Z', '2026-10-19T17:35:00Z', 'work', 'cut'),
        // Сегмент stage=design не должен попасть в строку skipped-этапа.
        seg('2026-10-19T17:36:00Z', '2026-10-19T17:43:00Z', 'work', 'design'),
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:55:00Z', 'work', 'prod'),
      ]),
      NOW,
    );
    const row = rowOf(model, 'design');
    expect(row.isSkipped).toBe(true);
    expect(row.minutes).toBe(0);
    expect(row.from).toBeNull();
    expect(row.to).toBeNull();
    expect(row.segments).toEqual([]);
    expect(row.breakdown).toBeNull();
    // Обычные строки при этом не затронуты.
    expect(rowOf(model, 'cut').isSkipped).toBe(false);
    expect(rowOf(model, 'cut').minutes).toBe(35);
  });
});
