// Тесты секции «Объём» вкладки «Разбор» (FS-FR2 + FS-FRv): строки stepProgress (7 числовых +
// «Состояние плана») и строки плана («Исходный план», «Расширение», «Доработки после мержа») —
// только у открытой, строки по линиям, медиана шага по проекту, медиана шага этой фичи и
// смены знаменателя, итоги закрытой — только у закрытой. Порядок пар «ключ — значение»
// проверяется
// целиком по html: (у открытой) шаги → план → линии → медиана по проекту → медиана фичи →
// смены знаменателя; (у закрытой) линии → медианы → итоги. Окружение node: renderToStaticMarkup.
//
// Мутации (краснеющие тесты — по фактическим прогонам в копии дерева, 10.11):
// (1) `if (sp && !closed)` → `if (sp)` (группа шагов рисуется и у закрытой) — краснеет ровно
//     «закрытая со stepProgress: группы шагов нет — только строки итогов»;
// (2) из признака закрытой убран `closed === true` — краснеет ровно «closed: true при
//     незакрытом state — тоже закрытая: итоги есть»;
// (3) `sp.inMaster ?? null` → `?? 0` (строка появляется без поля) — краснеет ровно
//     «inMaster: поля нет — строки нет»;
// (4) строка «Состояние плана» убрана из строк — краснеют «полные пары stepProgress…»,
//     «словарь «Состояния плана»» и «2 линии → 2 строки…» (тот ищет индекс этой строки через
//     findIndex и требует его ≥ 0).
// Прежняя здесь формулировка «признак закрытой → всегда true — краснеет ровно один тест»
// была неверна: проба Глеба (задача edff6e94) — при всегда-true краснеют четыре теста.
// После отката каждой мутации — зелёные.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReviewVolume } from './FeatureReviewVolume';
import { formatDuration } from './FeatureStatsBits';
import type { StepProgress } from '../../lib/featureProgress';
import type { FeatureRun, FeatureTask, FeatureMergeMark } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline, TypicalPercentiles, FeatureEstimateDrift } from '../../types/featureStats';

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

function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: { from: '2026-10-20T10:00:00Z', to: '2026-10-20T16:00:00Z', stages: [], segments: [], current: null, totals: mkTotals() },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

function mkProgress(over: Partial<StepProgress> = {}): StepProgress {
  return {
    planState: 'partial', total: 44, accepted: 30, review: 3, work: 2, notCreated: 6,
    withdrawn: 3, unlinkedReworks: 0, inMaster: 20, ...over,
  };
}

function mkTypical(over: Partial<TypicalPercentiles> = {}): TypicalPercentiles {
  return { median: 70, p25: 40, p75: 90, ...over };
}

function mkDrift(over: Partial<FeatureEstimateDrift> = {}): FeatureEstimateDrift {
  return {
    initialEtaMinutes: 60, firstEtaMinutes: 60, currentEtaMinutes: 90, shiftMinutes: 30,
    denominatorChanges: 2, accuracy: null, ...over,
  };
}

const html = (run: FeatureRun, stats: FeatureStats = mkStats()): string =>
  renderToStaticMarkup(createElement(FeatureReviewVolume, { run, stats }));

// Пары «ключ — значение» всех строк ReviewKv в порядке html (строки с value=null
// в html отсутствуют — ReviewKv их не рендерит).
function kvPairs(h: string): [string, string][] {
  return [...h.matchAll(/data-testid="review-kv-row"[^>]*><span[^>]*>([^<]*)<\/span><span[^>]*>([^<]*)<\/span>/g)]
    .map((m) => [m[1], m[2]] as [string, string]);
}

// Фикстура c1: stepProgress {total 44, accepted 30, review 3, work 2, notCreated 6,
// withdrawn 3, inMaster 20} + lanes из 2 элементов.
const C1_RUN = mkRun({ stepProgress: mkProgress(), lanes: [
  { lane: 'back', personaIds: ['p-1'], total: 24, accepted: 3, returns: 1 },
  { lane: 'front', personaIds: ['p-2'], total: 17, accepted: 3, returns: 0 },
] });

describe('шаги (c1)', () => {
  it('полные пары stepProgress: 7 числовых строк + «Состояние плана», в порядке', () => {
    // Без линий и медианы: ровно 8 строк — 7 числовых + состояние плана.
    expect(kvPairs(html(mkRun({ stepProgress: mkProgress() })))).toEqual([
      ['Всего шагов', '44'],
      ['Приняты', '30'],
      ['На ревью', '3'],
      ['В работе', '2'],
      ['Не заведены', '6'],
      ['Сняты', '3'],
      ['Уже в master', '20'],
      ['Состояние плана', 'частично'],
    ]);
  });

  it('нулевые корзины рисуются (0 — значение, не «нет данных»)', () => {
    const h = html(mkRun({ stepProgress: mkProgress({ withdrawn: 0 }) }));
    expect(kvPairs(h)).toContainEqual(['Сняты', '0']);
  });

  it('inMaster: поля нет — строки нет (аддитивное поле)', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ inMaster: undefined }) })));
    expect(pairs.some((p) => p[0] === 'Уже в master')).toBe(false);
  });

  it('stepProgress нет (старый ответ) — строк шагов нет', () => {
    const h = html(mkRun());
    expect(h).not.toContain('Всего шагов');
    expect(kvPairs(h).length).toBe(0);
  });
});

// Фикстура c1: план вырос с 12 до 15, после мержа 1 доработка из 3 принята.
const PLAN_GROWN = mkProgress({
  total: 15, accepted: 10, review: 2, work: 2, notCreated: 1,
  baselineTotal: 12, followUpTotal: 3, followUpAccepted: 1,
});

describe('план и доработки (c1)', () => {
  it('рост знаменателя и доработки: «Исходный план» 12 шагов, «Расширение» +3, «Доработки» 1 из 3 — после строк шагов', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: PLAN_GROWN })));
    expect(pairs).toEqual([
      ['Всего шагов', '15'],
      ['Приняты', '10'],
      ['На ревью', '2'],
      ['В работе', '2'],
      ['Не заведены', '1'],
      ['Сняты', '3'],
      ['Уже в master', '20'],
      ['Состояние плана', 'частично'],
      ['Исходный план', '12 шагов'],
      ['Расширение', '+3'],
      ['Доработки после мержа', '1 из 3'],
    ]);
  });

  it('baselineTotal null или нет (не зафиксирована, старый ответ) и followUpTotal 0 — этих строк нет', () => {
    for (const p of [
      mkProgress({ total: 15, accepted: 10, review: 2, work: 2, notCreated: 1, baselineTotal: null, followUpTotal: 0 }),
      mkProgress({ total: 15, accepted: 10, review: 2, work: 2, notCreated: 1 }),
    ]) {
      const pairs = kvPairs(html(mkRun({ stepProgress: p })));
      for (const label of ['Исходный план', 'Расширение', 'Доработки после мержа']) {
        expect(pairs.some((r) => r[0] === label)).toBe(false);
      }
    }
  });

  it('знаменатель не рос (total == baselineTotal) — «Расширения» нет, «Исходный план» есть', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ baselineTotal: 44, followUpTotal: 0 }) })));
    expect(pairs).toContainEqual(['Исходный план', '44 шага']);
    expect(pairs.some((r) => r[0] === 'Расширение')).toBe(false);
  });

  it('baselineTotal больше total (знаменатель сжимался) — «Расширения» нет (никакого «−N» и «+0»)', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ total: 10, accepted: 6, review: 1, work: 1, notCreated: 2, baselineTotal: 12 }) })));
    expect(pairs.some((r) => r[0] === 'Расширение')).toBe(false);
  });

  it('доработки без принятого счётчика: followUpAccepted нет → «0 из N» (0 — значение)', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ followUpTotal: 2 }) })));
    expect(pairs).toContainEqual(['Доработки после мержа', '0 из 2']);
  });

  it('закрытая с полными полями плана — строк плана нет (группа stepProgress не рисуется вовсе)', () => {
    const pairs = kvPairs(html(mkRun({ state: 'closed', stepProgress: PLAN_GROWN })));
    for (const label of ['Исходный план', 'Расширение', 'Доработки после мержа', 'Всего шагов']) {
      expect(pairs.some((r) => r[0] === label)).toBe(false);
    }
  });
});

describe('словарь «Состояния плана»', () => {
  it('pending/partial/cut/done — существующие слова формата (featureProgress.ts:71-72, макет :486)', () => {
    for (const [state, label] of [
      ['pending', 'не разобран'], ['partial', 'частично'], ['cut', 'разрезан'], ['done', 'готово'],
    ] as const) {
      expect(kvPairs(html(mkRun({ stepProgress: mkProgress({ planState: state }) })))).toContainEqual(['Состояние плана', label]);
    }
  });
});

describe('линии', () => {
  it('2 линии → 2 строки «{линия}: N шагов · принято A · возвратов R», после строк шагов', () => {
    const pairs = kvPairs(html(C1_RUN));
    // Склонение по plural: 24 → «шага», 17 → «шагов».
    expect(pairs).toContainEqual(['бэк', '24 шага · принято 3 · возвратов 1']);
    expect(pairs).toContainEqual(['фронт', '17 шагов · принято 3 · возвратов 0']);
    const iSteps = pairs.findIndex((p) => p[0] === 'Состояние плана');
    const iBack = pairs.findIndex((p) => p[0] === 'бэк');
    const iFront = pairs.findIndex((p) => p[0] === 'фронт');
    expect(iSteps).toBeGreaterThanOrEqual(0);
    expect(iSteps).toBeLessThan(iBack);
    expect(iBack).toBeLessThan(iFront);
  });

  it('прочая линия и русские значения линии — та же подпись, что в существующем коде', () => {
    const pairs = kvPairs(html(mkRun({ lanes: [
      { lane: 'other', personaIds: [], total: 3, accepted: 2, returns: 0 },
      { lane: 'бэк', personaIds: [], total: 1, accepted: 1, returns: 0 },
    ] })));
    expect(pairs).toContainEqual(['прочее', '3 шага · принято 2 · возвратов 0']);
    expect(pairs).toContainEqual(['бэк', '1 шаг · принято 1 · возвратов 0']);
  });

  it('lines нет (старый ответ) — строк линий нет', () => {
    expect(kvPairs(html(mkRun({ stepProgress: mkProgress() }))).some((p) => p[0] === 'бэк')).toBe(false);
  });
});

describe('медиана шага по проекту', () => {
  it('stats.typical.stepMinutes.median — через formatDuration, после линий', () => {
    const pairs = kvPairs(html(C1_RUN, mkStats({ typical: { count: 5, waitingCount: 0, duration: mkTypical(), waiting: null, loops: mkTypical(), stepCount: 4, stepMinutes: mkTypical() } })));
    expect(pairs).toContainEqual(['Медиана шага по проекту', formatDuration(70)]); // 1 ч 10 мин
    const iFront = pairs.findIndex((p) => p[0] === 'фронт');
    const iMed = pairs.findIndex((p) => p[0] === 'Медиана шага по проекту');
    expect(iFront).toBeLessThan(iMed);
  });

  it('typical нет или stepMinutes null (фич с шагами <3) — строки нет', () => {
    expect(html(mkRun(), mkStats())).not.toContain('Медиана шага по проекту');
    const noSteps = mkStats({ typical: { count: 5, waitingCount: 0, duration: mkTypical(), waiting: null, loops: mkTypical(), stepCount: 0, stepMinutes: null } });
    expect(html(mkRun(), noSteps)).not.toContain('Медиана шага по проекту');
  });
});

const TYPICAL_STATS: FeatureStats = mkStats({ typical: { count: 5, waitingCount: 0, duration: mkTypical(), waiting: null, loops: mkTypical(), stepCount: 4, stepMinutes: mkTypical() } });

describe('медиана этой фичи и смены знаменателя (c2)', () => {
  it('stepMedianMinutes и estimateDrift: строки через formatDuration и числом — после медианы по проекту', () => {
    const pairs = kvPairs(html(C1_RUN, mkStats({ typical: TYPICAL_STATS.typical, stepMedianMinutes: 90, estimateDrift: mkDrift() })));
    expect(pairs).toContainEqual(['Медиана шага этой фичи', formatDuration(90)]); // 1 ч 30 мин
    expect(pairs).toContainEqual(['Смены знаменателя', '2']);
    const iProject = pairs.findIndex((p) => p[0] === 'Медиана шага по проекту');
    const iFeature = pairs.findIndex((p) => p[0] === 'Медиана шага этой фичи');
    const iDrift = pairs.findIndex((p) => p[0] === 'Смены знаменателя');
    expect(iProject).toBeGreaterThanOrEqual(0);
    expect(iProject).toBeLessThan(iFeature);
    expect(iFeature).toBeLessThan(iDrift);
  });

  it('полей нет (старый ответ) или stepMedianMinutes null (принятых цепочек нет) — этих строк нет', () => {
    const h = html(C1_RUN, TYPICAL_STATS);
    expect(h).not.toContain('Медиана шага этой фичи');
    expect(h).not.toContain('Смены знаменателя');
    expect(html(C1_RUN, mkStats({ stepMedianMinutes: null, estimateDrift: null }))).not.toContain('Смены знаменателя');
  });

  it('estimateDrift есть и denominatorChanges 0 — строка есть со значением «0» (не менялся — факт)', () => {
    const pairs = kvPairs(html(C1_RUN, mkStats({ estimateDrift: mkDrift({ denominatorChanges: 0 }) })));
    expect(pairs).toContainEqual(['Смены знаменателя', '0']);
  });

  it('закрытая: медианы и смены знаменателя — перед итогами закрытой', () => {
    const closedRun = mkRun({
      state: 'closed',
      summary: { mergedAt: '2026-10-30T12:00:00Z', durationMinutes: 200, tasks: 21, steps: 12, loops: 1 },
    });
    const pairs = kvPairs(html(closedRun, mkStats({ typical: TYPICAL_STATS.typical, stepMedianMinutes: 90, estimateDrift: mkDrift() })));
    const iDrift = pairs.findIndex((p) => p[0] === 'Смены знаменателя');
    const iSteps = pairs.findIndex((p) => p[0] === 'Шагов');
    expect(iDrift).toBeGreaterThanOrEqual(0);
    expect(iDrift).toBeLessThan(iSteps);
  });
});

describe('итоги закрытой (c2)', () => {
  const CLOSED_RUN = mkRun({
    state: 'closed',
    closed: true,
    summary: { mergedAt: '2026-10-30T12:00:00Z', durationMinutes: 200, tasks: 21, steps: 12, loops: 1 },
    reviews: [1, 2, 3].map((n): FeatureTask => ({ id: `r-${n}`, title: `ревью ${n}` })),
    merges: [
      { taskId: 'm-1', at: '2026-10-28T10:00:00Z', stepRefs: ['Ш1'] },
      { taskId: 'm-2', at: '2026-10-30T12:00:00Z', stepRefs: ['Ш2'] },
    ] as FeatureMergeMark[],
    postMergeTails: ['t-1', 't-2'],
  });
  // Та же закрытая, но с stepProgress (фича, у которого был план): группа «Всего шагов/Приняты…»
  // не рисуется — настоящий объём закрытой дают строки итогов (макет ov.vol :513).
  const CLOSED_SP_RUN: FeatureRun = { ...CLOSED_RUN, stepProgress: mkProgress() };

  it('закрытая: «Шагов», «Задач всего», «Пачек ревью», «Слияний», «Хвостов» — после медианы', () => {
    const pairs = kvPairs(html(CLOSED_RUN, mkStats({ typical: { count: 5, waitingCount: 0, duration: mkTypical(), waiting: null, loops: mkTypical(), stepCount: 4, stepMinutes: mkTypical() } })));
    expect(pairs).toContainEqual(['Шагов', '12']);
    expect(pairs).toContainEqual(['Задач всего', '21']);
    expect(pairs).toContainEqual(['Пачек ревью', '3']);
    expect(pairs).toContainEqual(['Слияний', '2']);
    expect(pairs).toContainEqual(['Хвостов после мержа', '2']);
    const iMed = pairs.findIndex((p) => p[0] === 'Медиана шага по проекту');
    const iTail = pairs.findIndex((p) => p[0] === 'Хвостов после мержа');
    expect(iMed).toBeGreaterThanOrEqual(0);
    expect(iMed).toBeLessThan(iTail);
  });

  it('«Открытых шагов» нет: поля summary.stepsOpen в контракте нет', () => {
    expect(html(CLOSED_RUN)).not.toContain('Открытых шагов');
  });

  it('закрытая со stepProgress: группы шагов нет — только строки итогов', () => {
    // Ни одной строки группы stepProgress (решение постановщика: рядом «Всего шагов 44» и
    // «Шагов 12» — два разных числа), и весь список строк закрытой — ровно строки итогов.
    const pairs = kvPairs(html(CLOSED_SP_RUN));
    expect(pairs).toEqual([
      ['Шагов', '12'],
      ['Задач всего', '21'],
      ['Пачек ревью', '3'],
      ['Слияний', '2'],
      ['Хвостов после мержа', '2'],
    ]);
    for (const label of ['Всего шагов', 'Приняты', 'На ревью', 'В работе', 'Не заведены', 'Сняты', 'Уже в master', 'Состояние плана']) {
      expect(pairs.some((p) => p[0] === label)).toBe(false);
    }
  });

  it('открытая — строк итогов нет (0 строк)', () => {
    const h = html(mkRun({ reviews: [{ id: 'r-1', title: 'ревью' }] }));
    expect(h).not.toContain('Слияний');
    expect(h).not.toContain('Хвостов после мержа');
    expect(h).not.toContain('Пачек ревью');
  });

  it('признак закрытости — closed или state merged/closed', () => {
    const byState = html(mkRun({ state: 'merged' }));
    expect(byState).toContain('Слияний');
    expect(byState).toContain('Хвостов после мержа');
    // closed:false при state running — не закрытая.
    expect(html(mkRun({ closed: false }))).not.toContain('Слияний');
  });

  it('closed: true при незакрытом state — тоже закрытая: итоги есть', () => {
    // Отдельный случай на `closed === true` в признаке (прежний тест проверял только
    // state merged/closed: ветка closed === true оставалась непроверенной).
    const pairs = kvPairs(html(mkRun({ closed: true })));
    expect(pairs).toEqual([
      ['Пачек ревью', '0'],
      ['Слияний', '0'],
      ['Хвостов после мержа', '0'],
    ]);
  });

  it('закрытая без summary/merges/tails (старый ответ): итогов по summary нет, счётчики — 0', () => {
    const pairs = kvPairs(html(mkRun({ state: 'closed' })));
    expect(pairs.some((p) => p[0] === 'Шагов')).toBe(false);
    expect(pairs.some((p) => p[0] === 'Задач всего')).toBe(false);
    expect(pairs).toContainEqual(['Пачек ревью', '0']);
    expect(pairs).toContainEqual(['Слияний', '0']);
    expect(pairs).toContainEqual(['Хвостов после мержа', '0']);
  });
});

describe('карточку «Объём» не повторяю', () => {
  it('в html нет числа «N из M» и полосы корзин FeatureVolumeCard', () => {
    const h = html(C1_RUN);
    expect(h).not.toContain('30 из 44');
    expect(h).not.toContain('volume-card');
    expect(h).not.toContain('volume-seg');
  });
});
