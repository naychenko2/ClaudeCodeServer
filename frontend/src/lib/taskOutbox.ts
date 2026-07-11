// Офлайн-очередь мутаций задач (outbox). Мутации, сделанные без связи, копятся в
// IndexedDB (стор 'outbox', одна запись на задачу — естественный коалесинг) и
// проигрываются на сервер при восстановлении связи. Клиентский id задачи делает
// create идемпотентным: replay с тем же id не создаёт дубль (см. TaskManager.Create).
//
// Store-мутации (upsert/remove/reload) живут в tasks.ts и регистрируются через
// configureOutbox — так модуль не зависит от tasks.ts (нет циклического импорта).

import { api } from './api';
import { isOnline, OfflineError } from './offline';
import { outboxAll, outboxGet, outboxPut, outboxDelete } from './idb';
import type { CreateTaskDto, Task, UpdateTaskDto } from '../types';

export type OutboxKind = 'create' | 'update' | 'delete';

export interface OutboxOp {
  taskId: string;                       // keyPath; клиентский uuid (create) или id существующей задачи
  seq: number;                          // монотонный — FIFO-дренаж
  kind: OutboxKind;
  projectId?: string | null;            // только create
  dto: CreateTaskDto | UpdateTaskDto;   // коалесированная нагрузка
  baseUpdatedAt?: string;               // task.updatedAt на момент правки (задел под конфликты)
  attempts: number;
  nextAttemptAt?: number;               // backoff (epoch ms)
  enqueuedAt: number;
}

// === Регистрация store-хуков (из tasks.ts) ===

interface StoreHooks {
  upsert: (t: Task) => void;
  remove: (id: string) => void;
  reload: () => Promise<void>;
}
let _hooks: StoreHooks | null = null;

export function configureOutbox(hooks: StoreHooks): void {
  _hooks = hooks;
  void initPending();
}

// === Синхронный индекс pending (для realtime-guard) ===
// _pending держим в памяти, чтобы обработчик task_changed мог мгновенно понять,
// есть ли по задаче несинхронизированная локальная правка (её не надо затирать).

const _pending = new Set<string>();
let _pendingInit: Promise<void> | null = null;

function initPending(): Promise<void> {
  if (!_pendingInit) {
    _pendingInit = outboxAll<OutboxOp>()
      .then(ops => { for (const o of ops) _pending.add(o.taskId); })
      .catch(() => {});
  }
  return _pendingInit;
}

export function outboxHasPending(taskId: string): boolean {
  return _pending.has(taskId);
}

// === Монотонный seq ===

let _seq = 0;
let _seqInit: Promise<void> | null = null;

async function nextSeq(): Promise<number> {
  if (!_seqInit) {
    _seqInit = outboxAll<OutboxOp>()
      .then(ops => { _seq = ops.reduce((m, o) => Math.max(m, o.seq), 0); })
      .catch(() => {});
  }
  await _seqInit;
  return ++_seq;
}

// === Коалесинг ===
// Результат слияния существующей и новой операции по одной задаче.
// 'drop' — обе операции взаимоуничтожаются (create+delete: задача не дошла до сервера).

type MergeResult =
  | { kind: OutboxKind; dto: CreateTaskDto | UpdateTaskDto; projectId?: string | null }
  | 'drop';

// Наложить поля update-DTO на create-DTO: "" трактуем как «поля нет», recurrence 'none' — убрать.
function applyUpdateOntoCreate(create: CreateTaskDto, upd: UpdateTaskDto): CreateTaskDto {
  const out = { ...create } as Record<string, unknown>;
  for (const [k, v] of Object.entries(upd)) {
    if (v === undefined) continue;
    if (k === 'recurrence') {
      const r = v as UpdateTaskDto['recurrence'];
      if (r && r.type !== 'none') out.recurrence = r; else delete out.recurrence;
    } else if (v === '') {
      delete out[k];
    } else {
      out[k] = v;
    }
  }
  return out as unknown as CreateTaskDto;
}

export function mergeOps(prev: OutboxOp, incoming: { kind: OutboxKind; dto: CreateTaskDto | UpdateTaskDto }): MergeResult {
  // delete терминален: любая последующая операция игнорируется
  if (prev.kind === 'delete') return { kind: 'delete', dto: {} };

  if (prev.kind === 'create') {
    if (incoming.kind === 'delete') return 'drop';   // создано и удалено офлайн → 0 сети
    // create + update → одна POST со слитыми полями
    return {
      kind: 'create',
      dto: applyUpdateOntoCreate(prev.dto as CreateTaskDto, incoming.dto as UpdateTaskDto),
      projectId: prev.projectId,
    };
  }

  // prev.kind === 'update'
  if (incoming.kind === 'delete') return { kind: 'delete', dto: {} };
  // update + update → слияние (позднее значение перекрывает)
  return { kind: 'update', dto: { ...(prev.dto as UpdateTaskDto), ...(incoming.dto as UpdateTaskDto) } };
}

// === Постановка в очередь ===

export async function enqueue(
  taskId: string,
  kind: OutboxKind,
  dto: CreateTaskDto | UpdateTaskDto,
  opts?: { projectId?: string | null; baseUpdatedAt?: string },
): Promise<void> {
  await initPending();
  const prev = await outboxGet<OutboxOp>(taskId).catch(() => undefined);

  if (!prev) {
    const op: OutboxOp = {
      taskId, seq: await nextSeq(), kind, dto,
      projectId: opts?.projectId, baseUpdatedAt: opts?.baseUpdatedAt,
      attempts: 0, enqueuedAt: Date.now(),
    };
    await outboxPut(op).catch(() => {});
    _pending.add(taskId);
    return;
  }

  const merged = mergeOps(prev, { kind, dto });
  if (merged === 'drop') {
    await outboxDelete(taskId).catch(() => {});
    _pending.delete(taskId);
    return;
  }
  const op: OutboxOp = {
    ...prev,
    kind: merged.kind,
    dto: merged.dto,
    projectId: merged.projectId ?? prev.projectId,
    baseUpdatedAt: opts?.baseUpdatedAt ?? prev.baseUpdatedAt,
    attempts: 0,
    nextAttemptAt: undefined,
  };
  await outboxPut(op).catch(() => {});
  _pending.add(taskId);
}

// === Дренаж ===

function httpStatus(e: unknown): number | undefined {
  return (e as { status?: number } | null)?.status;
}

function backoff(attempts: number): number {
  return Math.min(2 ** attempts * 1000, 30_000);
}

async function applyOp(op: OutboxOp): Promise<void> {
  if (!_hooks) return;
  if (op.kind === 'create') {
    // id уходит в теле → сервер принимает клиентский id (идемпотентно при replay)
    const t = await api.tasks.create(op.projectId ?? null, { ...(op.dto as CreateTaskDto), id: op.taskId });
    _hooks.upsert(t);
  } else if (op.kind === 'update') {
    const t = await api.tasks.update(op.taskId, op.dto as UpdateTaskDto);
    _hooks.upsert(t);
  } else {
    try {
      await api.tasks.delete(op.taskId);
    } catch (e) {
      if (httpStatus(e) !== 404) throw e;   // 404 = уже удалена → успех
    }
    _hooks.remove(op.taskId);
  }
}

// Перманентная (4xx) ошибка: op снимается, локальное состояние согласуем осмысленно.
function handlePermanent(op: OutboxOp, e: unknown): void {
  if (op.kind === 'update' && httpStatus(e) === 404) {
    // задача удалена на другом устройстве — убираем локальный призрак
    _hooks?.remove(op.taskId);
  }
  // прочие 4xx (напр. 400 валидации): оставляем локальную правку, но синк по ней не удастся
  console.warn('[taskOutbox] операция отклонена сервером, снята из очереди:', op.kind, op.taskId, e);
}

let _draining = false;

export async function drainTaskOutbox(): Promise<void> {
  if (!_hooks || !isOnline() || _draining) return;
  _draining = true;
  try {
    const ops = (await outboxAll<OutboxOp>().catch(() => [] as OutboxOp[])).sort((a, b) => a.seq - b.seq);
    for (const op of ops) {
      if (op.nextAttemptAt && Date.now() < op.nextAttemptAt) continue;
      try {
        await applyOp(op);
        await outboxDelete(op.taskId).catch(() => {});
        _pending.delete(op.taskId);
      } catch (e) {
        if (e instanceof OfflineError) break;             // снова офлайн — ждём следующего триггера
        const status = httpStatus(e);
        if (typeof status === 'number' && status >= 400 && status < 500) {
          handlePermanent(op, e);
          await outboxDelete(op.taskId).catch(() => {});
          _pending.delete(op.taskId);
        } else {
          // 5xx / прочее — повтор с backoff
          await outboxPut({ ...op, attempts: op.attempts + 1, nextAttemptAt: Date.now() + backoff(op.attempts + 1) }).catch(() => {});
        }
      }
    }
  } finally {
    _draining = false;
  }
  // Канонизируем состояние только когда очередь пуста (иначе reload затрёт pending)
  const rest = await outboxAll<OutboxOp>().catch(() => [] as OutboxOp[]);
  if (rest.length === 0) await _hooks.reload().catch(() => {});
}

// === Оверлей pending поверх серверного списка (защита от reload при непустой очереди) ===

export async function mergePending(serverList: Task[], current: Task[]): Promise<Task[]> {
  const ops = (await outboxAll<OutboxOp>().catch(() => [] as OutboxOp[])).sort((a, b) => a.seq - b.seq);
  if (!ops.length) return serverList;

  const curById = new Map(current.map(t => [t.id, t]));
  const byId = new Map(serverList.map(t => [t.id, t]));
  for (const op of ops) {
    if (op.kind === 'delete') {
      byId.delete(op.taskId);
    } else if (op.kind === 'create') {
      if (!byId.has(op.taskId)) {
        byId.set(op.taskId, curById.get(op.taskId) ?? buildLocalTask(op.taskId, op.projectId ?? null, op.dto as CreateTaskDto, 1e9));
      }
    } else {
      const base = byId.get(op.taskId) ?? curById.get(op.taskId);
      if (base) byId.set(op.taskId, applyUpdateLocally(base, op.dto as UpdateTaskDto));
    }
  }
  return [...byId.values()];
}

// === Чистые построители локального состояния (используются и tasks.ts, и mergePending) ===

function nowIso(): string {
  return new Date().toISOString();
}

// Собрать оптимистичную задачу из CreateTaskDto (дефолты — как TaskManager.Create).
export function buildLocalTask(id: string, projectId: string | null, dto: CreateTaskDto, order: number): Task {
  const now = nowIso();
  return {
    id,
    projectId: projectId ?? undefined,
    title: dto.title,
    description: dto.description ?? '',
    status: dto.status ?? 'todo',
    columnId: dto.columnId,
    priority: dto.priority ?? 'medium',
    dueDate: dto.dueDate || undefined,
    dueTime: dto.dueTime || undefined,
    reminderMinutes: dto.reminderMinutes,
    // Персона-исполнитель ⇒ assignee=claude (зеркало инварианта TaskManager)
    assignee: dto.personaId ? 'claude' : dto.assignee,
    personaId: dto.personaId || undefined,
    recurrence: dto.recurrence && dto.recurrence.type !== 'none' ? dto.recurrence : undefined,
    seriesId: dto.recurrence && dto.recurrence.type !== 'none' ? id : undefined,
    linkedSessionId: dto.linkedSessionId,
    linkedFiles: dto.linkedFiles ?? [],
    subtasks: (dto.subtasks ?? []).map(s => ({ id: crypto.randomUUID(), title: s.title, isDone: false })),
    labels: dto.labels ?? [],
    order,
    createdAt: now,
    updatedAt: now,
  };
}

// Применить UpdateTaskDto к задаче локально (зеркало семантики TaskManager.Update).
export function applyUpdateLocally(task: Task, dto: UpdateTaskDto): Task {
  const next: Task = { ...task };
  if (dto.title !== undefined) next.title = dto.title;
  if (dto.description !== undefined) next.description = dto.description;
  if (dto.status !== undefined) next.status = dto.status;
  if (dto.priority !== undefined) next.priority = dto.priority;

  const dueBefore = `${next.dueDate ?? ''}|${next.dueTime ?? ''}|${next.reminderMinutes ?? ''}`;
  if (dto.dueDate !== undefined) next.dueDate = dto.dueDate === '' ? undefined : dto.dueDate;
  if (dto.dueTime !== undefined) next.dueTime = dto.dueTime === '' ? undefined : dto.dueTime;
  if (dto.reminderMinutes !== undefined) next.reminderMinutes = dto.reminderMinutes < 0 ? undefined : dto.reminderMinutes;
  const dueAfter = `${next.dueDate ?? ''}|${next.dueTime ?? ''}|${next.reminderMinutes ?? ''}`;
  if (dueBefore !== dueAfter) next.reminderSentAt = undefined;

  if (dto.assignee !== undefined) next.assignee = dto.assignee;
  // Персона-исполнитель: '' — снять; иначе назначить и выставить assignee=claude (инвариант)
  if (dto.personaId !== undefined) {
    next.personaId = dto.personaId === '' ? undefined : dto.personaId;
    if (next.personaId) next.assignee = 'claude';
  }
  if (dto.recurrence !== undefined) {
    next.recurrence = dto.recurrence.type === 'none' ? undefined : dto.recurrence;
    if (next.recurrence && !next.seriesId) next.seriesId = next.id;
  }
  if (dto.linkedSessionId !== undefined) next.linkedSessionId = dto.linkedSessionId === '' ? undefined : dto.linkedSessionId;
  if (dto.linkedFiles !== undefined) next.linkedFiles = dto.linkedFiles;
  if (dto.labels !== undefined) next.labels = dto.labels;
  if (dto.order !== undefined) next.order = dto.order;
  // Колонка: явное значение ("" = сброс), иначе смена статуса не через доску сбрасывает колонку
  if (dto.columnId !== undefined) next.columnId = dto.columnId === '' ? undefined : dto.columnId;
  else if (dto.status !== undefined) next.columnId = undefined;
  if (dto.subtasks !== undefined) next.subtasks = dto.subtasks.map(s => ({ id: s.id || crypto.randomUUID(), title: s.title, isDone: s.isDone ?? false }));

  next.updatedAt = nowIso();
  return next;
}
