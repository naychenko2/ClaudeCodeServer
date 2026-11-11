// «Итоги» закрытой фичи (FS-FI). Рендер-тесты на fakeDom:
//   · смерженная — «Смержена … · шла …», ровно 3 плитки, четвёртой «точности срока» нет;
//   · initialEtaMinutes задан — «обещали ≈ …», не задан — слова «обещали» нет;
//   · без даты мержа — текст CLOSED_NO_MERGE_STATUS;
//   · isMobile — сетка 2 колонки (minmax), десктоп — 3;
//   · «Разбор ›» есть только при onOpenReview и вызывает его;
//   · followUp (FS-FVp) — «Доработки: N из M» под строкой итога; при null/total=0 — строки нет;
//     строка итога и плитки не меняются; «план расширен» в DOM 0.
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

function mountSummary(over: Partial<FeatureClosedSummaryProps> = {}) {
  const props: FeatureClosedSummaryProps = {
    summary: { mergedAt: '2026-11-10T22:00:00Z', durationMinutes: 200, tasks: 9, steps: 7, loops: 2 },
    tailsCount: 1,
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
    // Метки плиток склоняются по числу: 7 → «шагов», 2 → «возврата», 1 → «хвост».
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-steps'))).toBe('7шагов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-loops'))).toBe('2возврата');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails'))).toBe('1хвост');
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
    const idxFollow = idxOf('closed-summary-followup');
    const idxTiles = idxOf('closed-summary-tiles');
    expect(idxFollow).toBe(idxLine + 1);
    expect(idxTiles).toBe(idxFollow + 1);
    // Строка итога и числа плиток — ровно как без followUp.
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-summary-line'))).toBe(baseLine);
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-steps'))).toBe('7шагов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-loops'))).toBe('2возврата');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails'))).toBe('1хвост');
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
