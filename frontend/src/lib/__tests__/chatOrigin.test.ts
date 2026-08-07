// Подпись чата-исполнителя задачи. Ключевое — не врать «Задача удалена» в двух гонках:
// (1) стор задач ещё не грузился ни разу (первая загрузка приложения) и
// (2) стор уже наполнен, но именно ЭТА (только что созданная) задача ещё не долетела
// своим task_changed — сессия чата-исполнителя бродкастится раньше неё (находки
// приёмки Э7 «Командной реализации» и волны 6).
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

  it.each(['starting', 'working', 'waiting'] as const)(
    'свежий дочерний чат (%s) не подписывает «Задача (удалена)», даже когда стор уже загружен, но эту задачу ещё не привезли',
    status => {
      store.loaded = true;   // стор наполнен раньше — но конкретной задачи в нём нет
      const info = describeTaskChat(chat({ status }))!;
      expect(info.fullLabel).toBe('Задача');
      expect(resolveChatOrigin(chat({ status }))!.label).toBe('Задача');
    },
  );

  it('после того как чат угомонился, а задачи в загруженном сторе всё ещё нет — она действительно удалена', () => {
    store.loaded = true;
    const info = describeTaskChat(chat({ status: 'finished' }))!;
    expect(info.status.kind).toBe('deleted');
    expect(info.fullLabel).toBe('Задача (удалена)');
    expect(resolveChatOrigin(chat({ status: 'finished' }))!.label).toBe('Задача (удалена)');
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
