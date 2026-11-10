// «Итоги» закрытой фичи (FS-FI). Рендер-тесты на fakeDom:
//   · смерженная — «Смержена … · шла …», ровно 3 плитки, четвёртой «точности срока» нет;
//   · initialEtaMinutes задан — «обещали ≈ …», не задан — слова «обещали» нет;
//   · без даты мержа — текст CLOSED_NO_MERGE_STATUS;
//   · isMobile — сетка 2 колонки, десктоп — 3;
//   · «Разбор ›» есть только при onOpenReview и вызывает его.

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureClosedSummary, type FeatureClosedSummaryProps } from './FeatureClosedSummary';
import { CLOSED_NO_MERGE_STATUS } from '../../lib/chatGroups';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';

beforeEach(() => installFakeDom());
afterEach(() => uninstallFakeDom());

function mountSummary(over: Partial<FeatureClosedSummaryProps> = {}) {
  const props: FeatureClosedSummaryProps = {
    summary: { mergedAt: '2026-11-10T12:00:00Z', durationMinutes: 200, tasks: 9, steps: 7, loops: 2 },
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
    expect(line).toBe('Смержена 10 ноября · шла 3 ч 20 мин');
    for (const k of ['steps', 'loops', 'tails']) {
      expect(findNodeByAttr(container, 'data-testid', `closed-tile-${k}`)).not.toBeNull();
    }
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-steps'))).toBe('7шагов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-loops'))).toBe('2возвратов');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'closed-tile-tails'))).toBe('1хвосты');
    const tiles = findNodeByAttr(container, 'data-testid', 'closed-summary-tiles')!;
    expect(tiles.childNodes.length).toBe(3);
  });

  it('initialEtaMinutes задан — «обещали ≈ …»; не задан — слова нет', () => {
    const withEta = mountSummary({ initialEtaMinutes: 120 });
    expect(textOf(findNodeByAttr(withEta.container, 'data-testid', 'closed-summary-line')))
      .toBe('Смержена 10 ноября · шла 3 ч 20 мин, обещали ≈ 2 ч');
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
    expect(grid.style.gridTemplateColumns).toBe('repeat(2, 1fr)');
    uninstallFakeDom(); installFakeDom();
    const d = mountSummary();
    const gridD = findNodeByAttr(d.container, 'data-testid', 'closed-summary-tiles')!;
    expect(gridD.style.gridTemplateColumns).toBe('repeat(3, 1fr)');
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
