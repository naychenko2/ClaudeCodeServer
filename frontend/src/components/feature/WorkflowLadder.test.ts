// Тесты лестницы маршрута (F02): колонки и «∥ параллельно», пунктирная far-дуга, чипы-чекбоксы
// (onToggle у съёмных, 🔒 с тултипом у несъёмных), «Снято» с возвратом, isNew, заглушка.
// Окружение node: mount (fakeDom), раскладки нет — дуга в DOM с пустым путём.

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { WorkflowLadder, LADDER_PLACEHOLDER, type WorkflowLadderProps } from './WorkflowLadder';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import type { WorkflowStage } from '../../features/workflowTemplates/types';

const st = (id: string, role: string, after: string[], over: Partial<WorkflowStage> = {}): WorkflowStage =>
  ({ id, role, after, presence: 'on', ...over });

// Параллельный цикл: cut → plan → [back, front, docs] → review → qa → final; docs → final — far.
const cycle: WorkflowStage[] = [
  st('cut', 'cut', []),
  st('plan', 'plan', ['cut']),
  st('qa-plan', 'qa', ['plan'], { presence: 'optional', title: 'Проверка плана' }),
  st('back', 'step', ['qa-plan'], { lane: 'back' }),
  st('front', 'step', ['qa-plan'], { lane: 'front' }),
  st('docs', 'design', ['qa-plan'], { presence: 'optional', title: 'Документация' }),
  st('review', 'review', ['back', 'front']),
  st('qa', 'qa', ['review']),
  st('final', 'final', ['qa', 'docs']),
];

const render = (over: Partial<WorkflowLadderProps> = {}) =>
  mount(createElement(WorkflowLadder, { stages: cycle, removedIds: [], isMobile: false, ...over }));

const countText = (c: ReturnType<typeof mount>['container'], text: string) =>
  textNodes(c).filter(t => t === text).length;

const countAll = (c: ReturnType<typeof mount>['container'], testid: string) => {
  let n = 0;
  const walk = (x: ReturnType<typeof mount>['container']) => {
    if (x.nodeType === 1 && x.getAttribute('data-testid') === testid) n++;
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c);
  return n;
};

describe('WorkflowLadder', () => {
  beforeEach(() => installFakeDom());
  afterEach(() => uninstallFakeDom());

  it('«Параллельный цикл»: «∥ параллельно» один раз, колонок ≥ 5, far-дуга пунктиром', () => {
    const { container: c } = render();
    expect(countText(c, '∥ параллельно')).toBe(1);
    expect(countAll(c, 'workflow-ladder-column')).toBeGreaterThanOrEqual(5);
    const arc = findNodeByAttr(c, 'data-testid', 'workflow-ladder-arc')!;
    expect(arc).not.toBeNull();
    expect(arc.getAttribute('stroke-dasharray')).toBe('3 4');
  });

  it('чип съёмного этапа вызывает onToggle; у step/final чекбокса нет, виден 🔒 с тултипом', () => {
    const onToggle = vi.fn();
    const { container: c } = render({ onToggle });
    const qaPlan = findNodeByAttr(c, 'data-ladder-chip', 'qa-plan')!;
    expect(qaPlan.nodeName.toLowerCase()).toBe('button');
    (reactProps(qaPlan)!.onClick as () => void)();
    expect(onToggle).toHaveBeenCalledWith('qa-plan');

    for (const id of ['back', 'final']) {
      const locked = findNodeByAttr(c, 'data-ladder-chip', id)!;
      expect(reactProps(locked)!.onClick).toBeUndefined();
      expect(locked.getAttribute('aria-disabled')).toBe('true');
      expect(locked.getAttribute('title')).toBe('Шаги, финал и слияние не снимаются');
      expect(textNodes(locked)).toContain('🔒');
    }
  });

  it('снятый этап — в «Снято:», клик возвращает', () => {
    const onToggle = vi.fn();
    const { container: c } = render({ removedIds: ['qa-plan'], onToggle });
    expect(textNodes(c)).toContain('Снято:');
    const removed = findNodeByAttr(findNodeByAttr(c, 'data-testid', 'workflow-ladder-removed')!, 'data-ladder-chip', 'qa-plan')!;
    expect(removed.getAttribute('aria-checked')).toBe('false');
    (reactProps(removed)!.onClick as () => void)();
    expect(onToggle).toHaveBeenCalledWith('qa-plan');
    // в самой лестнице снятого этапа нет
    expect(countAll(c, 'workflow-ladder-removed')).toBe(1);
  });

  it('isNew-этап: «новый» в aria-label и зелёная рамка', () => {
    const { container: c } = render({ newIds: ['docs'] });
    const chip = findNodeByAttr(c, 'data-ladder-chip', 'docs')!;
    expect(chip.getAttribute('aria-label')).toContain('новый');
    const pill = chip.childNodes[0] as unknown as { style: Record<string, string> };
    expect(pill.style.border).toContain('var(--c-success)');
  });

  it('тач-цель: minHeight ≥ 40 у чипа лестницы и у чипа возврата в «Снято»', () => {
    for (const isMobile of [false, true]) {
      const { container: c } = render({ removedIds: ['qa-plan'], isMobile });
      const inLadder = findNodeByAttr(c, 'data-ladder-chip', 'back')!;
      const back = findNodeByAttr(findNodeByAttr(c, 'data-testid', 'workflow-ladder-removed')!, 'data-ladder-chip', 'qa-plan')!;
      for (const n of [inLadder, back]) {
        const style = reactProps(n)!.style as { minHeight: number };
        expect(style.minHeight).toBeGreaterThanOrEqual(40);
      }
    }
  });

  it('readonly: съёмные чипы без onClick', () => {
    const { container: c } = render({ mode: 'readonly' });
    const chip = findNodeByAttr(c, 'data-ladder-chip', 'qa-plan')!;
    expect(reactProps(chip)!.onClick).toBeUndefined();
    expect(chip.getAttribute('aria-disabled')).toBe('true');
  });

  it('мобила: стрелки «↓», на десктопе «→»', () => {
    expect(textNodes(render({ isMobile: true }).container)).toContain('↓');
    expect(textNodes(render().container)).toContain('→');
  });

  it('без этапов — заглушка с текстом', () => {
    const { container: c } = render({ stages: null });
    expect(textNodes(c)).toContain(LADDER_PLACEHOLDER);
    expect(findNodeByAttr(c, 'data-testid', 'workflow-ladder-slot')).not.toBeNull();
  });
});
