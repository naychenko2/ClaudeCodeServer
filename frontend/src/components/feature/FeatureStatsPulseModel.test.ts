// Модель «Пульса» вкладки «Статистика» (ST-F3). Чистые функции — в node, без DOM.
//
// Покрытие: четыре состояния героя (running/waiting/merged/few) и приоритет
// (merged выше всех, waiting выше few); порог few — ровно FEW_DATA_MIN (30 мин —
// уже не few); состав времени: порядок строк, проценты от elapsed, null-поля не
// дают строки; очередь (Q5F): с queuedMinutes — строка «в очереди» после простого,
// без неё (null/0/нет поля) — прежнее; проценты округляются по частям — сумма может
// дать 99 или 101; плитки: 3 без spend,
// 4 со spend, токены — сумма всех четырёх видов; потери: карточка 1 из longestWait
// (live при attention.kind='wait'),
// карточка 2 — цепочка с максимальным loops (loops=0 — её нет), карточка 3 —
// longestNow, у закрытой — этап с максимальным approxMinutes; пустая модель —
// empty=true.
//
// Мутации (в отчёте): (1) «<» на «<=» в пороге few — краснеет тест «30 мин —
// уже не few»; (2) заменить `v != null && v > 0` в compositionRows на `v >= 0` —
// краснеет «null-поле — строки нет» (null >= 0 — true); просто снять `v != null`
// НЕ краснит (null > 0 — false); (3) `ch.loops > 0` без условия — краснеет
// «loops=0 — карточки нет»; (4) longestWaitMinutes в живой карточке 1 — краснеет
// тест «текущее 3 мин → 3».

import { describe, it, expect } from 'vitest';
import {
  FEW_DATA_MIN, pickHeroState, compositionRows, tilesModel, roundMarks, lossesModel, COMP_LABEL,
} from './FeatureStatsPulseModel';
import type { FeatureRun, FeatureStage, FeatureStepChain } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

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

function mkTotals(over: Partial<FeatureTimeline['totals']> = {}): FeatureTimeline['totals'] {
  return {
    elapsedMinutes: 0,
    workWallMinutes: 0,
    agentsSumMinutes: 0,
    notWorkingMinutes: 0,
    waitingMinutes: null,
    idleMinutes: null,
    waitingCount: null,
    longestWaitMinutes: null,
    errorCount: null,
    errorMinutes: null,
    ...over,
  };
}

// Сегменты одного-двух этапов для pickHeroState: «два этапа» = данные есть.
function mkTimeline(stages: string[], totals: FeatureTimeline['totals']): FeatureTimeline {
  return {
    from: '2026-10-20T10:00:00Z',
    to: '2026-10-20T16:00:00Z',
    stages: [],
    segments: stages.map((stage, i) => ({
      from: `2026-10-20T1${i}:00:00Z`,
      to: `2026-10-20T1${i}:30:00Z`,
      kind: 'work' as const,
      track: 'main' as const,
      stage,
      taskId: null,
    })),
    current: null,
    totals,
  };
}

function mkStats(over: Partial<FeatureStats> = {}, stages: string[] = ['cut', 'prod']): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: mkTimeline(stages, mkTotals({ elapsedMinutes: 400, workWallMinutes: 250 })),
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

const ATTENTION = { kind: 'wait' as const, since: '2026-10-20T15:00:00Z', reason: 'Вопрос', personaId: null, sessionId: null, taskId: null };
// «Сейчас» для живой карточки: NOW — на 3 минуты позже attention.since.
const NOW = Date.parse('2026-10-20T15:03:00Z');

describe('FEW_DATA_MIN', () => {
  it('экспортирован и равен 30', () => {
    expect(FEW_DATA_MIN).toBe(30);
  });
});

describe('pickHeroState', () => {
  it('running — идёт, данные есть', () => {
    expect(pickHeroState(mkRun(), mkStats())).toBe('running');
  });

  it('waiting — attention.kind=wait', () => {
    expect(pickHeroState(mkRun({ attention: ATTENTION }), mkStats())).toBe('waiting');
  });

  it('merged — state merged или closed', () => {
    expect(pickHeroState(mkRun({ state: 'merged' }), mkStats())).toBe('merged');
    expect(pickHeroState(mkRun({ state: 'closed' }), mkStats())).toBe('merged');
  });

  it('few — сегменты только у одного этапа', () => {
    expect(pickHeroState(mkRun(), mkStats({}, ['cut']))).toBe('few');
  });

  it('few — нет ни одного сегмента', () => {
    expect(pickHeroState(mkRun(), mkStats({}, []))).toBe('few');
  });

  it('few — прошло меньше FEW_DATA_MIN', () => {
    const stats = mkStats({ timeline: mkTimeline(['cut', 'prod'], mkTotals({ elapsedMinutes: 29, workWallMinutes: 29 })) });
    expect(pickHeroState(mkRun(), stats)).toBe('few');
  });

  it('граница: ровно 30 минут — уже не few', () => {
    const stats = mkStats({ timeline: mkTimeline(['cut', 'prod'], mkTotals({ elapsedMinutes: 30, workWallMinutes: 30 })) });
    expect(pickHeroState(mkRun(), stats)).toBe('running');
  });

  it('приоритет: merged выше few и waiting', () => {
    const stats = mkStats({}, ['cut']);
    expect(pickHeroState(mkRun({ state: 'merged', attention: ATTENTION }), stats)).toBe('merged');
  });

  it('приоритет: waiting выше few — действие читателя важнее', () => {
    expect(pickHeroState(mkRun({ attention: ATTENTION }), mkStats({}, ['cut']))).toBe('waiting');
  });

  it('attention.kind=merge — не ожидание человека: running', () => {
    expect(pickHeroState(mkRun({ attention: { ...ATTENTION, kind: 'merge' } }), mkStats())).toBe('running');
  });
});

describe('compositionRows', () => {
  it('фикстура постановки: 4 строки с минутами и процентами 63/16/18/4', () => {
    const r = compositionRows(mkTotals({
      elapsedMinutes: 400, workWallMinutes: 250, waitingMinutes: 65, idleMinutes: 70, errorMinutes: 15,
    }));
    expect(r.length).toBe(4);
    expect(r.map(x => x.minutes)).toEqual([250, 65, 70, 15]);
    expect(r.map(x => x.percent)).toEqual([63, 16, 18, 4]);
    expect(r.map(x => x.percentText)).toEqual(['63 %', '16 %', '18 %', '4 %']);
    expect(r.map(x => x.label)).toEqual(['работа', 'ждали вас', 'простой', 'ошибки']);
    expect(r.map(x => x.kind)).toEqual(['work', 'waiting', 'idle', 'error']);
  });

  it('поле null — строки нет', () => {
    const r = compositionRows(mkTotals({ elapsedMinutes: 400, workWallMinutes: 250 }));
    expect(r.length).toBe(1);
    expect(r[0]!.kind).toBe('work');
  });

  it('ноль — строки нет', () => {
    const r = compositionRows(mkTotals({ elapsedMinutes: 400, workWallMinutes: 0, waitingMinutes: 0 }));
    expect(r.length).toBe(0);
  });

  it('elapsed 0 — пустой список', () => {
    expect(compositionRows(mkTotals({ workWallMinutes: 10 })).length).toBe(0);
  });

  // Q5F: очередь — самостоятельный пятый вид разряды (R-Q3B-2, коммит 6c659e527, ADR-031
  // §«Время в очереди (queued)»): строка после простого; сумма видов = elapsed (контракт
  // TimelineTotals) — минуты, не проценты.
  it('очередь есть: строка «в очереди» после простого', () => {
    const r = compositionRows(mkTotals({
      elapsedMinutes: 400, workWallMinutes: 240, waitingMinutes: 60, idleMinutes: 60, errorMinutes: 20, queuedMinutes: 20,
    }));
    expect(r.map(x => x.kind)).toEqual(['work', 'waiting', 'idle', 'queued', 'error']);
    // Метка очереди — из источника, не литералом: смена слова в COMP_LABEL меняет ожидание
    // здесь и в тестах «Подробнее»/мини-сводки (они привязаны к тому же COMP_LABEL, Q5H-3).
    expect(r.map(x => x.label)).toEqual(['работа', 'ждали вас', 'простой', COMP_LABEL.queued, 'ошибки']);
    expect(r.map(x => x.minutes)).toEqual([240, 60, 60, 20, 20]);
    expect(r.map(x => x.percent)).toEqual([60, 15, 15, 5, 5]);
    expect(r.map(x => x.percentText)).toEqual(['60 %', '15 %', '15 %', '5 %', '5 %']);
  });

  // Q5H-3 (R-Q5H-1, находка 1): слово вида «очередь» — единый источник для всей статистики.
  // Значение закреплено здесь (лексика согласована: ADR-031, макет feature-stats-5) — смена
  // слова в COMP_LABEL красит этот тест, т. е. становится осознанным решением; при этом
  // все места меняются вместе: состав героя (ниже), «Подробнее» (FeatureStatsTab.test) и
  // мини-сводка (FeatureMiniSummary.test) сравнивают с тем же COMP_LABEL, а не с литералом.
  it('сторож: слово очереди — единый источник, значение закреплено (Q5H-3)', () => {
    expect(COMP_LABEL.queued).toBe('в очереди');
    // Строка состава героя строится из него же (потребители в FeatureStatsTab/MiniSummary —
    // в своих тестах, привязанных к той же константе).
    const r = compositionRows(mkTotals({ elapsedMinutes: 100, queuedMinutes: 100 }));
    expect(r.map(x => x.label)).toEqual([COMP_LABEL.queued]);
  });

  // Округление по частям — поведение старое (не регресс Q5F): сумма процентов может дать
  // 101 % (17+17+33+17+17); ширина сегментов в StatsHero мерится минутами, не процентами.
  it('проценты округляются по частям: сумма 101 % допустима', () => {
    const r = compositionRows(mkTotals({
      elapsedMinutes: 6, workWallMinutes: 1, waitingMinutes: 1, idleMinutes: 2, errorMinutes: 1, queuedMinutes: 1,
    }));
    expect(r.map(x => x.percent)).toEqual([17, 17, 33, 17, 17]);
    expect(r.reduce((a, x) => a + x.percent, 0)).toBe(101);
  });

  it('очереди нет (null / 0 / поля нет): строки и проценты — прежние', () => {
    const base = { elapsedMinutes: 400, workWallMinutes: 240, waitingMinutes: 60, idleMinutes: 60, errorMinutes: 20 };
    for (const over of [{ queuedMinutes: null }, { queuedMinutes: 0 }, {}]) {
      const r = compositionRows(mkTotals({ ...base, ...over }));
      expect(r.map(x => x.kind)).toEqual(['work', 'waiting', 'idle', 'error']);
      expect(r.map(x => x.percent)).toEqual([60, 15, 15, 5]);
    }
  });
});

describe('tilesModel', () => {
  it('без spend — три плитки: работа, ожидание, возвраты', () => {
    const tiles = tilesModel(mkRun(), mkStats());
    expect(tiles.length).toBe(3);
    expect(tiles.map(t => t.key)).toEqual(['work', 'waiting', 'loops']);
    expect(tiles.map(t => t.label)).toEqual(['Работали', 'Ждали вас', 'Возвраты']);
  });

  it('со spend — четыре, последняя «Расход» с суммой токенов', () => {
    const stats = mkStats({
      spend: {
        inputTokens: 1000, outputTokens: 500, cacheReadTokens: 100, cacheCreationTokens: 50,
        costUsd: 21.8, costPartial: false, hiddenCostModels: [], from: '2026-10-20T10:00:00Z', shared: null,
      },
    });
    const tiles = tilesModel(mkRun(), stats);
    expect(tiles.length).toBe(4);
    const spend = tiles[3]!;
    expect(spend.key).toBe('spend');
    expect(spend.label).toBe('Расход');
    if (spend.key === 'spend') {
      expect(spend.costUsd).toBe(21.8);
      expect(spend.tokens).toBe(1650);
    }
  });

  it('ряд витков: returned — ↩, принятые — ✓; withdrawn и running не показываются', () => {
    const run = mkRun({
      verdicts: { accepted: 1, acceptedWithRemarks: 1, returned: 2, withdrawn: 1, loops: 2 },
      stepChains: [{
        stepRef: 'Ш1', title: 'Шаг', taskIds: [], outcome: 'accepted', status: '',
        durationMinutes: 10, loops: 2,
        rounds: [
          { n: 1, verdict: 'returned', reviewChatId: null },
          { n: 2, verdict: 'accepted_with_remarks', reviewChatId: null },
          { n: 3, verdict: 'withdrawn', reviewChatId: null },
          { n: 4, verdict: 'running', reviewChatId: null },
        ],
        chatId: null,
      }],
    });
    const tiles = tilesModel(run, mkStats());
    const loops = tiles[2]!;
    if (loops.key === 'loops') {
      expect(loops.rounds).toEqual(['ret', 'ok']);
    }
  });
});

describe('roundMarks', () => {
  it('пустые цепочки — пустой ряд', () => {
    expect(roundMarks(undefined)).toEqual([]);
    expect(roundMarks([])).toEqual([]);
  });
});

function mkChain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'Ш1', title: 'Шаг', taskIds: [], outcome: 'running', status: '',
    durationMinutes: 10, loops: 0, rounds: [], chatId: null, ...over,
  };
}

function mkStage(over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key: 'prod', state: 'done', passes: 1, taskIds: [], rounds: [],
    firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0,
    overlapsActive: false, executors: [], ...over,
  };
}

describe('lossesModel', () => {
  it('полная фикстура: три карточки, первая живая при ожидании', () => {
    const run = mkRun({
      attention: ATTENTION,
      stepChains: [mkChain({ stepRef: 'Ш1', loops: 1 }), mkChain({ stepRef: 'Ш4', loops: 2, title: 'Кнопка' })],
    });
    const stats = mkStats({
      timeline: mkTimeline(['cut', 'prod'], mkTotals({
        elapsedMinutes: 400, workWallMinutes: 250, waitingMinutes: 65, waitingCount: 3, longestWaitMinutes: 42,
      })),
      longestNow: { taskId: 't-9', chatId: 'c-1', personaId: 'p-1', stepRef: 'Ш3', minutes: 55, tokens: null, share: 0.3 },
    });
    const m = lossesModel(run, stats, NOW);
    expect(m.empty).toBe(false);
    expect(m.cards.map(c => c.no)).toEqual([1, 2, 3]);
    const c1 = m.cards[0]!;
    if (c1.no !== 1) throw new Error('карточка 1 отсутствует');
    expect(c1.live).toBe(true);
    expect(c1.title).toBe('Ждёт вас сейчас');
    // Живая карточка — текущее ожидание (NOW − since = 3 мин), не прошлый максимум 42.
    expect(c1.minutes).toBe(3);
    const c2 = m.cards[1]!;
    if (c2.no !== 2) throw new Error('карточка 2 отсутствует');
    expect(c2.stepRef).toBe('Ш4');
    expect(c2.loops).toBe(2);
    const c3 = m.cards[2]!;
    if (c3.no !== 3 || c3.kind !== 'longest') throw new Error('карточка 3 отсутствует');
    expect(c3.title).toBe('Дольше всех сейчас');
    expect(c3.minutes).toBe(55);
  });

  it('не живая: longestWait есть, ожидания нет — live=false, подпись прошедшая', () => {
    const stats = mkStats({
      timeline: mkTimeline(['cut', 'prod'], mkTotals({ elapsedMinutes: 400, longestWaitMinutes: 42, waitingCount: 2 })),
    });
    const m = lossesModel(mkRun(), stats, NOW);
    const c1 = m.cards[0]!;
    if (c1.no !== 1) throw new Error('карточка 1 отсутствует');
    expect(c1.live).toBe(false);
    expect(c1.title).toBe('Дольше всего ждали вас');
    expect(c1.minutes).toBe(42);
  });

  it('живая карточка: текущее ожидание 3 мин, прошлый максимум 120 — показываем 3', () => {
    const run = mkRun({ attention: ATTENTION });
    const stats = mkStats({
      timeline: mkTimeline(['cut', 'prod'], mkTotals({ elapsedMinutes: 400, longestWaitMinutes: 120, waitingCount: 3 })),
    });
    const m = lossesModel(run, stats, NOW);
    const c1 = m.cards[0]!;
    if (c1.no !== 1) throw new Error('карточка 1 отсутствует');
    expect(c1.minutes).toBe(3);
  });

  it('loops=0 — карточки 2 нет', () => {
    const run = mkRun({ stepChains: [mkChain({ loops: 0 })] });
    const m = lossesModel(run, mkStats(), NOW);
    expect(m.cards.some(c => c.no === 2)).toBe(false);
  });

  it('берётся цепочка с максимальным loops, не первая', () => {
    const run = mkRun({ stepChains: [mkChain({ stepRef: 'Ш1', loops: 1 }), mkChain({ stepRef: 'Ш2', loops: 3 })] });
    const m = lossesModel(run, mkStats(), NOW);
    const c2 = m.cards.find(c => c.no === 2)!;
    if (c2.no !== 2) throw new Error('карточка 2 отсутствует');
    expect(c2.stepRef).toBe('Ш2');
  });

  it('закрытая фича без longestNow — карточка 3 из этапа с максимальным approxMinutes', () => {
    const run = mkRun({
      state: 'merged',
      closed: true,
      stages: [mkStage({ key: 'cut', approxMinutes: 18 }), mkStage({ key: 'prod', approxMinutes: 210 }), mkStage({ key: 'qa', approxMinutes: 40 })],
    });
    const m = lossesModel(run, mkStats(), NOW);
    const c3 = m.cards.find(c => c.no === 3)!;
    if (c3.no !== 3 || c3.kind !== 'stage') throw new Error('карточка 3 отсутствует');
    expect(c3.title).toBe('Самый долгий этап');
    expect(c3.stageKey).toBe('prod');
    expect(c3.stageLabel).toBe('Производство');
    expect(c3.minutes).toBe(210);
  });

  it('longestNow важнее этапа даже у закрытой', () => {
    const run = mkRun({ state: 'merged', closed: true, stages: [mkStage({ key: 'prod', approxMinutes: 210 })] });
    const stats = mkStats({ longestNow: { taskId: 't-1', chatId: null, personaId: null, stepRef: null, minutes: 30, tokens: null, share: null } });
    const m = lossesModel(run, stats, NOW);
    const c3 = m.cards.find(c => c.no === 3)!;
    if (c3.no !== 3 || c3.kind !== 'longest') throw new Error('карточка 3 отсутствует');
  });

  it('нечего показать — empty=true', () => {
    const m = lossesModel(mkRun(), mkStats(), NOW);
    expect(m.empty).toBe(true);
    expect(m.cards.length).toBe(0);
  });

  it('закрытая фича без этапов — карточки 3 нет', () => {
    const run = mkRun({ state: 'closed', closed: true, stages: [] });
    const m = lossesModel(run, mkStats(), NOW);
    expect(m.empty).toBe(true);
  });
});
