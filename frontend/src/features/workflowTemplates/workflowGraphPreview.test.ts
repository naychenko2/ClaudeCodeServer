import { describe, expect, it } from 'vitest';
import { layoutGraph } from './WorkflowGraphPreview';
import { normalizeStages } from './workflowGraphModel';
import type { WorkflowStage } from './types';

// Форма встроенного пресета parallel-cycle (разрез 2 §1.10).
const parallelCycle = (): WorkflowStage[] => [
  { role: 'cut', presence: 'on' },
  { role: 'design', presence: 'on' },
  { role: 'plan', presence: 'on' },
  { role: 'qa', presence: 'on', id: 'qa-plan', after: ['design'] },
  { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
  { role: 'step', presence: 'on', id: 'step-front', lane: 'front', after: ['plan'] },
  { role: 'review', presence: 'on', after: ['step-back', 'step-front'] },
  { role: 'qa', presence: 'on', id: 'qa', after: ['review', 'qa-plan'] },
  { role: 'final', presence: 'on' },
  { role: 'merge', presence: 'on' },
];

// plan → {step-back, step-front} → review: средняя колонка параллельна.
const diamond = (): WorkflowStage[] => [
  { role: 'plan', presence: 'on' },
  { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
  { role: 'step', presence: 'on', id: 'step-front', lane: 'front', after: ['plan'] },
  { role: 'review', presence: 'on', after: ['step-back', 'step-front'] },
];

describe('layoutGraph', () => {
  it('широкая раскладка: колонки слева направо, параллельные этапы друг под другом, 4 ребра', () => {
    const g = layoutGraph(diamond(), false);
    const n = Object.fromEntries(g.nodes.map(x => [x.id, x]));
    expect(n.plan.x).toBeLessThan(n['step-back'].x);
    expect(n['step-back'].x).toBe(n['step-front'].x);
    expect(n['step-front'].y).toBeGreaterThan(n['step-back'].y);
    expect(n['step-back'].x).toBeLessThan(n.review.x);
    expect(g.edges).toHaveLength(4);
    expect(g.groups).toHaveLength(0);
  });

  it('узкая раскладка: колонки сверху вниз, у параллельной — рамка', () => {
    const g = layoutGraph(diamond(), true);
    const n = Object.fromEntries(g.nodes.map(x => [x.id, x]));
    expect(n.plan.y).toBeLessThan(n['step-back'].y);
    expect(n['step-front'].y).toBeGreaterThan(n['step-back'].y);
    expect(n.review.y).toBeGreaterThan(n['step-front'].y);
    expect(new Set(g.nodes.map(x => x.x)).size).toBe(1);
    expect(g.groups).toHaveLength(1);
  });

  it('висящая ссылка и ссылка назад рёбер не дают', () => {
    const g = layoutGraph([
      { role: 'plan', presence: 'on', after: ['ghost'] },
      { role: 'step', presence: 'on', after: ['review'] },
      { role: 'review', presence: 'on', after: ['step'] },
    ], false);
    // step→review идёт вправо (колонка 0 → 1); review→step назад — не рисуется.
    expect(g.edges.map(e => `${e.from}>${e.to}`)).toEqual(['step>review']);
  });

  it('пустой список — пустая раскладка', () => {
    expect(layoutGraph([], false).nodes).toEqual([]);
  });

  describe('parallel-cycle', () => {
    const edgesExpected = normalizeStages(parallelCycle()).reduce((n, s) => n + (s.after ?? []).length, 0);

    it('step-back и step-front — в одной колонке; рёбер столько же, сколько элементов в after', () => {
      const g = layoutGraph(parallelCycle(), false);
      const n = Object.fromEntries(g.nodes.map(x => [x.id, x]));
      expect(n['step-back'].col).toBe(n['step-front'].col);
      expect(n['step-back'].x).toBe(n['step-front'].x);
      expect(g.edges).toHaveLength(edgesExpected);
      expect(edgesExpected).toBe(11);
    });

    it('узкая раскладка: колонки вертикально, каждая колонка из двух этапов — в рамке «параллельно»', () => {
      const g = layoutGraph(parallelCycle(), true);
      expect(new Set(g.nodes.map(x => x.x)).size).toBe(1);
      const n = Object.fromEntries(g.nodes.map(x => [x.id, x]));
      expect(n.plan.y).toBeLessThan(n['step-back'].y);
      expect(n['step-front'].y).toBeLessThan(n.review.y);
      // Две колонки по два этапа: {plan, qa-plan} и {step-back, step-front}.
      expect(g.groups).toHaveLength(2);
      const inGroup = (id: string) => g.groups.some(b => n[id].y >= b.y && n[id].y + g.nodeH <= b.y + b.h);
      expect(['step-back', 'step-front', 'plan', 'qa-plan'].every(inGroup)).toBe(true);
      expect(inGroup('review')).toBe(false);
      expect(g.edges).toHaveLength(edgesExpected);
    });
  });
});
