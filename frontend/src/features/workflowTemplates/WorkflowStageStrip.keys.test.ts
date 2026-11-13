import { describe, expect, it, vi } from 'vitest';
import { isValidElement, type ReactElement, type ReactNode } from 'react';
import type { WorkflowStage } from './types';
import { moveStage } from './workflowGraphModel';
import { stageKeys } from './workflowStripModel';

// Хуки вне рендера — заглушки: лента вызывается как функция, а дерево элементов обходится вручную.
vi.mock('react', async orig => {
  const actual = await orig<typeof import('react')>();
  return { ...actual, useState: (v: unknown) => [v, () => {}], useRef: (v: unknown) => ({ current: v }), useEffect: () => {} };
});
vi.mock('./WorkflowStageRow', () => ({ STAGE_ROLE_LABEL: { cut: 'Разрез' }, WorkflowStageRow: () => null }));

const { WorkflowStageStrip } = await import('./WorkflowStageStrip');
const { WorkflowStageRow } = await import('./WorkflowStageRow');

const noop = () => {};
const stages = (): WorkflowStage[] => [
  { role: 'cut', presence: 'on' },
  { role: 'plan', presence: 'on', after: ['cut'] },
  { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
  { role: 'step', presence: 'on', id: 'step-front', lane: 'front', after: ['plan'] },
];

// Ключи строк этапов в порядке отрисовки, с индексом этапа из пропа.
function rowKeys(list: WorkflowStage[]): Record<string, number> {
  const tree = WorkflowStageStrip({
    stages: list, readOnly: false, specialties: [], personas: [], mcpChoices: [], errorsFor: () => [],
    onStagesChange: noop, onStageChange: noop, onRenameStage: noop, onRemoveStage: noop,
  });
  const out: Record<string, number> = {};
  // Ключ строки — ключ ближайшей ключеванной обёртки (сама строка лежит в ней без ключа).
  const walk = (n: ReactNode, key: string | null) => {
    if (Array.isArray(n)) { n.forEach(c => walk(c, key)); return; }
    if (!isValidElement(n)) return;
    const el = n as ReactElement<{ index?: number; children?: ReactNode }>;
    if (el.type === WorkflowStageRow) out[String(el.key ?? key)] = el.props.index as number;
    walk(el.props.children, el.key !== null ? String(el.key) : key);
  };
  walk(tree, null);
  return out;
}

// Путь предков каждой строки (тип/ключ каждого узла от корня до строки, включая обёртку строки).
function rowParents(list: WorkflowStage[]): Record<string, string> {
  const tree = WorkflowStageStrip({
    stages: list, readOnly: false, specialties: [], personas: [], mcpChoices: [], errorsFor: () => [],
    onStagesChange: noop, onStageChange: noop, onRenameStage: noop, onRemoveStage: noop,
  });
  const out: Record<string, string> = {};
  const walk = (n: ReactNode, path: string[]) => {
    if (Array.isArray(n)) { n.forEach(c => walk(c, path)); return; }
    if (!isValidElement(n)) return;
    const el = n as ReactElement<{ stage?: WorkflowStage; children?: ReactNode }>;
    if (el.type === WorkflowStageRow) { out[(el.props.stage?.id ?? el.props.stage?.role) as string] = path.join('>'); return; }
    const label = `${typeof el.type === 'string' ? el.type : 'C'}:${el.key ?? ''}`;
    walk(el.props.children, [...path, label]);
  };
  walk(tree, []);
  return out;
}

describe('лента этапов — ключ строки по id этапа', () => {
  it('после перестановки стрелкой путь предков строки не меняется', () => {
    const before = rowParents(stages());
    const after = rowParents(moveStage(stages(), 0, 1));
    expect(after['cut']).toBe(before['cut']);
    expect(after['plan']).toBe(before['plan']);
  });

  it('перестановка внутри и через рамку «параллельно» тоже не пересаживает строку', () => {
    const before = rowParents(stages());
    const after = rowParents(moveStage(stages(), 1, 3));
    for (const k of ['cut', 'plan', 'step-back', 'step-front']) expect(after[k]).toBe(before[k]);
  });

  it('ключ этапа не меняется после moveStage, меняется только его позиция', () => {
    const before = rowKeys(stages());
    const after = rowKeys(moveStage(stages(), 0, 1));
    expect(Object.keys(before).sort()).toEqual(Object.keys(after).sort());
    expect(before['cut']).toBe(0);
    expect(after['cut']).toBe(1);
  });

  it('два шага вниз подряд — ключ тот же, позиция +2', () => {
    const twice = moveStage(moveStage(stages(), 0, 1), 1, 2);
    expect(rowKeys(twice)['cut']).toBe(2);
  });

  it('повтор id не даёт дубля ключа', () => {
    const dup: WorkflowStage[] = [{ role: 'qa', presence: 'on' }, { role: 'qa', presence: 'on' }];
    expect(stageKeys(dup)).toEqual(['qa', 'qa#1']);
  });
});
