// Полоса трекера (общий рендер В2/С1/П2/раскрытия) — подсветка «в master» как ДОЛЯ
// принятой части (дизайн-ревью F2, п. 1): раньше единственный flex-ребёнок сегмента
// растягивался на весь сегмент («3 из 7» рисовалось как «7 из 7»). Окружение node:
// renderToStaticMarkup, модель — общий featureTruthBar.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { FeatureTrackerBar } from './FeatureTrackerBar';
import { featureTruthBar } from './featureTruthModel';
import type { StepProgress } from '../../../lib/featureProgress';

function sp(over: Partial<StepProgress> = {}): StepProgress {
  return { planState: 'partial', total: 17, accepted: 7, review: 1, work: 9, notCreated: 0, withdrawn: 0, unlinkedReworks: 0, ...over };
}

function renderBar(inMaster: number): string {
  const bar = featureTruthBar(sp(), inMaster)!;
  return renderToStaticMarkup(createElement(FeatureTrackerBar, { bar, testId: 'tb' }));
}

const styleOf = (html: string, testId: string): string | undefined =>
  new RegExp(`data-testid="${testId}"[^>]*style="([^"]*)"`).exec(html)?.[1];

describe('FeatureTrackerBar: подсветка «в master» — доля принятой части', () => {
  it('«3 из 7»: подсветка flex 3, остаток сегмента flex 4 — доля, не весь сегмент', () => {
    const html = renderBar(3);
    const master = styleOf(html, 'feature-bar-master');
    expect(master, 'узел подсветки не найден').toBeDefined();
    expect(master).toContain('flex:3');
    const rest = styleOf(html, 'feature-bar-master-rest');
    expect(rest, 'остаток принятой части не найден').toBeDefined();
    expect(rest).toContain('flex:4');
  });

  it('тон подсветки — штриховка (successText/successBg), а не сплошной цвет близкий к «принято»', () => {
    const master = styleOf(renderBar(3), 'feature-bar-master');
    expect(master).toBeDefined();
    expect(master).toContain('repeating-linear-gradient');
    expect(master).toContain('var(--c-success-text)');
    expect(master).not.toContain('background:var(--c-success-text)');
  });

  it('inMaster ≥ accepted (10 при 7): подсветка на всю принятую часть, остатка нет', () => {
    const html = renderBar(10);
    expect(styleOf(html, 'feature-bar-master')).toContain('flex:7');
    expect(html).not.toContain('feature-bar-master-rest');
  });

  it('нет мержей (inMaster 0): подсветки нет вовсе', () => {
    expect(renderBar(0)).not.toContain('feature-bar-master');
  });
});
