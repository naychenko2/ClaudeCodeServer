// Подпись чата-исполнителя задачи. Ключевое — не врать «Задача удалена», пока стор
// задач ещё грузится: чат исполнения появляется в списке раньше, чем приезжают задачи
// (находка приёмки Э7 «Командной реализации»).
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Session, Task } from '../../types';

const store: { tasks: Task[]; loaded: boolean } = { tasks: [], loaded: false };

vi.mock('../tasks', () => ({
  getTaskById: (id: string) => store.tasks.find(t => t.id === id),
  tasksLoaded: () => store.loaded,
  dueLabel: () => 'Сегодня',
  isDueUrgent: () => false,
}));
vi.mock('../personas', () => ({ getPersonaById: () => undefined }));

import { describeTaskChat, resolveChatOrigin } from '../chatOrigin';

function task(over: Partial<Task> = {}): Task {
  return { id: 't1', title: 'Сделать экспорт', status: 'inProgress', subtasks: [], ...over } as Task;
}

function chat(over: Partial<Session> = {}): Session {
  return {
    id: 's1', mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-07-29T10:00:00Z', updatedAt: '2026-07-29T10:00:00Z',
    origin: 'task', taskId: 't1', name: 'Задача: Сделать экспорт',
    ...over,
  } as Session;
}

beforeEach(() => { store.tasks = []; store.loaded = false; });

describe('статус чата-задачи', () => {
  it('стор задач ещё не загружен — «Загрузка…», а не «Задача удалена»', () => {
    const info = describeTaskChat(chat())!;
    expect(info.status.kind).toBe('todo');
    expect(info.status.label).toBe('Загрузка…');
    expect(info.fullLabel).toBe('Задача');
  });

  it('задачи нет в загруженном сторе — она действительно удалена', () => {
    store.loaded = true;
    const info = describeTaskChat(chat())!;
    expect(info.status.kind).toBe('deleted');
    expect(info.status.label).toBe('Задача удалена');
    expect(info.fullLabel).toBe('Задача (удалена)');
  });

  it('работающий чат подписан живым состоянием, даже пока задача неизвестна', () => {
    const info = describeTaskChat(chat({ status: 'working' }))!;
    expect(info.status).toEqual({ kind: 'run', label: 'Выполняется', spinner: true });
  });

  it('спокойный чат показывает статус самой задачи', () => {
    store.loaded = true;
    store.tasks = [task()];
    expect(describeTaskChat(chat())!.status.label).toBe('В работе');
    store.tasks = [task({ status: 'done' })];
    expect(describeTaskChat(chat())!.status.label).toBe('Готово');
    store.tasks = [task({ status: 'todo' })];
    expect(describeTaskChat(chat())!.status.label).toBe('В очереди');
  });
});

describe('плашка происхождения', () => {
  it('до загрузки стора — нейтральная «Задача», после — «Задача (удалена)»', () => {
    expect(resolveChatOrigin(chat())!.label).toBe('Задача');
    store.loaded = true;
    expect(resolveChatOrigin(chat())!.label).toBe('Задача (удалена)');
  });

  it('известная задача названа по заголовку', () => {
    store.loaded = true;
    store.tasks = [task()];
    expect(resolveChatOrigin(chat())!.label).toBe('Задача: Сделать экспорт');
  });
});
