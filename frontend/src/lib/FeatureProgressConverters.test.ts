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
// Все числа согласованы с инвариантом бэка: total = accepted + review + work + notCreated
// (FeatureStepAccounting.cs, «Итог: контрактные числа») — такого ответа сервер не даёт.

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
    const sp = { planState: 'partial' as const, total: 5, accepted: 2, review: 2, work: 1, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 };
    expect(stepProgressFromRun(run({ stepProgress: sp }))).toEqual(sp);
  });

  it('stepProgress отсутствует (старый ответ) → null', () => {
    expect(stepProgressFromRun(run())).toBeNull();
  });
});

// === stepProgressFromIndex: hasNew / planState ?? 'done' / обнуление / clamp ===
describe('stepProgressFromIndex', () => {
  it('hasNew=false (только stepsAccepted/stepsTotal/loops, новых полей нет) → null', () => {
    // Старый ответ: всё принято — total = accepted (иначе инвариант бэка нарушен).
    expect(stepProgressFromIndex(idx({ stepsAccepted: 3, stepsTotal: 3, loops: 2 }))).toBeNull();
  });

  it('null-вход (новые поля приходят null, как в старых JSON) → null', () => {
    const f = { ...idx(), stepsReview: null, stepsWork: null, stepsNotCreated: null, planState: null } as unknown as FeatureIndexFeature;
    expect(stepProgressFromIndex(f)).toBeNull();
  });

  // Мутация: вернуть дефолт 'done' — тест красный: «готово» без данных о плане.
  it('hasNew=true (stepsReview) + planState отсутствует → безопасный дефолт «pending» (не «готово»)', () => {
    const p = stepProgressFromIndex(idx({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2 }));
    expect(p).not.toBeNull();
    expect(p!.planState).toBe('pending');
    // Формат: без «готово», с меткой «план не разобран».
    expect(formatStepProgress(p!)).toBe('принято 3 из 5 · 2 в ревью · план не разобран');
  });

  it('hasNew=true: review/work/notCreated маппятся, withdrawn/unlinkedReworks обнулены', () => {
    // 2+1+1+1 = 5 — корзины в сумме дают знаменатель (инвариант бэка).
    const p = stepProgressFromIndex(idx({ stepsAccepted: 2, stepsTotal: 5, stepsReview: 1, stepsWork: 1, stepsNotCreated: 1, planState: 'partial' }));
    expect(p).toEqual({ planState: 'partial', total: 5, accepted: 2, review: 1, work: 1, notCreated: 1, withdrawn: 0, unlinkedReworks: 0 });
  });

  it('отрицательные значения (баг бэка) → обнулены (нет «принято -2 из -5»)', () => {
    // Даже в «баге» суммы сходятся: -2-3-1-4 = -10 (инвариант бэка).
    const p = stepProgressFromIndex(idx({ stepsAccepted: -2, stepsTotal: -10, stepsReview: -3, stepsWork: -1, stepsNotCreated: -4, planState: 'partial' }));
    expect(p).toEqual({ planState: 'partial', total: 0, accepted: 0, review: 0, work: 0, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 });
  });

  it('stepsWithdrawn/unlinkedReworks из индекса подтягиваются (не хардкод 0) → «X снято · Y без привязки»', () => {
    // 3+2 = 5 — знаменатель закрыт корзиной ревью (инвариант бэка).
    // planState='cut': полное принятие ещё не достигнут (3+2 = 5), «готово» не должна
    // появляться — проверяется именно подтягивание withdrawn/unlinkedReworks.
    const f = idx({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2, stepsWork: 0, stepsNotCreated: 0, planState: 'cut', stepsWithdrawn: 2, unlinkedReworks: 1 });
    const p = stepProgressFromIndex(f);
    expect(p).not.toBeNull();
    expect(p!.withdrawn).toBe(2);
    expect(p!.unlinkedReworks).toBe(1);
    expect(formatStepProgress(p!)).toBe('принято 3 из 5 · 2 в ревью · 2 снято · 1 без привязки');
  });
});
