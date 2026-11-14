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
    expect(g.groups).toHaveLength(1);
    expect(g.groups[0].x).toBeLessThan(n['step-back'].x);
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

  // Дефект G7: узел обязан нести имя этапа (Title) — иначе qa-plan и qa дают две
  // неотличимые «Проверки» (тот же порядок имени, что nodeTitle в FeatureProcessGraph).
  it('узел несёт имя этапа (Title); без имени — null; крающие пробелы срезаны', () => {
    const titled = parallelCycle().map(s =>
      s.id === 'qa-plan' ? { ...s, title: '  Проверка плана ' } : s.id === 'qa' ? { ...s, title: 'Проверка' } : s);
    const n = Object.fromEntries(layoutGraph(titled, false).nodes.map(x => [x.id, x]));
    expect(n['qa-plan'].title).toBe('Проверка плана');
    expect(n['qa'].title).toBe('Проверка');
    expect(n['step-back'].title).toBeNull();
    expect(n.plan.title).toBeNull();
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

describe('подпись роли в превью', () => {
  it('cut в превью называется «Разрез», как в ленте', async () => {
    const src = await import('node:fs').then(f => f.readFileSync(new URL('./WorkflowGraphPreview.tsx', import.meta.url), 'utf8'));
    expect(src).toContain('STAGE_ROLE_LABEL[n.role]');
    expect(src).not.toContain('Нарезка');
  });
});

describe('граф: узлы, замок, дуга, снятые', () => {
  it('этап presence=off не рисуется, потомок наследует его родителя', () => {
    const g = layoutGraph([
      { role: 'plan', presence: 'on' },
      { role: 'design', presence: 'off', after: ['plan'] },
      { role: 'review', presence: 'on', after: ['design'] },
    ], false);
    expect(g.nodes.map(n => n.id)).toEqual(['plan', 'review']);
    expect(g.edges.map(e => `${e.from}>${e.to}`)).toEqual(['plan>review']);
  });

  it('ребро через уровень — far, путь уходит под граф', () => {
    const g = layoutGraph([
      { role: 'plan', presence: 'on' },
      { role: 'step', presence: 'on', lane: 'back', after: ['plan'] },
      { role: 'review', presence: 'on', after: ['step', 'plan'] },
    ], false);
    // plan→review транзитивно лишнее; настоящая дуга — через колонку без промежуточного предка:
    const far = layoutGraph([
      { role: 'plan', presence: 'on' },
      { role: 'step', presence: 'on', lane: 'back', after: ['plan'] },
      { role: 'qa', presence: 'on', id: 'qa-x', after: ['plan'] },
      { role: 'final', presence: 'on', after: ['step'] },
      { role: 'merge', presence: 'on', after: ['final', 'qa-x'] },
    ], false);
    expect(g.edges.some(e => e.far)).toBe(false);
    const e = far.edges.find(x => x.from === 'qa-x');
    expect(e?.far).toBe(true);
    const maxY = Math.max(...far.nodes.map(n => n.y + far.nodeH));
    expect(far.height).toBeGreaterThanOrEqual(maxY + 18);
    expect(far.height - layoutGraph(far.nodes.length ? [
      { role: 'plan', presence: 'on' },
      { role: 'step', presence: 'on', lane: 'back', after: ['plan'] },
      { role: 'qa', presence: 'on', id: 'qa-x', after: ['plan'] },
      { role: 'final', presence: 'on', after: ['step', 'qa-x'] },
      { role: 'merge', presence: 'on', after: ['final'] },
    ] : [], false).height).toBeGreaterThanOrEqual(18);
  });

  it('узел несёт замок для несъёмной роли и ключ строки ленты', () => {
    const g = layoutGraph([
      { role: 'plan', presence: 'on' },
      { role: 'qa', presence: 'optional' },
    ], false);
    const n = Object.fromEntries(g.nodes.map(x => [x.id, x]));
    expect(n.plan.locked).toBe(true);
    expect(n.qa.locked).toBe(false);
    expect(n.qa.key).toBe('qa');
  });
});

describe('WorkflowGraphPreview — разметка', () => {
  const src = () => import('node:fs').then(f => f.readFileSync(new URL('./WorkflowGraphPreview.tsx', import.meta.url), 'utf8'));
  it('узел — кнопка, клик зовёт onSelectStage, подпись «Маршрут, который увидит фича»', async () => {
    const t = await src();
    expect(t).toContain("onClick={() => onSelectStage?.(n.key)}");
    expect(t).toContain('Маршрут, который увидит фича');
    expect(t).not.toContain('Схема маршрута');
  });
  it('редактор передаёт графу тот же selectedStageId и setSelectedStageId', async () => {
    const t = await import('node:fs').then(f => f.readFileSync(new URL('./WorkflowTemplatesEditor.tsx', import.meta.url), 'utf8'));
    expect(t).toMatch(/<WorkflowGraphPreview[\s\S]*?onSelectStage=\{setSelectedStageId\}/);
  });
});
