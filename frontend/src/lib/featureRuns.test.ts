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
  openPillMenu,
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

describe('Перф-2 B2: троттлинг и дедуп запросов агрегата', () => {
  let getFeaturesSpy: ReturnType<typeof vi.spyOn>;
  const msg = () => ({
    type: 'task_changed' as const, action: 'updated' as const,
    task: task({ id: 'epic-t', role: 'feature', sourceSessionId: 'T' }),
  });

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
    setAllFlags({ [FLAG]: true });
    getFeaturesSpy = vi.spyOn(api.tasks, 'getFeatures').mockResolvedValue([]);
    upsertTaskLocal(task({ id: 'epic-t', role: 'feature', sourceSessionId: 'T' }));
    __test.setActive('T');
  });
  afterEach(() => {
    getFeaturesSpy.mockRestore();
    __test.reset();
    vi.useRealTimers();
  });

  it('второй всплеск событий вскоре после запроса откладывается до конца окна, не уходит сразу', async () => {
    __test.handleTaskChanged(msg());
    await vi.advanceTimersByTimeAsync(500);
    expect(getFeaturesSpy).toHaveBeenCalledTimes(1);

    // новый всплеск через 1 с после запроса: запрос не чаще окна в 4 с
    await vi.advanceTimersByTimeAsync(500);
    __test.handleTaskChanged(msg());
    await vi.advanceTimersByTimeAsync(500);
    expect(getFeaturesSpy).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(3000);
    expect(getFeaturesSpy).toHaveBeenCalledTimes(2);
  });

  it('серия событий в окне даёт один запрос', async () => {
    for (let i = 0; i < 20; i++) {
      __test.handleTaskChanged(msg());
      await vi.advanceTimersByTimeAsync(200);
    }
    await vi.advanceTimersByTimeAsync(10000);
    // 4 с окна → за ~14 с не больше 4 запросов, а не 20
    expect(getFeaturesSpy.mock.calls.length).toBeLessThanOrEqual(4);
    expect(getFeaturesSpy.mock.calls.length).toBeGreaterThanOrEqual(1);
  });

  it('параллельные запросы одной сессии делят один fetch', async () => {
    const a = __test.fetchRuns('T');
    const b = __test.fetchRuns('T');
    expect(a).toBe(b);
    await a;
    expect(getFeaturesSpy).toHaveBeenCalledTimes(1);
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

// ── F9: сигнал «открыть меню пилюли» (per chat, одноразовый) ──
describe('F9: openPillMenu (одноразовый сигнал, per chat)', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  it('openPillMenu: null до сигнала, после — объект с seq', () => {
    expect(__test.pillMenuFor('A'), 'до сигнала null').toBeNull();
    openPillMenu('A');
    const first = __test.pillMenuFor('A');
    expect(first, 'после сигнала объект').not.toBeNull();
    expect(first!.seq).toBeGreaterThan(0);
  });

  it('openPillMenu: повторный сигнал — seq растёт, новая ссылка (подписчик сработает заново)', () => {
    openPillMenu('A');
    const first = __test.pillMenuFor('A')!;
    openPillMenu('A');
    const second = __test.pillMenuFor('A')!;
    expect(second.seq).toBeGreaterThan(first.seq);
    expect(second).not.toBe(first);
  });

  it('openPillMenu: изоляция по sessionId (сигнал чата A не доходит в B)', () => {
    openPillMenu('A');
    expect(__test.pillMenuFor('A')).not.toBeNull();
    expect(__test.pillMenuFor('B'), 'чужая сессия сигнала не получила').toBeNull();
  });
});

// ── F4: потолок per-session памяти — LRU 8 чатов, активные не вытесняются ──────

describe('F4: LRU потолок 8 чатов, вытеснение из всех 7 Map разом', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  const MANY = Array.from({ length: 20 }, (_, i) => `s${i}`);

  // Запись во все 7 Map по одному ключу — состояние, которое оставляет просмотренный чат.
  function touchAllMaps(sessionId: string) {
    __test.setRuns(sessionId, [run(sessionId)]);
    __test.setLoading(sessionId, true);
    __test.setError(sessionId, 'boom');
    // явный «plan» при наличии фич различим с умолчанием (при фичах умолчание — 'feature')
    setTurnPlanMode(sessionId, 'plan');
    setSelectedFeatureId(sessionId, `feat-${sessionId}`);
    focusFeatureStep(sessionId, `feat-${sessionId}`, `task-${sessionId}`);
    openPillMenu(sessionId);
  }

  function expectPresent(sessionId: string) {
    expect(__test.snapshotFor(sessionId).length, `runs ${sessionId}`).toBe(1);
    expect(__test.loadingFor(sessionId), `loading ${sessionId}`).toBe(true);
    expect(__test.errorFor(sessionId), `error ${sessionId}`).toBe('boom');
    expect(__test.modeFor(sessionId), `mode ${sessionId}`).toBe('plan');
    expect(__test.selectedFeatureIdFor(sessionId), `selected ${sessionId}`).toBe(`feat-${sessionId}`);
    expect(__test.focusFor(sessionId), `focus ${sessionId}`).not.toBeNull();
    expect(__test.pillMenuFor(sessionId), `pillMenu ${sessionId}`).not.toBeNull();
  }

  // mode здесь не проверяем: после вытеснения явный «plan» неразличим с умолчанием
  // без-фич; размер _modeBySession закрыт проверкой sizes().
  function expectGone(sessionId: string) {
    expect(__test.snapshotFor(sessionId).length, `runs ${sessionId}`).toBe(0);
    expect(__test.loadingFor(sessionId), `loading ${sessionId}`).toBe(false);
    expect(__test.errorFor(sessionId), `error ${sessionId}`).toBeNull();
    expect(__test.selectedFeatureIdFor(sessionId), `selected ${sessionId}`).toBeUndefined();
    expect(__test.focusFor(sessionId), `focus ${sessionId}`).toBeNull();
    expect(__test.pillMenuFor(sessionId), `pillMenu ${sessionId}`).toBeNull();
  }

  it('c1: 20 чатов по очереди → в каждой из 7 Map ≤ 8 ключей', () => {
    for (const s of MANY) touchAllMaps(s);
    const sizes = __test.sizes();
    expect(sizes.runs, 'runs').toBeLessThanOrEqual(8);
    expect(sizes.loading, 'loading').toBeLessThanOrEqual(8);
    expect(sizes.error, 'error').toBeLessThanOrEqual(8);
    expect(sizes.mode, 'mode').toBeLessThanOrEqual(8);
    expect(sizes.selected, 'selected').toBeLessThanOrEqual(8);
    expect(sizes.focus, 'focus').toBeLessThanOrEqual(8);
    expect(sizes.pillMenu, 'pillMenu').toBeLessThanOrEqual(8);
    // LRU, а не «какие попало»: держатся последние 8, первые вытеснены
    expectPresent('s19');
    expectPresent('s12');
    expectGone('s0');
    expectGone('s11');
  });

  it('c2: 3 активных чата + 20 других → все 3 активных на месте', () => {
    for (const a of ['a1', 'a2', 'a3']) {
      __test.setActive(a);
      touchAllMaps(a);
    }
    for (const s of MANY) touchAllMaps(s);
    for (const a of ['a1', 'a2', 'a3']) expectPresent(a);
    // потолок держится и при активных: всего ключей в Map не больше 8 (3 активных + 5 неактивных)
    expect(__test.sizes().runs).toBeLessThanOrEqual(8);
  });

  it('вытеснение убирает ключ из всех 7 Map разом (не только runs)', () => {
    touchAllMaps('x1'); // x1 — самый старый
    // 8 активных заполняют потолок; вытеснять можно только x1
    for (let i = 0; i < 8; i++) {
      __test.setActive(`act${i}`);
      __test.setRuns(`act${i}`, [run(`act${i}`)]);
    }
    expectGone('x1');
    expect(__test.lru().includes('x1'), 'ключ убран и из LRU-порядка').toBe(false);
    for (let i = 0; i < 8; i++) expect(__test.snapshotFor(`act${i}`).length).toBe(1);
  });
});
