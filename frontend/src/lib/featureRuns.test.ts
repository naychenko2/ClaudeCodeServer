// Стор агрегатов фич (режим «Исполнение фичи»).
//
// Oкружение node (без jsdom): effects не идут, поэтому тестируем ЛОГИКУ стора
// через тестовые швы `__test`, а не React-хук. Два блока:
//   M1 — стор разделён по sessionId: срез по сессии, стабильная ссылка,
//        обновление одной сессии не меняет другую;
//   M2 — live-режим: task_changed перезапрашивает агрегат затронутой сессии
//        (дебаунс ~500 мс), чужие не трогает; фича выключена — событие проигнорировано.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  __test,
  setTurnPlanMode,
  getTurnPlanMode,
  setSelectedFeatureId,
  getSelectedFeatureId,
  focusFeatureStep,
} from './featureRuns';
import { api } from './api';
import { upsertTaskLocal } from './tasks';
import { setAllFlags } from './featureFlags';
import type { FeatureRun } from '../types/featureRun';
import type { Task } from '../types';

// ── Фикстуры ─────────────────────────────────────────────────────────────────

function run(id: string): FeatureRun {
  return {
    featureId: id,
    epic: { id, title: `Эпик ${id}` },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
  };
}

function task(over: Partial<Task>): Task {
  return {
    id: 't', title: 'Задача', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 1, createdAt: '', updatedAt: '',
    ...over,
  } as Task;
}

const FLAG = 'turn-plan-feature-mode';

// ── M1: стор агрегатов по sessionId ───────────────────────────────────────────

describe('M1: стор агрегатов разделён по sessionId', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  it('срез по sessionId: A видит только фичи A, B — только B', () => {
    const a = [run('A')];
    const b = [run('B')];
    __test.setRuns('A', a);
    __test.setRuns('B', b);
    expect(__test.snapshotFor('A')).toBe(a);
    expect(__test.snapshotFor('B')).toBe(b);
    // срезы разных сессий — разные ссылки (не общий массив)
    expect(__test.snapshotFor('A')).not.toBe(__test.snapshotFor('B'));
  });

  it('setRuns("B") не меняет срез A (стабильная ссылка)', () => {
    const a = [run('A')];
    __test.setRuns('A', a);
    const aBefore = __test.snapshotFor('A');
    expect(aBefore).toBe(a);
    // новый fetch для B — срез A остаётся тем же массивом (иначе useSyncExternalStore зациклит)
    __test.setRuns('B', [run('B')]);
    expect(__test.snapshotFor('A')).toBe(aBefore);
    expect(__test.snapshotFor('A')).toBe(a);
  });

  it('срез несуществующей/нулевой сессии — EMPTY (стабильная ссылка)', () => {
    expect(__test.snapshotFor('nope')).toBe(__test.snapshotFor('nope'));
    expect(__test.snapshotFor(null)).toBe(__test.snapshotFor(null));
    expect(__test.snapshotFor('nope').length).toBe(0);
  });

  it('clearRuns() — Map.clear (все срезы пусты)', () => {
    __test.setRuns('A', [run('A')]);
    __test.setRuns('B', [run('B')]);
    __test.clearRuns();
    expect(__test.snapshotFor('A').length).toBe(0);
    expect(__test.snapshotFor('B').length).toBe(0);
  });
});

// ── M2: перезапрос агрегата по task_changed ───────────────────────────────────

describe('M2: live-режим — перезапрос по task_changed', () => {
  let getFeaturesSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
    setAllFlags({ [FLAG]: true });
    // Шпион на REST-запрос: доказываем, что именно эта сессия была перезапрошена.
    getFeaturesSpy = vi.spyOn(api.tasks, 'getFeatures').mockResolvedValue([]);
  });
  afterEach(() => {
    getFeaturesSpy.mockRestore();
    __test.reset();
    vi.useRealTimers();
  });

  it('task_changed по шагу фичи → перезапрос сессии эпика (sourceSessionId), чужая — нет', () => {
    // эпик живёт в чате A; дочерний шаг ссылается на него
    upsertTaskLocal(task({ id: 'epic-1', role: 'feature', sourceSessionId: 'A' }));
    upsertTaskLocal(task({ id: 'step-1', featureId: 'epic-1' }));
    // чат A «активен» (смонтирован useFeatureRuns('A', true)); B — чужая, тоже активна
    __test.setActive('A');
    __test.setActive('B');

    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-1' }) });

    // дебаунс: перезапрос A запланирован, B — нет
    expect(__test.pending().has('A')).toBe(true);
    expect(__test.pending().has('B')).toBe(false);
    // после окна дебаунса запрос уходит ТОЛЬКО для A
    vi.advanceTimersByTime(500);
    expect(getFeaturesSpy).toHaveBeenCalledTimes(1);
    expect(getFeaturesSpy).toHaveBeenCalledWith('A');
  });

  it('task_changed по эпику (role=feature) → перезапрос его sourceSessionId', () => {
    upsertTaskLocal(task({ id: 'epic-2', role: 'feature', sourceSessionId: 'C' }));
    __test.setActive('C');

    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-2', role: 'feature', sourceSessionId: 'C' }) });

    expect(__test.pending().has('C')).toBe(true);
  });

  it('фича выключена — событие проигнорировано (нет перезапроса)', () => {
    setAllFlags({ [FLAG]: false });
    upsertTaskLocal(task({ id: 'epic-3', role: 'feature', sourceSessionId: 'D' }));
    __test.setActive('D');

    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-3', role: 'feature', sourceSessionId: 'D' }) });

    expect(__test.pending().has('D')).toBe(false);
    expect(getFeaturesSpy).not.toHaveBeenCalled();
  });

  it('обычная задача (без featureId/role) — нет перезапроса', () => {
    __test.setActive('E');
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'plain' }) });
    expect(__test.pending().has('E')).toBe(false);
    expect(getFeaturesSpy).not.toHaveBeenCalled();
  });

  it('дебаунс: серия task_changed в окне 500мс → один перезапрос', () => {
    upsertTaskLocal(task({ id: 'epic-4', role: 'feature', sourceSessionId: 'F' }));
    __test.setActive('F');
    const msg = { type: 'task_changed' as const, action: 'updated' as const, task: task({ id: 'epic-4', role: 'feature', sourceSessionId: 'F' }) };
    __test.handleTaskChanged(msg);
    __test.handleTaskChanged(msg);
    // один таймер на сессию (дебаунс), не два
    expect([...__test.pending()].filter(s => s === 'F')).toHaveLength(1);
  });
});

// ── F1: режим, выбор фичи, фокус шага — в памяти вкладки, per-session ─────────

describe('F1: режим «План/Фича» на чат (в памяти вкладки)', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  it('умолчание: есть хотя бы одна фича → feature, нет фич → plan', () => {
    __test.setRuns('A', [run('A')]);
    expect(getTurnPlanMode('A')).toBe('feature');
    // у B фич нет → plan
    expect(getTurnPlanMode('B')).toBe('plan');
  });

  it('режим одного чата не влияет на другой (изоляция по sessionId)', () => {
    __test.setRuns('A', [run('A')]);
    __test.setRuns('B', [run('B')]);
    // явный выбор «план» для A; B — своё умолчание (есть фича → feature)
    setTurnPlanMode('A', 'plan');
    expect(getTurnPlanMode('A')).toBe('plan');
    expect(getTurnPlanMode('B')).toBe('feature');
  });

  it('явный выбор режима перебивает умолчание', () => {
    __test.setRuns('A', [run('A')]);
    setTurnPlanMode('A', 'plan');
    expect(getTurnPlanMode('A')).toBe('plan');
    // и обратно: явный feature, даже если фичи исчезли
    setTurnPlanMode('A', 'feature');
    expect(getTurnPlanMode('A')).toBe('feature');
  });

  it('умолчание пересчитывается, когда фичи появляются/исчезают (явного выбора нет)', () => {
    // сначала фич нет → plan
    expect(getTurnPlanMode('A')).toBe('plan');
    // фича появилась → feature (явного выбора не было)
    __test.setRuns('A', [run('A')]);
    expect(getTurnPlanMode('A')).toBe('feature');
    // фичи исчезли → plan
    __test.setRuns('A', []);
    expect(getTurnPlanMode('A')).toBe('plan');
  });
});

describe('F1: выбор фичи (id эпика) на чат', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  it('выбор фичи сбрасывается при смене чата (изоляция по sessionId)', () => {
    setSelectedFeatureId('A', 'feat-1');
    expect(getSelectedFeatureId('A')).toBe('feat-1');
    // B не наследует выбор A
    expect(getSelectedFeatureId('B')).toBeUndefined();
  });

  it('смена выбора внутри чата', () => {
    setSelectedFeatureId('A', 'feat-1');
    setSelectedFeatureId('A', 'feat-2');
    expect(getSelectedFeatureId('A')).toBe('feat-2');
  });

  it('без сессии — выбор не ставится', () => {
    // null/пустая сессия: set игнорирует, get — undefined
    expect(getSelectedFeatureId(null)).toBeUndefined();
  });
});

describe('F1: сигнал фокуса шага (featureId + taskId) на чат', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  it('фокус доходит до подписчика (focusFor возвращает сигнал)', () => {
    focusFeatureStep('A', 'feat-1', 'task-1');
    const focus = __test.focusFor('A');
    expect(focus).not.toBeNull();
    expect(focus!.featureId).toBe('feat-1');
    expect(focus!.taskId).toBe('task-1');
  });

  it('фокус не течёт между чатами (изоляция по sessionId)', () => {
    focusFeatureStep('A', 'feat-1', 'task-1');
    expect(__test.focusFor('A')).not.toBeNull();
    // B: фокуса нет
    expect(__test.focusFor('B')).toBeNull();
  });

  it('повторный фокус на тот же (featureId, taskId) даёт новый сигнал (seq растёт)', () => {
    focusFeatureStep('A', 'feat-1', 'task-1');
    const first = __test.focusFor('A')!;
    focusFeatureStep('A', 'feat-1', 'task-1');
    const second = __test.focusFor('A')!;
    expect(second.seq).toBeGreaterThan(first.seq);
    // ссылки разные → useSyncExternalStore увидит смену
    expect(second).not.toBe(first);
  });
});

describe('F1: useFeatureRunsSnapshot — только читает стор, без сетевых вызовов', () => {
  let getFeaturesSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    __test.reset();
    // Шпион на REST-запрос: доказываем, что читальный путь его не трогает.
    getFeaturesSpy = vi.spyOn(api.tasks, 'getFeatures').mockResolvedValue([]);
  });
  afterEach(() => {
    getFeaturesSpy.mockRestore();
    __test.reset();
  });

  it('срез по sessionId читается без fetch (snapshotFor — чистое чтение Map)', () => {
    const a = [run('A')];
    __test.setRuns('A', a);
    // useFeatureRunsSnapshot(sessionId) → getSnapshot = snapshotFor(sessionId)
    expect(__test.snapshotFor('A')).toBe(a);
    // сетевой запрос не уходил
    expect(getFeaturesSpy).not.toHaveBeenCalled();
  });

  it('срез несуществующей сессии — EMPTY, без fetch', () => {
    expect(__test.snapshotFor('nope').length).toBe(0);
    expect(getFeaturesSpy).not.toHaveBeenCalled();
  });
});
