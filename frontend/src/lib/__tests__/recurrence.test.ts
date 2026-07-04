// Тест-зеркало nextDueDate — держит фронтовый порт в согласии с бэкендом
// (TaskRecurrenceCalculator.cs). При расхождении правила повторения на бэке
// нужно синхронно править и tasks.ts.

import { describe, it, expect } from 'vitest';
import type { Task, TaskRecurrence } from '../../types';
import { nextDueDate, expandRecurringTasks } from '../tasks';

const rule = (r: Partial<TaskRecurrence>): TaskRecurrence =>
  ({ type: 'daily', interval: 1, ...r });

describe('nextDueDate', () => {
  it('daily: прибавляет интервал', () => {
    expect(nextDueDate('2026-07-04', rule({ type: 'daily', interval: 1 }))).toBe('2026-07-05');
    expect(nextDueDate('2026-07-04', rule({ type: 'daily', interval: 3 }))).toBe('2026-07-07');
  });

  it('monthly: клипует день на конец месяца', () => {
    expect(nextDueDate('2026-01-31', rule({ type: 'monthly', interval: 1 }))).toBe('2026-02-28');
    expect(nextDueDate('2026-01-15', rule({ type: 'monthly', interval: 1 }))).toBe('2026-02-15');
    expect(nextDueDate('2026-12-10', rule({ type: 'monthly', interval: 2 }))).toBe('2027-02-10');
  });

  it('yearly: клипует 29 февраля', () => {
    expect(nextDueDate('2024-02-29', rule({ type: 'yearly', interval: 1 }))).toBe('2025-02-28');
    expect(nextDueDate('2026-03-10', rule({ type: 'yearly', interval: 1 }))).toBe('2027-03-10');
  });

  it('weekly без weekdays: тот же день недели через N недель', () => {
    expect(nextDueDate('2026-07-04', rule({ type: 'weekly', interval: 1 }))).toBe('2026-07-11');
    expect(nextDueDate('2026-07-04', rule({ type: 'weekly', interval: 2 }))).toBe('2026-07-18');
  });

  it('weekly по дням недели: ближайший подходящий день', () => {
    // 2026-07-06 — Пн; правило Пн/Ср/Пт → следующий Ср 2026-07-08
    expect(nextDueDate('2026-07-06', rule({ type: 'weekly', interval: 1, weekdays: [1, 3, 5] }))).toBe('2026-07-08');
    // от Ср 2026-07-08 → Пт 2026-07-10
    expect(nextDueDate('2026-07-08', rule({ type: 'weekly', interval: 1, weekdays: [1, 3, 5] }))).toBe('2026-07-10');
  });

  it('weekly каждые 2 недели по Пн: пропускает промежуточную неделю', () => {
    // 2026-07-06 Пн → через 2 недели 2026-07-20 (не 07-13)
    expect(nextDueDate('2026-07-06', rule({ type: 'weekly', interval: 2, weekdays: [1] }))).toBe('2026-07-20');
  });

  it('until: null когда следующая дата выходит за границу серии', () => {
    expect(nextDueDate('2026-07-04', rule({ type: 'daily', interval: 1, until: '2026-07-04' }))).toBeNull();
    expect(nextDueDate('2026-07-04', rule({ type: 'daily', interval: 1, until: '2026-07-05' }))).toBe('2026-07-05');
  });

  it('none: null (повторения нет)', () => {
    expect(nextDueDate('2026-07-04', rule({ type: 'none' }))).toBeNull();
  });
});

describe('expandRecurringTasks', () => {
  const base: Task = {
    id: 't1', title: 'Полить цветы', description: '', status: 'todo', priority: 'medium',
    dueDate: '2026-07-04', recurrence: { type: 'daily', interval: 1 },
    linkedFiles: [], subtasks: [], labels: [], createdAt: '', updatedAt: '',
  };

  it('разворачивает ежедневную задачу в окне и помечает проекции virtual', () => {
    const out = expandRecurringTasks([base], '2026-07-04', '2026-07-07');
    expect(out.map(t => t.dueDate)).toEqual(['2026-07-04', '2026-07-05', '2026-07-06', '2026-07-07']);
    // Первая — реальная, остальные — проекции с ссылкой на реальный экземпляр
    expect(out[0].virtual).toBeFalsy();
    expect(out.slice(1).every(t => t.virtual && t.occurrenceOf === 't1')).toBe(true);
  });

  it('не проецирует завершённую задачу', () => {
    const done: Task = { ...base, status: 'done' };
    const out = expandRecurringTasks([done], '2026-07-04', '2026-07-10');
    expect(out).toHaveLength(1);
    expect(out[0].id).toBe('t1');
  });

  it('не проецирует задачу без повторения', () => {
    const plain: Task = { ...base, recurrence: undefined };
    const out = expandRecurringTasks([plain], '2026-07-04', '2026-07-10');
    expect(out).toHaveLength(1);
  });

  it('пропускает повторы раньше окна, но включает попавшие в него', () => {
    const out = expandRecurringTasks([base], '2026-07-06', '2026-07-07');
    // Реальная задача (07-04) всегда включена; проекции — только 07-06 и 07-07
    expect(out.filter(t => t.virtual).map(t => t.dueDate)).toEqual(['2026-07-06', '2026-07-07']);
  });
});
