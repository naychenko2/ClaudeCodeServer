import { describe, expect, it } from 'vitest';
import { afterOptions, normalizeStages, previewColumns, removeStage, renameStageId } from './workflowGraphModel';
import type { WorkflowStage } from './types';

// Форма тестов — третий встроенный пресет parallel-cycle (разрез 2 §1.10):
// cut → design → {plan, qa-plan}, plan → {step-back, step-front} → review,
// {review, qa-plan} → qa → final → merge. Список топологический; неявный after
// ( = предыдущий в списке) оставлен там, где пресет именно так и связывает,
// остальные рёбра — явные.
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

// Линейный шаблон: id = роль, after — неявные (старые шаблоны, full-cycle/fast-track).
const linear = (): WorkflowStage[] => [
  { role: 'cut', presence: 'on' },
  { role: 'design', presence: 'on' },
  { role: 'merge', presence: 'on' },
];

describe('normalizeStages', () => {
  it('линейный шаблон: id = роль, after = предыдущий, у первого — []', () => {
    const n = normalizeStages(linear());
    expect(n.map(s => s.id)).toEqual(['cut', 'design', 'merge']);
    expect(n.map(s => s.after)).toEqual([[], ['cut'], ['design']]);
  });

  it('явный after сохраняется, значения триммятся (как серверный Normalize)', () => {
    const n = normalizeStages([
      { role: ' cut ', presence: 'on' },
      { role: 'qa', presence: 'on', id: ' qa-plan ', after: [' cut ', ''] },
    ]);
    expect(n[0].id).toBe('cut');
    expect(n[1].id).toBe('qa-plan');
    expect(n[1].after).toEqual(['cut', '']);
  });

  it('не меняет исходный список и идемпотентна', () => {
    const raw = parallelCycle();
    const once = normalizeStages(raw);
    expect(raw[3].after).toEqual(['design']); // исходная запись целая
    expect(raw[1].after).toBeUndefined();
    expect(normalizeStages(once)).toEqual(once);
  });
});

describe('afterOptions', () => {
  it('кандидаты — id этапов со строго меньшим индексом', () => {
    const cycle = parallelCycle();
    expect(afterOptions(cycle, 3)).toEqual(['cut', 'design', 'plan']);
    expect(afterOptions(cycle, 7)).toEqual(['cut', 'design', 'plan', 'qa-plan', 'step-back', 'step-front', 'review']);
  });

  it('первый этап и выход за список: пусто и все id соответственно', () => {
    const cycle = parallelCycle();
    expect(afterOptions(cycle, 0)).toEqual([]);
    expect(afterOptions(cycle, -1)).toEqual([]);
    expect(afterOptions(cycle, 99).length).toBe(cycle.length);
  });

  it('на линейном шаблоне без id кандидаты — роли (id после нормализации)', () => {
    expect(afterOptions(linear(), 2)).toEqual(['cut', 'design']);
  });
});

describe('renameStageId', () => {
  it('каскадом меняет id в after у всех, кто ссылался', () => {
    const r = renameStageId(parallelCycle(), 'plan', 'plan-v2');
    const byId = new Map(r.map(s => [s.id, s]));
    expect(byId.has('plan')).toBe(false);
    expect(byId.get('step-back')!.after).toEqual(['plan-v2']);
    expect(byId.get('step-front')!.after).toEqual(['plan-v2']);
    // несвязанные рёбра не трогаются
    expect(byId.get('qa-plan')!.after).toEqual(['design']);
    expect(byId.get('review')!.after).toEqual(['step-back', 'step-front']);
  });

  it('неявный after (предыдущий в списке) получает новый id', () => {
    const r = renameStageId(linear(), 'design', 'design-2');
    expect(r.map(s => s.id)).toEqual(['cut', 'design-2', 'merge']);
    expect(r[2].after).toEqual(['design-2']);
  });

  it('пустой/неизменившийся newId и неизвестный oldId — список как есть', () => {
    const cycle = parallelCycle();
    expect(renameStageId(cycle, 'plan', '')).toBe(cycle);
    expect(renameStageId(cycle, 'plan', 'plan')).toBe(cycle);
    expect(renameStageId(cycle, 'нет-такого', 'x')).toBe(cycle);
  });
});

describe('removeStage', () => {
  it('убирает этап и его id из after остальных; чужие рёбра целые', () => {
    const r = removeStage(parallelCycle(), 'step-front');
    expect(r.map(s => s.id)).toEqual(['cut', 'design', 'plan', 'qa-plan', 'step-back', 'review', 'qa', 'final', 'merge']);
    const byId = new Map(r.map(s => [s.id, s]));
    expect(byId.get('review')!.after).toEqual(['step-back']);
    expect(byId.get('qa')!.after).toEqual(['review', 'qa-plan']);
  });

  it('удаление этапа с неявной ссылкой: следующий остаётся без битого id', () => {
    const r = removeStage(linear(), 'design');
    expect(r.map(s => s.id)).toEqual(['cut', 'merge']);
    expect(r[1].after).toEqual([]); // был [design] — вычистился
  });

  it('неизвестного id нет — список как есть', () => {
    const cycle = parallelCycle();
    expect(removeStage(cycle, 'нет-такого')).toBe(cycle);
  });
});

describe('previewColumns', () => {
  it('parallel-cycle: ветки в одной колонке, qa правее review', () => {
    const c = previewColumns(parallelCycle());
    expect(c).toEqual({
      cut: 0, design: 1, plan: 2, 'qa-plan': 2, 'step-back': 3, 'step-front': 3,
      review: 4, qa: 5, final: 6, merge: 7,
    });
    expect(c['step-back']).toBe(c['step-front']); // параллельные — в одной колонке
    expect(c.qa).toBeGreaterThan(c.review); // схождение веток — правее обеих
  });

  it('линейный — колонка = позиция; ребро на неизвестный id колонки не даёт', () => {
    expect(previewColumns(linear())).toEqual({ cut: 0, design: 1, merge: 2 });
    expect(previewColumns([{ role: 'cut', presence: 'on', after: ['призрака'] }])).toEqual({ cut: 0 });
  });
});
