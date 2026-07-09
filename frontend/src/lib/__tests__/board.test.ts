import { describe, it, expect } from 'vitest';
import type { Task, TaskAssignee, TaskPriority, TaskStatus } from '../../types';
import { addDaysIso, boardCardSort, boardLanes, computeOrder, todayIso } from '../tasks';

// Полная задача с дефолтами — переопределяем только нужные поля
let seq = 0;
function mk(p: Partial<Task> = {}): Task {
  seq++;
  return {
    id: p.id ?? `t${seq}`,
    title: p.title ?? `Задача ${seq}`,
    description: '',
    status: (p.status ?? 'todo') as TaskStatus,
    priority: (p.priority ?? 'medium') as TaskPriority,
    linkedFiles: [],
    subtasks: [],
    labels: [],
    order: p.order ?? 0,
    createdAt: p.createdAt ?? `2026-01-0${(seq % 9) + 1}T00:00:00Z`,
    updatedAt: '2026-01-01T00:00:00Z',
    ...p,
  };
}

describe('computeOrder', () => {
  it('midpoint между соседями', () => {
    expect(computeOrder(1000, 2000)).toBe(1500);
  });
  it('край: вставка в конец (только prev)', () => {
    expect(computeOrder(3000, undefined)).toBe(4000);
  });
  it('край: вставка в начало (только next)', () => {
    expect(computeOrder(undefined, 1000)).toBe(0);
  });
  it('пустая колонка', () => {
    expect(computeOrder(undefined, undefined)).toBe(1000);
  });
});

describe('boardCardSort', () => {
  it('сортирует по order по возрастанию', () => {
    const cards = [mk({ order: 3000 }), mk({ order: 1000 }), mk({ order: 2000 })];
    const sorted = [...cards].sort(boardCardSort).map(c => c.order);
    expect(sorted).toEqual([1000, 2000, 3000]);
  });
  it('при равном order — тай-брейк по приоритету (urgent раньше low)', () => {
    const low = mk({ order: 0, priority: 'low' });
    const urgent = mk({ order: 0, priority: 'urgent' });
    const sorted = [low, urgent].sort(boardCardSort);
    expect(sorted[0].priority).toBe('urgent');
  });
});

describe('boardLanes', () => {
  const projects = new Map([['p1', { name: 'Проект 1' }]]);

  it('none — одна дорожка со всеми задачами', () => {
    const lanes = boardLanes([mk(), mk()], 'none', projects);
    expect(lanes).toHaveLength(1);
    expect(lanes[0].key).toBe('all');
    expect(lanes[0].tasks).toHaveLength(2);
  });

  it('priority — дорожки в порядке важности, пустые скрыты', () => {
    const lanes = boardLanes(
      [mk({ priority: 'low' }), mk({ priority: 'urgent' })],
      'priority', projects,
    );
    expect(lanes.map(l => l.key)).toEqual(['urgent', 'low']);
  });

  it('assignee — порядок claude, me, none', () => {
    const lanes = boardLanes(
      [mk({ assignee: 'me' as TaskAssignee }), mk({ assignee: 'claude' as TaskAssignee }), mk()],
      'assignee', projects,
    );
    expect(lanes.map(l => l.key)).toEqual(['claude', 'me', 'none']);
  });

  it('project — личные (none) первыми', () => {
    const lanes = boardLanes(
      [mk({ projectId: 'p1' }), mk()],
      'project', projects,
    );
    expect(lanes[0].key).toBe('none');
    expect(lanes[0].label).toBe('Личное');
    expect(lanes[1].label).toBe('Проект 1');
  });

  it('due — ведёрки по сроку в правильном порядке', () => {
    const today = todayIso();
    const lanes = boardLanes([
      mk({ dueDate: addDaysIso(today, 30) }),   // later
      mk({ dueDate: addDaysIso(today, -1) }),   // overdue
      mk({ dueDate: today }),                   // today
    ], 'due', projects);
    expect(lanes.map(l => l.key)).toEqual(['overdue', 'today', 'later']);
  });
});
