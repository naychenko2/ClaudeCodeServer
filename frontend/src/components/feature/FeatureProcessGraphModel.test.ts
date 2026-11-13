// Модель графа процесса фичи (C3a, разрез 2 §1.7, §1.9). Чистые функции — в node, без DOM.
//
// Покрытие:
// - graphColumns: колонки строго по полю column (не по индексу списка), порядок внутри
//   колонки — порядок run.stages; отсутствующее column (старый ответ) = 0, как бэк для легаси;
//   легаси (все column 0) — одна колонка; отсутствующее stages — пусто;
// - graphEdges: ровно по элементам after (parallel-cycle — 10), порядок обхода run.stages;
//   ссылка на отсутствующую стадию ребра не даёт; легаси (after пусто) — без рёбер;
// - defaultSelected: первая active по порядку run.stages (не по колонкам), иначе последняя
//   done, иначе null;
// - stageNodeId: id с бэка; отсутствие (старый ответ) — ключ, как бэк для легаси.
//
// Мутации (в отчёте): (1) graphColumns ставит колонку по индексу в списке, а не по column →
// краснеет «parallel-cycle: колонки по полю column»; (2) defaultSelected берёт последнюю
// active, а не первую → краснеет «две active: первая по порядку run.stages»; (3) не фильтровать
// висящие after → краснеет «ссылка на отсутствующую стадию»; (4) сжимать значения column до
// плотного ряда 0..k → краснеет «значения column не перенумеровываются».

import { describe, it, expect } from 'vitest';
import { graphColumns, graphEdges, defaultSelected, stageNodeId } from './FeatureProcessGraphModel';
import type { FeatureRun, FeatureStage, StageKey, StageState } from '../../types/featureRun';

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

// Стадия графа: id/role/column/after как их отдаёт бэк (FeatureRun.cs:273-292).
function mkStage(key: StageKey, id: string, role: string, column: number, state: StageState, after: string[] = []): FeatureStage {
  return {
    key, id, role, column, state, after,
    passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null,
    barEnd: null, approxMinutes: 0, overlapsActive: false, executors: [], runningSince: null,
  };
}

const ids = (cs: { stages: FeatureStage[] }[]): string[][] => cs.map(c => c.stages.map(stageNodeId));

// === parallel-cycle (разрез 2 §1.10): 10 этапов, колонки 0..5 ===
// Порядок списка — топологический (бэк), в нём plan стоит раньше qaplan одной колонки.
function parallelCycle(): FeatureStage[] {
  return [
    mkStage('cut', 'cut', 'cut', 0, 'done'),
    mkStage('design', 'design', 'design', 1, 'done', ['cut']),
    mkStage('plan', 'plan', 'plan', 2, 'done', ['design']),
    mkStage('qa', 'qaplan', 'qa', 2, 'pending', ['design']),
    mkStage('prod', 'step-back', 'step', 3, 'active', ['plan']),
    mkStage('prod', 'step-front', 'step', 3, 'pending', ['plan']),
    mkStage('qa', 'qa', 'qa', 3, 'pending', ['qaplan']),
    mkStage('review', 'review', 'review', 4, 'pending', ['step-back', 'step-front']),
    mkStage('final', 'final', 'final', 4, 'pending', ['qa']),
    mkStage('merge', 'merge', 'merge', 5, 'pending', ['final']),
  ];
}

describe('graphColumns — колонки строго по полю column', () => {
  it('parallel-cycle: колонки по полю column, step-back и step-front в одной колонке', () => {
    const cols = graphColumns(mkRun({ stages: parallelCycle() }));
    expect(cols.map(c => c.column)).toEqual([0, 1, 2, 3, 4, 5]);
    expect(ids(cols)).toEqual([
      ['cut'],
      ['design'],
      ['plan', 'qaplan'],
      ['step-back', 'step-front', 'qa'],
      ['review', 'final'],
      ['merge'],
    ]);
  });

  it('внутри колонки — порядок run.stages, не сортировка по id', () => {
    // Колонка 3 = step-back, step-front, qa. Поменять их местами в списке — модель обязана
    // сохранить порядок списка, а не привести к алфавиту id.
    const stages = parallelCycle();
    const front = stages[5]!;
    const qa = stages[6]!;
    const swapped = stages.map(s => (s === front ? qa : s === qa ? front : s));
    const col3 = graphColumns(mkRun({ stages: swapped })).find(c => c.column === 3)!;
    expect(col3.stages.map(stageNodeId)).toEqual(['step-back', 'qa', 'step-front']);
  });

  it('значения column не сжимаются и не перенумеровываются', () => {
    // Пустая колонка между непустыми (в живом графа её нет, но поле — с бэка, и модель его
    // не переписывает): порядок — по значениям.
    const stages = [
      mkStage('cut', 'cut', 'cut', 0, 'done'),
      mkStage('final', 'final', 'final', 2, 'pending', ['cut']),
    ];
    const cols = graphColumns(mkRun({ stages }));
    expect(cols.map(c => c.column)).toEqual([0, 2]);
  });

  it('легаси (все column 0, графа нет) — одна колонка в порядке run.stages', () => {
    const legacy = (['cut', 'plan', 'prod', 'review', 'final'] as StageKey[]).map(k =>
      mkStage(k, k, k === 'prod' ? 'step' : k, 0, k === 'prod' ? 'active' : 'done'));
    const cols = graphColumns(mkRun({ stages: legacy }));
    expect(cols).toHaveLength(1);
    expect(cols[0]!.column).toBe(0);
    expect(cols[0]!.stages.map(s => s.key)).toEqual(['cut', 'plan', 'prod', 'review', 'final']);
  });

  it('отсутствующее column (старый ответ) — 0, как бэк для легаси; отсутствие stages — пусто', () => {
    const oldCut: FeatureStage = { ...mkStage('cut', 'cut', 'cut', 0, 'done'), column: undefined };
    const oldPlan: FeatureStage = { ...mkStage('plan', 'plan', 'plan', 1, 'pending', ['cut']), column: undefined };
    expect(ids(graphColumns(mkRun({ stages: [oldCut, oldPlan] })))).toEqual([['cut', 'plan']]);
    expect(graphColumns(mkRun())).toEqual([]);
    expect(graphColumns(mkRun({ stages: undefined }))).toEqual([]);
  });
});

describe('graphEdges — рёбра строго по after', () => {
  it('parallel-cycle: рёбер ровно столько, сколько элементов во всех after (10)', () => {
    const stages = parallelCycle();
    const expectedCount = stages.reduce((n, s) => n + (s.after?.length ?? 0), 0);
    expect(expectedCount).toBe(10);
    expect(graphEdges(mkRun({ stages }))).toHaveLength(expectedCount);
  });

  it('ребро [from → to] по каждой записи after, в порядке обхода run.stages', () => {
    const edges = graphEdges(mkRun({ stages: parallelCycle() }));
    expect(edges).toEqual([
      { from: 'cut', to: 'design' },
      { from: 'design', to: 'plan' },
      { from: 'design', to: 'qaplan' },
      { from: 'plan', to: 'step-back' },
      { from: 'plan', to: 'step-front' },
      { from: 'qaplan', to: 'qa' },
      { from: 'step-back', to: 'review' },
      { from: 'step-front', to: 'review' },
      { from: 'qa', to: 'final' },
      { from: 'final', to: 'merge' },
    ]);
  });

  it('ссылка на отсутствующую стадию ребра не даёт: провести его не между чем', () => {
    const stages = [
      mkStage('cut', 'cut', 'cut', 0, 'done'),
      mkStage('final', 'final', 'final', 1, 'pending', ['cut', 'gone']),
    ];
    expect(graphEdges(mkRun({ stages }))).toEqual([{ from: 'cut', to: 'final' }]);
  });

  it('легаси (after пусто) — рёбер нет; fast-track — линейная цепочка', () => {
    expect(graphEdges(mkRun({ stages: [mkStage('cut', 'cut', 'cut', 0, 'done'), mkStage('plan', 'plan', 'plan', 0, 'pending')] }))).toEqual([]);
    const fast = fastTrack();
    expect(graphEdges(mkRun({ stages: fast }))).toEqual([
      { from: 'cut', to: 'plan' }, { from: 'plan', to: 'prod' }, { from: 'prod', to: 'final' }, { from: 'final', to: 'merge' },
    ]);
  });
});

describe('defaultSelected — первая active, иначе последняя done', () => {
  it('две active: первая по порядку run.stages, не по колонкам и не последняя', () => {
    const stages = parallelCycle();
    stages[4] = { ...stages[4]!, state: 'active', tone: 'run' }; // step-back
    stages[6] = { ...stages[6]!, state: 'active', tone: 'wait' }; // qa
    expect(defaultSelected(mkRun({ stages }))).toBe('step-back');
    // Тот же граф, но active стоят в обратном порядке списка — выбор идёт по списку.
    const swapped = [stages[6]!, ...stages.filter(s => s !== stages[6]!)];
    expect(defaultSelected(mkRun({ stages: swapped }))).toBe('qa');
  });

  it('active нет — последняя done в порядке run.stages', () => {
    const stages = parallelCycle();
    stages[4] = { ...stages[4]!, state: 'pending' }; // снять единственную active (step-back)
    // done по списку: cut(0), design(1), plan(2) — последняя по порядку, а не по колонкам,
    // это plan.
    expect(defaultSelected(mkRun({ stages }))).toBe('plan');
  });

  it('ни active, ни done — null (фича ещё не начиналась)', () => {
    const untouched = parallelCycle().map(s => ({ ...s, state: 'pending' as StageState }));
    expect(defaultSelected(mkRun({ stages: untouched }))).toBeNull();
    expect(defaultSelected(mkRun())).toBeNull();
  });
});

describe('fast-track (5 стадий) — 5 колонок по одной', () => {
  it('колонки 0..4, в каждой ровно одна стадия', () => {
    const cols = graphColumns(mkRun({ stages: fastTrack() }));
    expect(cols.map(c => c.column)).toEqual([0, 1, 2, 3, 4]);
    expect(cols.every(c => c.stages.length === 1)).toBe(true);
  });
});

describe('stageNodeId — id с бэка, фолбэк = ключ (как бэк для легаси)', () => {
  it('id есть — id; id нет (старый ответ) — ключ', () => {
    expect(stageNodeId(mkStage('prod', 'step-back', 'step', 3, 'active'))).toBe('step-back');
    const old: FeatureStage = { ...mkStage('prod', 'prod', 'step', 0, 'active'), id: undefined };
    expect(stageNodeId(old)).toBe('prod');
  });
});

// fast-track (разрез 2 §1.10: «id = роль, After = null» в шаблоне; нормализация бэка
// подставляет линейную цепочку After=[предыдущий], WorkflowGraph.cs:54-56) — 5 стадий,
// колонки 0..4.
function fastTrack(): FeatureStage[] {
  return [
    mkStage('cut', 'cut', 'cut', 0, 'done'),
    mkStage('plan', 'plan', 'plan', 1, 'pending', ['cut']),
    mkStage('prod', 'prod', 'step', 2, 'pending', ['plan']),
    mkStage('final', 'final', 'final', 3, 'pending', ['prod']),
    mkStage('merge', 'merge', 'merge', 4, 'pending', ['final']),
  ];
}
