// Конвертеры честного прогресса (волна 3, Ф-pr) — юниты на `stepProgressFromRun`
// и `stepProgressFromIndex` (там вся логика: hasNew, planState ?? 'done', обнуление
// withdrawn/unlinkedReworks, маппинг полей индекса, clamp отрицательных).
//
// Окружение node (без jsdom): чистые функции, без DOM. Шаблон — featureProgress.test.ts.

import { describe, expect, it } from 'vitest';
import { stepProgressFromRun, stepProgressFromIndex, formatStepProgress } from './featureProgress';
import type { FeatureIndexFeature } from '../types/featureIndex';
import type { FeatureRun } from '../types/featureRun';

// === Фикстуры ===

// Минимальная строка фичи индекса: новые поля (stepsReview/stepsWork/stepsNotCreated/
// planState) по умолчанию отсутствуют (undefined) — как в старых ответах.
function idx(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Фича', projectId: 'p1',
    bucket: 'running', state: 'running', attention: null,
    rail: [], chips: [],
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  };
}

// Минимальный агрегат фичи: stepProgress по умолчанию отсутствует (старый ответ).
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running',
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    ...over,
  } as FeatureRun;
}

// === stepProgressFromRun: run.stepProgress ?? null ===
describe('stepProgressFromRun', () => {
  it('stepProgress задан → возвращает его (те же значения)', () => {
    const sp = { planState: 'partial' as const, total: 5, accepted: 2, review: 2, work: 0, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 };
    expect(stepProgressFromRun(run({ stepProgress: sp }))).toEqual(sp);
  });

  it('stepProgress отсутствует (старый ответ) → null', () => {
    expect(stepProgressFromRun(run())).toBeNull();
  });
});

// === stepProgressFromIndex: hasNew / planState ?? 'done' / обнуление / clamp ===
describe('stepProgressFromIndex', () => {
  it('hasNew=false (только stepsAccepted/stepsTotal/loops, новых полей нет) → null', () => {
    expect(stepProgressFromIndex(idx({ stepsAccepted: 3, stepsTotal: 5, loops: 2 }))).toBeNull();
  });

  it('null-вход (новые поля приходят null, как в старых JSON) → null', () => {
    const f = { ...idx(), stepsReview: null, stepsWork: null, stepsNotCreated: null, planState: null } as unknown as FeatureIndexFeature;
    expect(stepProgressFromIndex(f)).toBeNull();
  });

  it('hasNew=true (stepsReview) + planState отсутствует → planState «done»', () => {
    const p = stepProgressFromIndex(idx({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2 }));
    expect(p).not.toBeNull();
    expect(p!.planState).toBe('done');
  });

  it('hasNew=true: review/work/notCreated маппятся, withdrawn/unlinkedReworks обнулены', () => {
    const p = stepProgressFromIndex(idx({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2, stepsWork: 1, stepsNotCreated: 3, planState: 'partial' }));
    expect(p).toEqual({ planState: 'partial', total: 5, accepted: 3, review: 2, work: 1, notCreated: 3, withdrawn: 0, unlinkedReworks: 0 });
  });

  it('отрицательные значения (баг бэка) → обнулены (нет «принято -2 из -5»)', () => {
    const p = stepProgressFromIndex(idx({ stepsAccepted: -2, stepsTotal: -5, stepsReview: -3, stepsWork: -1, stepsNotCreated: -4, planState: 'partial' }));
    expect(p).toEqual({ planState: 'partial', total: 0, accepted: 0, review: 0, work: 0, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 });
  });

  it('stepsWithdrawn/unlinkedReworks из индекса подтягиваются (не хардкод 0) → «X снято · Y без привязки»', () => {
    const f = idx({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 0, stepsWork: 0, stepsNotCreated: 0, planState: 'done', stepsWithdrawn: 2, unlinkedReworks: 1 });
    const p = stepProgressFromIndex(f);
    expect(p).not.toBeNull();
    expect(p!.withdrawn).toBe(2);
    expect(p!.unlinkedReworks).toBe(1);
    expect(formatStepProgress(p!)).toBe('принято 3 из 5 · 2 снято · 1 без привязки');
  });
});
