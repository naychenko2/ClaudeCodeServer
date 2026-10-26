// Тесты легенды мини-полоски: только присутствующие виды, подписи/цвета из
// FeatureStatsBits, «⫽» — только при разрыве, пустая легенда — null.
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FeatureBriefLegend } from './FeatureBriefLegend';
import { kindBackground, kindLabel, type SegmentKind } from './FeatureStatsBits';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes } from '../chat/__tests__/fakeDom';

beforeEach(() => installFakeDom());
afterEach(() => uninstallFakeDom());

function legend(kinds: SegmentKind[], hasBreak?: boolean) {
  return mount(createElement(FeatureBriefLegend, { kinds, hasBreak })).container;
}

const ALL: SegmentKind[] = ['work', 'waiting', 'idle', 'error', 'notWorking'];

describe('FeatureBriefLegend', () => {
  it('показывает только виды, что есть на полоске, в фиксированном порядке', () => {
    const c = legend(['idle', 'work', 'work']);
    expect(findNodeByAttr(c, 'data-testid', 'brief-legend-work')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-legend-idle')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-legend-waiting')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-legend-error')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-legend-notWorking')).toBeNull();
    expect(textNodes(c)).toEqual([kindLabel('work'), kindLabel('idle')]);
  });

  it('подпись и цвет квадрата — ровно kindLabel/kindBackground', () => {
    const c = legend(ALL);
    for (const k of ALL) {
      const item = findNodeByAttr(c, 'data-testid', `brief-legend-${k}`)!;
      expect(textNodes(item)).toEqual([kindLabel(k)]);
      const sw = item.childNodes[0] as unknown as { style: Record<string, string> };
      expect(sw.style.background).toBe(kindBackground(k));
    }
  });

  it('«⫽ — длинная пауза сжата» — только при hasBreak', () => {
    expect(findNodeByAttr(legend(['work']), 'data-testid', 'brief-legend-break')).toBeNull();
    const c = legend(['work'], true);
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-legend-break')!).join('')).toContain('⫽');
  });

  it('нет видов и нет разрыва — легенды нет', () => {
    expect(findNodeByAttr(legend([]), 'data-testid', 'brief-legend')).toBeNull();
  });
});
