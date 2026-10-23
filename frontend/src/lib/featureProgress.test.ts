import { describe, it, expect } from 'vitest';
import { formatStepProgress, isStepProgressDone, type StepProgress } from './featureProgress';

// Образец с дефолтами: total=0, всё 0, planState='cut' (метки плана нет, «готово» нет —
// «готово» только при явном 'done'). Переопределяем точечно — тесты читаются как
// «что меняю → что жду».
// Все переопределения согласованы с инвариантом бэка: total = accepted + review + work +
// notCreated (FeatureStepAccounting.cs, «Итог: контрактные числа») — ответа с суммой
// меньше знаменателя сервер не даёт. Фикстуры с planState='done' всегда полные: бэк
// выдаёт done только при total > 0 и полном принятии.
function sp(over: Partial<StepProgress> = {}): StepProgress {
  return {
    planState: 'cut',
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
    // 3+2 = 5: остаток знаменателя — корзина ревью (иначе инвариант бэка нарушен).
    expect(formatStepProgress(sp({ total: 5, accepted: 3, review: 2 }))).toBe('принято 3 из 5 · 2 в ревью');
  });

  it('голый «N из M» без слов не остаётся: 0/0 → «принято 0 из 0»', () => {
    expect(formatStepProgress(sp({ total: 0, accepted: 0 }))).toBe('принято 0 из 0');
  });
});

describe('formatStepProgress — «готово» (решает только planState === done)', () => {
  it('«готово» при planState === done (бэк даёт его только при total > 0 и полном принятии)', () => {
    expect(formatStepProgress(sp({ planState: 'done', total: 5, accepted: 5 }))).toBe('готово');
  });

  // Мутация: вернуть старое условие (accepted === total && total > 0 && planState !== 'pending')
  // — три теста ниже краснеют: «готово» решает не полнота цифр.
  it('м7: НЕ «готово» при cut и полном принятии (разрез плана ≠ конец работы)', () => {
    expect(formatStepProgress(sp({ planState: 'cut', total: 3, accepted: 3 }))).toBe('принято 3 из 3');
  });

  it('м1: НЕ «готово» при partial и полном принятии (знаменатель может быть неполным)', () => {
    expect(formatStepProgress(sp({ planState: 'partial', total: 2, accepted: 2 }))).toBe('принято 2 из 2 · план частично');
  });

  it('м2: НЕ «готово» при pending (даже accepted === total)', () => {
    expect(formatStepProgress(sp({ planState: 'pending', total: 2, accepted: 2 }))).toBe('принято 2 из 2 · план не разобран');
  });

  it('НЕ «готово» при пустом знаменателе (total === 0, planState !== done)', () => {
    expect(formatStepProgress(sp({ planState: 'cut', total: 0, accepted: 0 }))).toBe('принято 0 из 0');
  });
});

describe('formatStepProgress — корзины знаменателя (review/work/notCreated)', () => {
  // Мутация 4: потерять review-сегмент при work === 0.
  // ↺ НЕ ставим (глиф за возвратами/loops в итоге смерженной): корзина ревью — «R в ревью».
  it('м4: «R в ревью» (БЕЗ ↺) при review > 0, даже если work === 0', () => {
    // Знаменатель ровно из корзинов: 2+0 = 2 (иначе такой строки бэк не даёт).
    expect(formatStepProgress(sp({ total: 2, accepted: 0, review: 2, work: 0 }))).toBe('принято 0 из 2 · 2 в ревью');
  });

  it('«W в работе» при work > 0', () => {
    expect(formatStepProgress(sp({ total: 2, accepted: 0, work: 2 }))).toBe('принято 0 из 2 · 2 в работе');
  });

  // Мутация 8: не показать «не заведено» при ненулевом значении.
  it('м8: «∅N не заведено» при notCreated > 0', () => {
    expect(formatStepProgress(sp({ total: 2, accepted: 0, notCreated: 2 }))).toBe('принято 0 из 2 · ∅2 не заведено');
  });

  it('нулевые корзины не дают лишних сегментов', () => {
    // Полное принятие при cut — ещё не «готово»: «готово» только при done.
    expect(formatStepProgress(sp({ planState: 'cut', total: 2, accepted: 2, review: 0, work: 0, notCreated: 0 }))).toBe('принято 2 из 2');
    expect(formatStepProgress(sp({ planState: 'done', total: 2, accepted: 2, review: 0, work: 0, notCreated: 0 }))).toBe('готово');
  });
});

describe('formatStepProgress — метка плана (planState)', () => {
  // Мутация 5: потерять «план не разобран» при pending.
  it('м5: «план не разобран» при planState === pending', () => {
    // План не разобран — все шаги в корзине notCreated: 1 = 0+0+0+1.
    expect(formatStepProgress(sp({ planState: 'pending', total: 1, accepted: 0, notCreated: 1 }))).toBe('принято 0 из 1 · ∅1 не заведено · план не разобран');
  });

  it('«план частично» при planState === partial', () => {
    expect(formatStepProgress(sp({ planState: 'partial', total: 2, accepted: 1, work: 1 }))).toBe('принято 1 из 2 · 1 в работе · план частично');
  });

  it('метки плана НЕТ при cut/done (там и так понятно)', () => {
    // Фикстура 'done' согласована с бэком: done ⇒ total > 0 и всё принято.
    expect(formatStepProgress(sp({ planState: 'cut', total: 2, accepted: 1, review: 1 }))).toBe('принято 1 из 2 · 1 в ревью');
    expect(formatStepProgress(sp({ planState: 'done', total: 2, accepted: 2 }))).toBe('готово');
  });
});

describe('formatStepProgress — вне знаменателя (withdrawn/unlinkedReworks)', () => {
  // Мутация 6: потерять «снято» при withdrawn > 0.
  it('м6: «X снято» при withdrawn > 0 (слово «снято», не «отменено»)', () => {
    // 2+1 = 3: остаток знаменателя — корзина ревью (withdrawn в знаменатель не входит).
    expect(formatStepProgress(sp({ total: 3, accepted: 2, review: 1, withdrawn: 1 }))).toBe('принято 2 из 3 · 1 в ревью · 1 снято');
  });

  it('«Y без привязки» при unlinkedReworks > 0', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 2, review: 1, unlinkedReworks: 1 }))).toBe('принято 2 из 3 · 1 в ревью · 1 без привязки');
  });

  it('нулевые withdrawn/unlinkedReworks — без сегментов', () => {
    expect(formatStepProgress(sp({ total: 3, accepted: 2, review: 1, withdrawn: 0, unlinkedReworks: 0 }))).toBe('принято 2 из 3 · 1 в ревью');
  });
});

describe('formatStepProgress — порядок сегментов', () => {
  // Мутация 3: подменить порядок сегментов.
  it('м3: порядок фиксирован: база → review → work → notCreated → план → снято → без привязки', () => {
    const p = sp({ planState: 'partial', total: 10, accepted: 3, review: 2, work: 1, notCreated: 4, withdrawn: 1, unlinkedReworks: 1 });
    expect(formatStepProgress(p)).toBe(
      'принято 3 из 10 · 2 в ревью · 1 в работе · ∅4 не заведено · план частично · 1 снято · 1 без привязки',
    );
  });

  // Противоречия «готово · план частично» больше нет: при partial «готово» не ставится
  // (знаменатель может быть неполным) — см. м1 в блоке «готово».
  it('при partial и полном принятии — «принято N из N · план частично», без «готово»', () => {
    expect(formatStepProgress(sp({ planState: 'partial', total: 2, accepted: 2 }))).toBe('принято 2 из 2 · план частично');
  });
});

describe('isStepProgressDone — единая точка правды', () => {
  it('true только при planState === done (для него бэк гарантирует total > 0 и полное принятие)', () => {
    expect(isStepProgressDone(sp({ planState: 'done', total: 5, accepted: 5 }))).toBe(true);
    // Старое условие (accepted === total && planState !== 'pending') больше не действует:
    // полнота цифр не означает конца — знаменатель при partial может быть неполным.
    expect(isStepProgressDone(sp({ planState: 'cut', total: 3, accepted: 3 }))).toBe(false);
    expect(isStepProgressDone(sp({ planState: 'partial', total: 2, accepted: 2 }))).toBe(false);
    expect(isStepProgressDone(sp({ planState: 'pending', total: 5, accepted: 5 }))).toBe(false);
    // Пустой знаменатель — тоже не «готово».
    expect(isStepProgressDone(sp({ planState: 'cut', total: 0, accepted: 0 }))).toBe(false);
  });
});
