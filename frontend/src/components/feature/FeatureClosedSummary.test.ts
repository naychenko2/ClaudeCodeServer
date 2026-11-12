// «Итоги» закрытой фичи (FS-FI). Рендер-тесты на fakeDom:
//   · смерженная — «Смержена … · шла …», ровно 3 плитки, четвёртой «точности срока» нет;
//   · initialEtaMinutes задан — «обещали ≈ …», не задан — слова «обещали» нет;
//   · без даты мержа — текст CLOSED_NO_MERGE_STATUS;
//   · isMobile — сетка 2 колонки (minmax), десктоп — 3;
//   · «Разбор ›» есть только при onOpenReview и вызывает его;
//   · followUp (FS-FVp) — «Доработки: N из M» под строкой итога; при null/total=0 — строки нет;
//     строка итога и плитки не меняются; «план расширен» в DOM 0;
//   · merged (showCloseHint=true) — подсказка «Закройте фичу, когда доработки не нужны»
//     сразу под строкой итога (FS-AC3b); у closed её нет: ни без мержа (mergedAt null/мусор),
//     ни после мержа (showCloseHint=false при валидном mergedAt — бэк его оставляет).
// FP-H1 (дефект Веры e128aac4): «возвраты» в герое — только verdicts.returned, не summary.loops
// (returned + замечания); замечаний в герое нет; FP-R3: «хвосты» → «открыто после мержа»
// (неизменяемая подпись; «доработки» — только строка followUp «Доработки: A из T»); без
// verdicts (старый ответ) — плитки возвратов нет, не падаем. Мутация «вернуть summary.loops»
// краснит «возвраты = verdicts.returned».
// Дата мержа форматируется в местном поясе (как featureTime) — поэтому зону пиним явно
// (Europe/Moscow). Пин — как в FeatureLaneAxis.test.ts. Фикстура близка к полуночи UTC
// (22:00Z → MSK следующий день, 01:00): подмена «локальная зона → UTC» в компоненте
// краснит тест (в UTC дата была бы предыдущей), чего фикстура 12:00Z не ловила.

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureClosedSummary, type FeatureClosedSummaryProps } from './FeatureClosedSummary';
import { CLOSED_NO_MERGE_STATUS } from '../../lib/chatGroups';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';

const ORIGINAL_TZ = process.env.TZ;

beforeEach(() => {
  process.env.TZ = 'Europe/Moscow';
  installFakeDom();
});
afterEach(() => {
  uninstallFakeDom();
  if (ORIGINAL_TZ === undefined) delete process.env.TZ;
  else process.env.TZ = ORIGINAL_TZ;
});

// Фикстура — как в дефекте Веры: summary.loops = returned + замечания = 35,
// настоящих возвратов 15. Плитка обязана показывать 15.
function mountSummary(over: Partial<FeatureClosedSummaryProps> = {}) {
  const props: FeatureClosedSummaryProps = {
    summary: { mergedAt: '2026-11-10T22:00:00Z', durationMinutes: 200, tasks: 9, steps: 7, loops: 35 },
    verdicts: { accepted: 5, acceptedWithRemarks: 20, returned: 15, withdrawn: 0, loops: 35 },
    tailsCount: 1,
    showCloseHint: true,
    ...over,
  };
  return mount(createElement(FeatureClosedSummary, props));
}

const textOf = (n: unknown) => textNodes(n as never).join('');

describe('FeatureClosedSummary', () => {
  it('смерженная: строка «Смержена … · шла …» и ровно три плитки', () => {
    const { container } = mountSummary();
    const line = textOf(findNodeByAttr(container, 'data-testid', 'closed-summary-line'));
    expect(line).toBe('Смержена 11 ноября · шла 3 ч 20 мин');
    for (const k of ['steps', 'loops', 'tails']) {
      expect(findNodeByAttr(container, 'data-testid', `closed-tile-${k}`)).not.toBeNull();
    }
    // Метки «шагов»/«возвратов» склоняются по числу: 7 → «шагов», 15 → «возвратов»;
    // «открыто после мержа» — неизменяемая подпись (FP-R3).
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-steps'))).toBe('7шагов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-loops'))).toBe('15возвратов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails'))).toBe('1открыто после мержа');
    const tiles = findNodeByAttr(container, 'data-testid', 'closed-summary-tiles')!;
    expect(tiles.childNodes.length).toBe(3);
  });

  it('initialEtaMinutes задан — «обещали ≈ …»; не задан — слова нет', () => {
    const withEta = mountSummary({ initialEtaMinutes: 120 });
    expect(textOf(findNodeByAttr(withEta.container, 'data-testid', 'closed-summary-line')))
      .toBe('Смержена 11 ноября · шла 3 ч 20 мин, обещали ≈ 2 ч');
    uninstallFakeDom(); installFakeDom();
    const without = mountSummary({ initialEtaMinutes: null });
    expect(textOf(findNodeByAttr(without.container, 'data-testid', 'closed-summary-line'))).not.toContain('обещали');
  });

  it('без даты мержа — текст CLOSED_NO_MERGE_STATUS', () => {
    const { container } = mountSummary({
      summary: { mergedAt: null, durationMinutes: 45, tasks: 3, steps: 2, loops: 0 },
    });
    const line = textOf(findNodeByAttr(container, 'data-testid', 'closed-summary-line'));
    expect(line.toLowerCase().startsWith(CLOSED_NO_MERGE_STATUS)).toBe(true);
    expect(line).toContain('шла 45 мин');
    expect(line).not.toContain('Смержена');
  });

  it('сетка: мобила — 2 колонки, десктоп — 3', () => {
    const m = mountSummary({ isMobile: true });
    const grid = findNodeByAttr(m.container, 'data-testid', 'closed-summary-tiles')!;
    expect(grid.style.gridTemplateColumns).toBe('repeat(2, minmax(0, 1fr))');
    uninstallFakeDom(); installFakeDom();
    const d = mountSummary();
    const gridD = findNodeByAttr(d.container, 'data-testid', 'closed-summary-tiles')!;
    expect(gridD.style.gridTemplateColumns).toBe('repeat(3, minmax(0, 1fr))');
  });

  it('followUp: «Доработки: N из M» под строкой итога; итог и плитки не меняются', () => {
    // Базовый рендер без followUp — эталон строки итога и плиток.
    const base = mountSummary();
    const baseLine = textOf(findNodeByAttr(base.container, 'data-testid', 'closed-summary-line'));
    uninstallFakeDom(); installFakeDom();
    const { container } = mountSummary({ followUp: { total: 2, accepted: 2 } });
    const followup = findNodeByAttr(container, 'data-testid', 'closed-summary-followup');
    expect(followup).not.toBeNull();
    expect(textOf(followup!)).toBe('Доработки: 2 из 2');
    // Порядок: строка сразу после итога, до плиток.
    const root = findNodeByAttr(container, 'data-testid', 'closed-summary')!;
    const idxOf = (id: string) => root.childNodes.findIndex((n) => n.getAttribute('data-testid') === id);
    const idxLine = idxOf('closed-summary-line');
    const idxHint = idxOf('closed-summary-close-hint');
    const idxFollow = idxOf('closed-summary-followup');
    const idxTiles = idxOf('closed-summary-tiles');
    // Подсказка «Закройте…» — сразу под итогом (FS-AC3b), доработки — после неё.
    expect(idxHint).toBe(idxLine + 1);
    expect(idxFollow).toBe(idxHint + 1);
    expect(idxTiles).toBe(idxFollow + 1);
    // Строка итога и числа плиток — ровно как без followUp.
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-summary-line'))).toBe(baseLine);
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-steps'))).toBe('7шагов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-loops'))).toBe('15возвратов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails'))).toBe('1открыто после мержа');
    // «план расширен» в итогах не показываем.
    expect(textOf(container)).not.toContain('план расширен');
    uninstallFakeDom(); installFakeDom();
    // Числа без склонений: 1 из 3.
    const one = mountSummary({ followUp: { total: 3, accepted: 1 } });
    expect(textOf(findNodeByAttr(one.container, 'data-testid', 'closed-summary-followup')!)).toBe('Доработки: 1 из 3');
  });

  it('followUp=null и total=0 — строки нет', () => {
    const none = mountSummary();
    expect(findNodeByAttr(none.container, 'data-testid', 'closed-summary-followup')).toBeNull();
    uninstallFakeDom(); installFakeDom();
    const zero = mountSummary({ followUp: { total: 0, accepted: 0 } });
    expect(findNodeByAttr(zero.container, 'data-testid', 'closed-summary-followup')).toBeNull();
  });

  // Решение Андрея 11.11: после мержа фича остаётся «смержена», пока человек сам не
  // нажмёт «Закрыть» (FeaturePageHeader) — строка зовёт к этому решению.
  // Мутация «убрать строку подсказки» краснит этот тест.
  it('merged: «Закройте фичу, когда доработки не нужны» сразу под строкой итога (FS-AC3b)', () => {
    const { container } = mountSummary();
    const hint = findNodeByAttr(container, 'data-testid', 'closed-summary-close-hint');
    expect(hint).not.toBeNull();
    expect(textOf(hint!)).toBe('Закройте фичу, когда доработки не нужны');
    // Без слова «Смержена» в начале: тег состояния уже его говорит (минор ревью).
    expect(textOf(hint!)).not.toContain('Смержена');
    const root = findNodeByAttr(container, 'data-testid', 'closed-summary')!;
    const idxOf = (id: string) => root.childNodes.findIndex((n) => n.getAttribute('data-testid') === id);
    expect(idxOf('closed-summary-close-hint')).toBe(idxOf('closed-summary-line') + 1);
  });

  // Закрытая после мержа: бэк оставляет валидный Summary.MergedAt (FeatureProjector),
  // состояние же 'closed' — родитель передаёт showCloseHint=false. Мутация
  // «убрать showCloseHint из условия» краснит этот тест.
  it('closed-after-merge (mergedAt валидный, showCloseHint=false): подсказки нет, итог «Смержена …» остаётся', () => {
    const { container } = mountSummary({ showCloseHint: false });
    expect(findNodeByAttr(container, 'data-testid', 'closed-summary-close-hint')).toBeNull();
    // Строка итога не зависит от состояния: дата мержа валидна — «Смержена» остаётся.
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-summary-line')))
      .toBe('Смержена 11 ноября · шла 3 ч 20 мин');
    // (строка итога сверяется целиком — не «содержит», а ровно как у merged)
  });

  it('closed (mergedAt null или мусор) — подсказки нет', () => {
    const closed = mountSummary({ summary: { mergedAt: null, durationMinutes: 45, tasks: 3, steps: 2, loops: 0 } });
    expect(findNodeByAttr(closed.container, 'data-testid', 'closed-summary-close-hint')).toBeNull();
    uninstallFakeDom(); installFakeDom();
    const junk = mountSummary({ summary: { mergedAt: 'не дата', durationMinutes: 45, tasks: 3, steps: 2, loops: 0 } });
    expect(findNodeByAttr(junk.container, 'data-testid', 'closed-summary-close-hint')).toBeNull();
    // Невалидная дата остаётся «закрытой без мержа» и в строке итога — один критерий на всё.
    expect(textOf(findNodeByAttr(junk.container, 'data-testid', 'closed-summary-line'))).not.toContain('Смержена');
  });

  it('«Разбор ›» — только при onOpenReview, клик вызывает его', () => {
    const none = mountSummary();
    expect(findNodeByAttr(none.container, 'data-testid', 'closed-summary-review')).toBeNull();
    uninstallFakeDom(); installFakeDom();
    const onOpenReview = vi.fn();
    const { container } = mountSummary({ onOpenReview });
    const btn = findNodeByAttr(container, 'data-testid', 'closed-summary-review')!;
    expect(textOf(btn)).toContain('Разбор');
    (reactProps(btn)!.onClick as () => void)();
    expect(onOpenReview).toHaveBeenCalledTimes(1);
  });
});

// FP-H1 (дефект Веры e128aac4): одно слово — одна величина на странице.
describe('«Возвраты» в герое — только настоящие (FP-H1)', () => {
  it('вердикты {returned 15, замечания 20}: плитка «15 возвратов», не «35» (сумма loops)', () => {
    const { container } = mountSummary();
    // Плитка — ровно verdicts.returned; summary.loops = 35 в DOM плиток нет.
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-loops')!)).toBe('15возвратов');
    const tiles = findNodeByAttr(container, 'data-testid', 'closed-summary-tiles')!;
    expect(textOf(tiles)).not.toContain('35');
  });

  it('замечаний в герое нет: ни числа, ни слова «замечания»', () => {
    const { container } = mountSummary();
    expect(textOf(container)).not.toContain('замечани');
    // 20 — только из вердиктов; в DOM числа 20 нет (duration 200 → «3 ч 20 мин» — не число вердиктов).
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-summary-tiles')!)).not.toContain('20');
  });

  it('без verdicts (старый ответ): плитки возвратов нет, герой не падает', () => {
    const { container } = mountSummary({ verdicts: null });
    expect(findNodeByAttr(container, 'data-testid', 'closed-tile-loops')).toBeNull();
    const tiles = findNodeByAttr(container, 'data-testid', 'closed-summary-tiles')!;
    // Остальные две плитки — на месте.
    expect(tiles.childNodes.length).toBe(2);
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-steps')!)).toBe('7шагов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails')!)).toBe('1открыто после мержа');
  });

  it('«открыто после мержа», не «хвосты» и не «доработки»: слова «хвост» в герое нет; подпись не склоняется (FP-R3)', () => {
    const { container } = mountSummary({ tailsCount: 5 });
    expect(textOf(container)).not.toContain('хвост');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails')!)).toBe('5открыто после мержа');
    uninstallFakeDom(); installFakeDom();
    // Неизменяемая подпись: одно и то же при 0 и при 5 (FP-R3: «доработки» — имя followUp).
    const zero = mountSummary({ tailsCount: 0 });
    expect(textOf(findNodeByAttr(zero.container, 'data-testid', 'closed-tile-tails')!)).toBe('0открыто после мержа');
  });
});

// FP-R3 (финал Александра d9373bd8): под одним именем «доработки после мержа» стояло два
// разных числа: postMergeTails (плитка героя) и followUp («Доработки: A из T»). Теперь это
// два разных слова. Мутация «вернуть «доработок после мержа» в плитке героя» краснит этот тест.
describe('FP-R3: «открыто после мержа» и «Доработки: A из T» — два слова для двух величин', () => {
  it('postMergeTails 0, followUp 2 из 2: плитка «0 открыто после мержа» и строка «Доработки: 2 из 2»', () => {
    const { container } = mountSummary({ tailsCount: 0, followUp: { total: 2, accepted: 2 } });
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails')!)).toBe('0открыто после мержа');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-summary-followup')!)).toBe('Доработки: 2 из 2');
    // Прежнего имени плитки нигде в герое не остаётся (ни в одном склонении).
    const whole = textOf(container);
    expect(whole).not.toContain('доработка после мержа');
    expect(whole).not.toContain('доработки после мержа');
    expect(whole).not.toContain('доработок после мержа');
  });
});
