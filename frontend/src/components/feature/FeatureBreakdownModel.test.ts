// Модель блока «Кто и на чём работал» (ST-F8). Чистые функции — в node, без DOM.
//
// Покрытие: sortGroups — убывание по time/tokens/cost, null в конце, стабильность
// среди null, вход не мутируется; formatMoney — «бесплатно» только при токенах,
// «нет учёта» при null, иначе formatUsd; groupChips — модели/роли персоны, персоны
// модели/роли, дедупликация, отсутствие executors — []; chatLabel — «Ш2 · доработка»
// из executors, «шаг» для step без слова роли, «чат» когда исполнитель не найден;
// liveChipText — running/«работает», waiting/«ждёт вас», running важнее, у закрытой
// — null; fewData — <2 групп; percentText — округление каждой доли (сумма может
// дать 101 %).
//
// Мутации (в отчёте): (1) убрать ветку «бесплатно» в formatMoney — краснеет тест
// «0 при токенах — «бесплатно»»; (2) `return 1` на `return -1` в sortGroups для
// null — краснеет «null в конце»; (3) снять `run.closed === true ||` в liveChipText —
// краснеет «у закрытой фичи — ничего».

import { describe, it, expect } from 'vitest';
import {
  sortGroups, formatMoney, groupChips, chatLabel, liveChipText, fewData, percentText,
} from './FeatureBreakdownModel';
import type { FeatureRun, FeatureStage, FeatureStageExecutor } from '../../types/featureRun';
import type { FeatureBreakdownGroup } from '../../types/featureStats';

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

function mkExecutor(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return {
    personaId: null, taskId: 't-1', taskRole: null, stepRef: null, lane: null,
    status: 'done', chatId: null, chatArchived: false,
    modelLabel: null, modelFull: null, ...over,
  };
}

function mkStage(executors: FeatureStageExecutor[]): FeatureStage {
  return {
    key: 'prod', state: 'done', passes: 1, taskIds: [], rounds: [],
    firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0,
    overlapsActive: false, executors,
  };
}

function mkGroup(over: Partial<FeatureBreakdownGroup> = {}): FeatureBreakdownGroup {
  return { key: 'k', minutes: 0, tokens: null, costUsd: null, chatIds: [], ...over };
}

describe('sortGroups', () => {
  const groups = [
    mkGroup({ key: 'a', minutes: 100, tokens: 500, costUsd: 1.0 }),
    mkGroup({ key: 'b', minutes: 300, tokens: null, costUsd: null }),
    mkGroup({ key: 'c', minutes: 200, tokens: 1200, costUsd: 5.0 }),
    mkGroup({ key: 'd', minutes: 50, tokens: 800, costUsd: null }),
  ];

  it('time — по убыванию минут', () => {
    expect(sortGroups(groups, 'time').map(g => g.key)).toEqual(['b', 'c', 'a', 'd']);
  });

  it('tokens — по убыванию, null в конце в исходном порядке', () => {
    expect(sortGroups(groups, 'tokens').map(g => g.key)).toEqual(['c', 'd', 'a', 'b']);
  });

  it('cost — по убыванию, null в конце в исходном порядке', () => {
    expect(sortGroups(groups, 'cost').map(g => g.key)).toEqual(['c', 'a', 'b', 'd']);
  });

  it('вход не мутируется', () => {
    const copy = [...groups];
    sortGroups(groups, 'time');
    expect(groups.map(g => g.key)).toEqual(copy.map(g => g.key));
  });

  it('пустой список — пустой', () => {
    expect(sortGroups([], 'time')).toEqual([]);
  });
});

describe('formatMoney', () => {
  it('0 при токенах — «бесплатно»', () => {
    expect(formatMoney(mkGroup({ costUsd: 0, tokens: 1200 }))).toBe('бесплатно');
  });

  it('0 без токенов — «$0,00», не «бесплатно»', () => {
    expect(formatMoney(mkGroup({ costUsd: 0, tokens: 0 }))).toBe('$0,00');
    expect(formatMoney(mkGroup({ costUsd: 0, tokens: null }))).toBe('$0,00');
  });

  it('null — «нет учёта»', () => {
    expect(formatMoney(mkGroup({ costUsd: null, tokens: 500 }))).toBe('нет учёта');
  });

  it('обычная цена — formatUsd', () => {
    expect(formatMoney(mkGroup({ costUsd: 15.6, tokens: 1000 }))).toBe('$15,60');
  });
});

describe('groupChips', () => {
  const run = mkRun({
    stages: [
      mkStage([
        mkExecutor({ personaId: 'p-timur', taskRole: 'step', stepRef: 'Ш1', modelFull: 'claude-opus-5.5', status: 'done' }),
        mkExecutor({ personaId: 'p-timur', taskRole: 'rework', stepRef: 'Ш2', modelFull: 'claude-opus-5.5', status: 'done' }),
        mkExecutor({ personaId: 'p-denis', taskRole: 'step', stepRef: 'Ш3', modelFull: 'qwen3.8-27b', status: 'running' }),
      ]),
      mkStage([
        mkExecutor({ personaId: 'p-gleb', taskRole: 'review', modelFull: null, status: 'done' }),
      ]),
    ],
  });

  it('персона — её модели и роли, дедуплицированные', () => {
    expect(groupChips('personas', 'p-timur', run)).toEqual([
      { kind: 'model', key: 'claude-opus-5.5' },
      { kind: 'role', key: 'step' },
      { kind: 'role', key: 'rework' },
    ]);
  });

  it('модель — персоны, работавшие на ней', () => {
    expect(groupChips('models', 'qwen3.8-27b', run)).toEqual([{ kind: 'persona', key: 'p-denis' }]);
  });

  it('роль — персоны с этой ролью', () => {
    expect(groupChips('roles', 'review', run)).toEqual([{ kind: 'persona', key: 'p-gleb' }]);
  });

  it('без executors — [], не исключение строки', () => {
    expect(groupChips('personas', 'p-timur', mkRun())).toEqual([]);
    expect(groupChips('personas', 'p-timur', mkRun({ stages: [] }))).toEqual([]);
  });

  it('null modelFull — модельного чипа нет, ролевой остаётся', () => {
    expect(groupChips('personas', 'p-gleb', run)).toEqual([{ kind: 'role', key: 'review' }]);
  });
});

describe('chatLabel', () => {
  const run = mkRun({
    stages: [
      mkStage([
        mkExecutor({ personaId: 'p-kira', taskRole: 'rework', stepRef: 'Ш2', chatId: 'c-1' }),
        mkExecutor({ personaId: 'p-kira', taskRole: 'step', stepRef: 'Ш1', chatId: 'c-2' }),
        mkExecutor({ personaId: 'p-gleb', taskRole: 'review', chatId: 'c-3' }),
        mkExecutor({ personaId: 'p-egor', taskRole: null, chatId: 'c-4' }),
      ]),
    ],
  });

  it('исполнитель с шагом и ролью — «Ш2 · доработка»', () => {
    expect(chatLabel('c-1', run)).toBe('Ш2 · доработка');
  });

  it('шаг без слова роли — «Ш1 · шаг»', () => {
    expect(chatLabel('c-2', run)).toBe('Ш1 · шаг');
  });

  it('роль без шага — «ревью»', () => {
    expect(chatLabel('c-3', run)).toBe('ревью');
  });

  it('исполнитель без шага и роли — «чат»', () => {
    expect(chatLabel('c-4', run)).toBe('чат');
  });

  it('чата среди исполнителей нет — «чат», без id', () => {
    expect(chatLabel('c-99', run)).toBe('чат');
    expect(chatLabel('c-99', mkRun())).toBe('чат');
  });
});

describe('liveChipText', () => {
  const run = mkRun({
    stages: [
      mkStage([
        mkExecutor({ personaId: 'p-timur', status: 'running' }),
        mkExecutor({ personaId: 'p-timur', status: 'waiting' }),
        mkExecutor({ personaId: 'p-denis', status: 'waiting' }),
        mkExecutor({ personaId: 'p-gleb', status: 'stopped' }),
      ]),
    ],
  });

  it('running — «работает», и running важнее waiting у той же персоны', () => {
    expect(liveChipText(run, 'p-timur')).toBe('работает');
  });

  it('waiting — «ждёт вас»', () => {
    expect(liveChipText(run, 'p-denis')).toBe('ждёт вас');
  });

  it('другие статусы — чипа нет', () => {
    expect(liveChipText(run, 'p-gleb')).toBeNull();
    expect(liveChipText(run, 'p-unknown')).toBeNull();
  });

  it('у закрытой фичи — ничего', () => {
    expect(liveChipText(mkRun({ state: 'merged', closed: true, stages: run.stages }), 'p-timur')).toBeNull();
    expect(liveChipText(mkRun({ state: 'closed', stages: run.stages }), 'p-timur')).toBeNull();
  });
});

describe('fewData', () => {
  it('меньше двух групп — true', () => {
    expect(fewData([])).toBe(true);
    expect(fewData([mkGroup({ key: 'a', minutes: 10 })])).toBe(true);
  });

  it('две группы — false', () => {
    expect(fewData([mkGroup({ key: 'a' }), mkGroup({ key: 'b' })])).toBe(false);
  });
});

describe('percentText', () => {
  it('доля 0..1 → «42 %»', () => {
    expect(percentText(0.42)).toBe('42 %');
    expect(percentText(1 / 3)).toBe('33 %');
    expect(percentText(0)).toBe('0 %');
    expect(percentText(1)).toBe('100 %');
  });

  it('округление каждой доли независимо: три по 1/3 дают 34 % × 3 = 101 %', () => {
    const thirds = [percentText(1 / 3), percentText(1 / 3), percentText(1 / 3)];
    expect(thirds).toEqual(['33 %', '33 %', '33 %']);
    const rounded = [percentText(0.335), percentText(0.335), percentText(0.335)];
    expect(rounded).toEqual(['34 %', '34 %', '34 %']);
  });
});
