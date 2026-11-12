import { describe, expect, it } from 'vitest';
import { layoutGraph } from './WorkflowGraphPreview';
import type { WorkflowStage } from './types';

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
});
