import { describe, it, expect } from 'vitest';
import { formatStepProgress, isStepProgressDone, type StepProgress } from './featureProgress';

// Образец с дефолтами: total=0, всё 0, planState='done' (без метки плана).
// Переопределяем точечно — тесты читаются как «что меняю → что жду».
function sp(over: Partial<StepProgress> = {}): StepProgress {
  return {
    planState: 'done',
    total: 0,
    accepted: 0,
    review: 0,
    work: 0,
    notCreated: 0,
    withdrawn: 0,
    unlinkedReworks: 0,
    ...over,
  };
}

describe('formatStepProgress — база «принято N из T»', () => {
  it('«принято N из T» — единственный случай с предлогом «из»', () => {
    expect(formatStepProgress(sp({ total: 5, accepted: 3 }))).toBe('принято 3 из 5');
  });

  it('голый «N из M» без слов не остаётся: 0/0 → «принято 0 из 0»', () => {
    expect(formatStepProgress(sp({ total: 0, accepted: 0 }))).toBe('принято 0 из 0');
  });
});

describe('formatStepProgress — «готово» (условие явное)', () => {
  it('«готово» когда accepted === total && total > 0 && planState !== pending', () => {
    expect(formatStepProgress(sp({ planState: 'done', total: 5, accepted: 5 }))).toBe('готово');
    expect(formatStepProgress(sp({ planState: 'cut', total: 3, accepted: 3 }))).toBe('готово');
  });

  // Мутация 7: показать «готово» при total === 0.
  it('м7: НЕ «готово» при total === 0 (даже accepted === total)', () => {
    expect(formatStepProgress(sp({ planState: 'done', total: 0, accepted: 0 }))).toBe('принято 0 из 0');
  });

  // Мутация 1: снять `accepted === total` из условия «готовности».
  it('м1: НЕ «готово» когда accepted < total (total > 0, planState !== pending)', () => {
    expect(formatStepProgress(sp({ planState: 'done', total: 5, accepted: 3 }))).toBe('принято 3 из 5');
  });

  // Мутация 2: снять `planState !== 'pending'` из условия «готовности».
  it('м2: НЕ «готово» при planState === pending (даже accepted === total)', () => {
    expect(formatStepProgress(sp({ planState: 'pending', total: 2, accepted: 2 }))).toBe('принято 2 из 2 · план не разобран');
  });
});

describe('formatStepProgress — корзины знаменателя (review/work/notCreated)', () => {
  // Мутация 4: потерять review-сегмент при work === 0.
  it('м4: «↺R в ревью» при review > 0, даже если work === 0', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 0, review: 2, work: 0 }))).toBe('принято 0 из 3 · ↺2 в ревью');
  });

  it('«W в работе» при work > 0', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 0, work: 2 }))).toBe('принято 0 из 3 · 2 в работе');
  });

  // Мутация 8: не показать «не заведено» при ненулевом значении.
  it('м8: «∅N не заведено» при notCreated > 0', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 0, notCreated: 2 }))).toBe('принято 0 из 3 · ∅2 не заведено');
  });

  it('нулевые корзины не дают лишних сегментов', () => {
    expect(formatStepProgress(sp({ total: 2, accepted: 2, review: 0, work: 0, notCreated: 0 }))).toBe('готово');
  });
});

describe('formatStepProgress — метка плана (planState)', () => {
  // Мутация 5: потерять «план не разобран» при pending.
  it('м5: «план не разобран» при planState === pending', () => {
    expect(formatStepProgress(sp({ planState: 'pending', total: 1, accepted: 0 }))).toBe('принято 0 из 1 · план не разобран');
  });

  it('«план частично» при planState === partial', () => {
    expect(formatStepProgress(sp({ planState: 'partial', total: 2, accepted: 1 }))).toBe('принято 1 из 2 · план частично');
  });

  it('метки плана НЕТ при cut/done (там и так понятно)', () => {
    expect(formatStepProgress(sp({ planState: 'cut', total: 2, accepted: 1 }))).toBe('принято 1 из 2');
    expect(formatStepProgress(sp({ planState: 'done', total: 2, accepted: 1 }))).toBe('принято 1 из 2');
  });
});

describe('formatStepProgress — вне знаменателя (withdrawn/unlinkedReworks)', () => {
  // Мутация 6: потерять «снято» при withdrawn > 0.
  it('м6: «X снято» при withdrawn > 0 (слово «снято», не «отменено»)', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 2, withdrawn: 1 }))).toBe('принято 2 из 3 · 1 снято');
  });

  it('«Y без привязки» при unlinkedReworks > 0', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 2, unlinkedReworks: 1 }))).toBe('принято 2 из 3 · 1 без привязки');
  });

  it('нулевые withdrawn/unlinkedReworks — без сегментов', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 2, withdrawn: 0, unlinkedReworks: 0 }))).toBe('принято 2 из 3');
  });
});

describe('formatStepProgress — порядок сегментов', () => {
  // Мутация 3: подменить порядок сегментов.
  it('м3: порядок фиксирован: база → review → work → notCreated → план → снято → без привязки', () => {
    const p = sp({ planState: 'partial', total: 10, accepted: 3, review: 2, work: 1, notCreated: 4, withdrawn: 1, unlinkedReworks: 1 });
    expect(formatStepProgress(p)).toBe(
      'принято 3 из 10 · ↺2 в ревью · 1 в работе · ∅4 не заведено · план частично · 1 снято · 1 без привязки',
    );
  });

  it('«готово» + «план частично» (partial и всё принято)', () => {
    expect(formatStepProgress(sp({ planState: 'partial', total: 2, accepted: 2 }))).toBe('готово · план частично');
  });
});

describe('isStepProgressDone — единая точка правды', () => {
  it('true только при total > 0 && accepted === total && planState !== pending', () => {
    expect(isStepProgressDone(sp({ planState: 'done', total: 5, accepted: 5 }))).toBe(true);
    expect(isStepProgressDone(sp({ planState: 'cut', total: 3, accepted: 3 }))).toBe(true);
    // total === 0
    expect(isStepProgressDone(sp({ planState: 'done', total: 0, accepted: 0 }))).toBe(false);
    // accepted < total
    expect(isStepProgressDone(sp({ planState: 'done', total: 5, accepted: 3 }))).toBe(false);
    // planState === pending
    expect(isStepProgressDone(sp({ planState: 'pending', total: 5, accepted: 5 }))).toBe(false);
  });
});
