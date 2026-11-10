// Вкладка «Ход» (FS-FH). Рендер-тесты на данных формы run.stages / brief.active / brief.next.
// Покрытие: прошлые этапы — ровно одна строка «… · готово»; текущий — строки «Сейчас» по числу
// active; кнопка «Все N шагов этапа» — только у prod и только со stepsSlot, клик монтирует
// слот, второй снимает; подпись склоняется (1 шаг / 2 шага / 5 шагов), без stepsTotal —
// «Все шаги этапа»; будущие и «Дальше»; клик по строке «Сейчас» → onOpenActive(taskId).
// Мутация (в отчёте): рисовать каждый прошлый этап отдельной строкой → красный тест сжатия.

import { act, createElement } from 'react';
import { describe, it, expect, afterEach } from 'vitest';
import { FeatureProgressTab } from './FeatureProgressTab';
import type { FeatureProgressTabProps } from './FeatureProgressTab';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureStage, StageKey, StageState } from '../../types/featureRun';
import type { FeatureBriefActive } from '../../lib/featurePage';

const NOW = Date.parse('2026-11-10T12:00:00Z');

function stage(key: StageKey, state: StageState): FeatureStage {
  return { key, state, passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false, executors: [] } as FeatureStage;
}
function act1(taskId: string, over: Partial<FeatureBriefActive> = {}): FeatureBriefActive {
  return { taskId, stage: 'prod', stepRef: 'Ш2', lane: 'front', personaId: 'p-kira', state: 'working', since: '2026-11-10T11:30:00Z', reason: null, ...over };
}

const roots: import('react-dom/client').Root[] = [];
function render(over: Partial<FeatureProgressTabProps> = {}) {
  installFakeDom();
  const { root, container } = mount(createElement(FeatureProgressTab, {
    stages: [stage('cut', 'done'), stage('design', 'done'), stage('plan', 'done'), stage('prod', 'active'), stage('review', 'pending'), stage('merge', 'pending')],
    next: [{ stage: 'review', personaId: 'p-gleb' }],
    active: [act1('t1'), act1('t2', { stepRef: 'Ш3', personaId: 'p-denis' })],
    nameOf: id => ({ 'p-kira': 'Кира', 'p-denis': 'Денис', 'p-gleb': 'Глеб' } as Record<string, string>)[id] ?? null,
    isMobile: false, nowMs: NOW,
    ...over,
  }));
  roots.push(root);
  return container;
}
function count(container: ReturnType<typeof render>, id: string): number {
  let n = 0;
  const walk = (x: { childNodes: unknown[]; getAttribute?: (a: string) => string | null }) => {
    if (x.getAttribute?.('data-testid') === id) n++;
    for (const c of x.childNodes) walk(c as typeof x);
  };
  walk(container as unknown as Parameters<typeof walk>[0]);
  return n;
}
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('Вкладка «Ход»', () => {
  it('3 прошлых этапа — одна строка «… · готово», не три', () => {
    const c = render();
    expect(count(c, 'progress-past')).toBe(1);
    const text = textNodes(findNodeByAttr(c, 'data-testid', 'progress-past')!).join('');
    expect(text).toContain('·');
    expect(text).toContain('готово');
    expect(text).toContain('Разрез');
    expect(text).toContain('План');
  });

  it('текущий этап: строки «Сейчас» по числу active, без обрезки', () => {
    const c = render({ active: [act1('t1'), act1('t2'), act1('t3'), act1('t4'), act1('t5')] });
    expect(count(c, 'brief-active-row')).toBe(5);
    expect(textNodes(c).join('')).toContain('Сейчас Кира делает Ш2');
  });

  it('кнопка «Все N шагов этапа»: только prod и только со stepsSlot; клик монтирует слот, второй снимает', () => {
    const slot = createElement('div', { 'data-testid': 'slot' }, 'шаги');
    const c = render({ stepsSlot: slot, stepsTotal: 7 });
    const btn = findNodeByAttr(c, 'data-testid', 'progress-steps-toggle')!;
    expect(btn).not.toBeNull();
    expect(textNodes(btn).join('')).toContain('Все 7 шагов этапа');
    expect(count(c, 'slot')).toBe(0);
    act(() => { (reactProps(btn)!.onClick as () => void)(); });
    expect(count(c, 'slot')).toBe(1);
    act(() => { (reactProps(btn)!.onClick as () => void)(); });
    expect(count(c, 'slot')).toBe(0);
  });

  it('склонение подписи: 1 шаг / 2 шага / 5 шагов; без stepsTotal — «Все шаги этапа»', () => {
    const slot = createElement('div', null, 'шаги');
    for (const [n, word] of [[1, 'шаг'], [2, 'шага'], [5, 'шагов']] as const) {
      const c = render({ stepsSlot: slot, stepsTotal: n });
      const btn = findNodeByAttr(c, 'data-testid', 'progress-steps-toggle')!;
      expect(textNodes(btn).join('')).toContain(`Все ${n} ${word} этапа`);
    }
    const c = render({ stepsSlot: slot });
    const btn = findNodeByAttr(c, 'data-testid', 'progress-steps-toggle')!;
    expect(textNodes(btn).join('')).toContain('Все шаги этапа');
  });

  it('нет stepsSlot — кнопки нет; текущий не prod (review) — кнопки нет', () => {
    expect(count(render(), 'progress-steps-toggle')).toBe(0);
    const slot = createElement('div', null, 'шаги');
    const c = render({
      stepsSlot: slot,
      stages: [stage('cut', 'done'), stage('prod', 'done'), stage('review', 'active'), stage('merge', 'pending')],
    });
    expect(count(c, 'progress-steps-toggle')).toBe(0);
  });

  it('будущие этапы — строками, «Дальше» — первый пункт next', () => {
    const c = render();
    expect(count(c, 'progress-future')).toBe(2);
    const next = textNodes(findNodeByAttr(c, 'data-testid', 'progress-next')!).join('');
    expect(next).toContain('Дальше');
    expect(next).toContain('Глеб');
    expect(count(render({ next: [] }), 'progress-next')).toBe(0);
  });

  it('skipped-этап не показывается', () => {
    const c = render({ stages: [stage('cut', 'done'), stage('design', 'skipped'), stage('prod', 'active')] });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'progress-past')!).join('')).not.toContain('Дизайн');
  });

  it('клик по строке «Сейчас» → onOpenActive(taskId)', () => {
    const opened: string[] = [];
    const c = render({ onOpenActive: id => opened.push(id) });
    const row = findNodeByAttr(c, 'data-testid', 'brief-active-row')!;
    act(() => { (reactProps(row)!.onClick as () => void)(); });
    expect(opened).toEqual(['t1']);
  });
});
