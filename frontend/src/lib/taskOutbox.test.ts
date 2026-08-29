// Зеркало правил дефектов в оптимистичной подготовке (волна 2):
// buildLocalTask/applyUpdateLocally обязаны отвергать действие ДО возврата задачи —
// иначе updateTaskOffline/createTaskOffline в tasks.ts успеют сделать upsert и enqueue,
// а на дренаже 4xx уйдёт тихим console.warn (handlePermanent в taskOutbox.ts:193-200),
// и пользователь не узнает об отказе.

import { describe, it, expect } from 'vitest';
import { buildLocalTask, applyUpdateLocally } from './taskOutbox';
import type { CreateTaskDto, Task, UpdateTaskDto } from '../types';
import {
  DefectRuleError, type DefectOutcome, type DefectRepro, type TaskKind, type TaskVerification,
} from './tasks';

// Расширенные типы под дефект-поля (kind/repro/verification/outcome). Когда фронт
// дефектов (волна 3) принесёт их в CreateTaskDto/UpdateTaskDto, эти приведения исчезнут.
type DefectCreateDto = CreateTaskDto & {
  kind?: TaskKind;
  repro?: DefectRepro;
  verification?: TaskVerification;
  outcome?: DefectOutcome;
};
type DefectUpdateDto = UpdateTaskDto & {
  kind?: TaskKind;
  repro?: DefectRepro | null;
  verification?: TaskVerification | null;
  outcome?: DefectOutcome | null;
};
type DefectTask = Task & {
  kind?: TaskKind;
  repro?: DefectRepro | null;
  verification?: TaskVerification | null;
  outcome?: DefectOutcome | null;
};

// Хелперы для фикстур — дефект в указанном статусе, с дефолтным null-окружением.
function defectCreate(status: Task['status'], extras: Partial<DefectCreateDto> = {}): DefectCreateDto {
  return { title: 'defect', status, kind: 'defect', ...extras };
}

function defectTask(status: Task['status'], extras: Partial<DefectTask> = {}): DefectTask {
  return {
    id: 'd1',
    title: 'defect',
    status,
    kind: 'defect',
    description: '',
    columnId: undefined,
    priority: 'medium',
    linkedFiles: [],
    subtasks: [],
    labels: [],
    order: 1000,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
    ...extras,
  };
}

// === buildLocalTask — правило 1 (EnsureNotClosedAtCreate) ===

describe('buildLocalTask — TS-зеркало EnsureNotClosedAtCreate', () => {
  it('дефект в Done бросает DefectRuleError ДО возврата задачи', () => {
    let result: unknown;
    let threw: unknown;
    try {
      result = buildLocalTask('d1', 'p1', defectCreate('done'), 1000);
    } catch (e) { threw = e; }
    expect(threw).toBeInstanceOf(DefectRuleError);
    expect(String(threw)).toMatch(/нельзя создавать сразу в Done/);
    expect(result).toBeUndefined();   // задача не возвращена → upsert не выполнится
  });

  it('обычная задача в Done проходит (Defect-правила её не касаются)', () => {
    const dto: CreateTaskDto = { title: 'task', status: 'done' };
    expect(() => buildLocalTask('t1', 'p1', dto, 1000)).not.toThrow();
    const task = buildLocalTask('t1', 'p1', dto, 1000);
    expect(task.status).toBe('done');
    expect(task.title).toBe('task');
  });

  it('дефект в Todo проходит (открытый дефект — ожидаемое состояние)', () => {
    const task = buildLocalTask('d1', 'p1', defectCreate('todo'), 1000);
    expect(task.status).toBe('todo');
    expect((task as DefectTask).kind).toBe('defect');
  });

  it('дефект в InProgress проходит', () => {
    const task = buildLocalTask('d1', 'p1', defectCreate('inProgress'), 1000);
    expect(task.status).toBe('inProgress');
  });

  it('kind отсутствует — обычная задача, правила не действуют даже при status=done', () => {
    const dto: CreateTaskDto = { title: 'plain task', status: 'done' };
    expect(() => buildLocalTask('t1', 'p1', dto, 1000)).not.toThrow();
  });

  it('дефект с заполненным Repro создаётся (repro пробрасывается в задачу)', () => {
    const dto = defectCreate('todo', { repro: { steps: 'шаги' } });
    const task = buildLocalTask('d1', 'p1', dto, 1000);
    expect((task as DefectTask).repro?.steps).toBe('шаги');
  });
});

// === applyUpdateLocally — правило 2 (EnsureVerificationOnClose) ===

describe('applyUpdateLocally — TS-зеркало EnsureVerificationOnClose', () => {
  const defectTodo = defectTask('todo');

  it('перевод дефекта из todo в done без Verification/Outcome бросает', () => {
    let threw: unknown;
    try {
      applyUpdateLocally(defectTodo, { status: 'done' } as UpdateTaskDto);
    } catch (e) { threw = e; }
    expect(threw).toBeInstanceOf(DefectRuleError);
    expect(String(threw)).toMatch(/заполните Verification/);
  });

  it('перевод дефекта в done с Verification проходит (дизъюнкция)', () => {
    const dto: DefectUpdateDto = {
      status: 'done',
      verification: { verifiedAt: '2026-08-01T12:00:00Z' },
    };
    expect(() => applyUpdateLocally(defectTodo, dto as UpdateTaskDto)).not.toThrow();
  });

  it('перевод дефекта в done с Outcome=closedWithoutCheck проходит (внутренний путь)', () => {
    const dto: DefectUpdateDto = {
      status: 'done',
      outcome: 'closedWithoutCheck',
    };
    expect(() => applyUpdateLocally(defectTodo, dto as UpdateTaskDto)).not.toThrow();
  });

  it('перевод дефекта в done и с Verification, и с ClosedWithoutCheck проходит', () => {
    const dto: DefectUpdateDto = {
      status: 'done',
      verification: { verifiedAt: '2026-08-01T12:00:00Z' },
      outcome: 'closedWithoutCheck',
    };
    expect(() => applyUpdateLocally(defectTodo, dto as UpdateTaskDto)).not.toThrow();
  });

  it('обычная задача без kind в Done без Verification проходит (no-op)', () => {
    const plain = defectTask('todo', { kind: 'task' });
    expect(() => applyUpdateLocally(plain, { status: 'done' } as UpdateTaskDto)).not.toThrow();
  });

  it('дефект уже в done — без verification не пройдёт правило (потеряли вердикт)', () => {
    const defectAlreadyDone = defectTask('done');
    let threw: unknown;
    try {
      applyUpdateLocally(defectAlreadyDone, { title: 'rename' } as UpdateTaskDto);
    } catch (e) { threw = e; }
    // Любая правка дефекта, уже нарушающего инвариант, должна бросать (защита от залипания
    // состояния в сторе с нарушением правила через повторные правки)
    expect(threw).toBeInstanceOf(DefectRuleError);
  });

  it('смена title у дефекта в todo проходит (правила закрытия не действуют)', () => {
    expect(() => applyUpdateLocally(defectTodo, { title: 'new title' } as UpdateTaskDto))
      .not.toThrow();
  });

  it('отказ при закрытии не возвращает новую задачу — исходный объект не модифицирован', () => {
    const before = { ...defectTodo, updatedAt: defectTodo.updatedAt };
    let next: Task | undefined;
    try {
      next = applyUpdateLocally(defectTodo, { status: 'done' } as UpdateTaskDto);
    } catch (_) { next = undefined; }
    expect(next).toBeUndefined();
    // Старый объект остаётся в исходном состоянии — оптимистичное применение не случилось
    expect(defectTodo.status).toBe('todo');
    expect(defectTodo.updatedAt).toBe(before.updatedAt);
  });

  it('verification=null трактуется как отсутствующее (правило бросает)', () => {
    const dto: DefectUpdateDto = { status: 'done', verification: null };
    let threw: unknown;
    try {
      applyUpdateLocally(defectTodo, dto as UpdateTaskDto);
    } catch (e) { threw = e; }
    expect(threw).toBeInstanceOf(DefectRuleError);
  });
});

// === Прокидывание дефект-полей (формат задачи) ===

describe('applyUpdateLocally — прокидывание defect-полей', () => {
  const base = defectTask('todo');

  it('kind задаётся через dto', () => {
    const dto: DefectUpdateDto = { kind: 'defect' };
    const next = applyUpdateLocally(base, dto as UpdateTaskDto) as DefectTask;
    expect(next.kind).toBe('defect');
  });

  it('repro пробрасывается в задачу', () => {
    const dto: DefectUpdateDto = { repro: { steps: '1. step' } };
    const next = applyUpdateLocally(base, dto as UpdateTaskDto) as DefectTask;
    expect(next.repro?.steps).toBe('1. step');
  });

  it('repro=null сбрасывает поле', () => {
    const withRepro = defectTask('todo', { repro: { steps: 'x' } });
    const dto: DefectUpdateDto = { repro: null };
    const next = applyUpdateLocally(withRepro, dto as UpdateTaskDto) as DefectTask;
    expect(next.repro).toBeUndefined();
  });

  it('verification пробрасывается', () => {
    const dto: DefectUpdateDto = { verification: { verifiedAt: '2026-08-15T10:00:00Z' } };
    const next = applyUpdateLocally(base, dto as UpdateTaskDto) as DefectTask;
    expect(next.verification?.verifiedAt).toBe('2026-08-15T10:00:00Z');
  });
});

// === Существующее поведение не сломано (страховка от регрессии) ===

describe('applyUpdateLocally — сохранено прежнее поведение для обычных задач', () => {
  const base = buildLocalTask('t1', null, { title: 'T', priority: 'medium' }, 1000);

  it('пустая строка dueDate очищает, undefined не трогает', () => {
    const withDue = applyUpdateLocally(base, { dueDate: '2026-07-10', dueTime: '14:00' });
    expect(withDue.dueDate).toBe('2026-07-10');
    const cleared = applyUpdateLocally(withDue, { dueTime: '' });
    expect(cleared.dueTime).toBeUndefined();
    expect(cleared.dueDate).toBe('2026-07-10');   // не менялось
  });

  it('reminderMinutes < 0 убирает напоминание', () => {
    const r = applyUpdateLocally(base, { reminderMinutes: -1 });
    expect(r.reminderMinutes).toBeUndefined();
  });

  it('смена статуса без columnId сбрасывает колонку', () => {
    const withCol: Task = { ...base, columnId: 'col-x', status: 'todo' };
    const r = applyUpdateLocally(withCol, { status: 'inProgress' });
    expect(r.columnId).toBeUndefined();
  });
});
