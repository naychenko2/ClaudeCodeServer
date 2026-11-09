import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Task } from '../../types';

// Облегчённый список задач (listItem): стор не должен терять уже загруженный полный текст
// при перезагрузке списка, а ensureFullTask догружает карточку по id ровно одним запросом.

const mocks = vi.hoisted(() => ({
  listAll: vi.fn(),
  get: vi.fn(),
}));

vi.mock('../api', () => ({ api: { tasks: { listAll: mocks.listAll, get: mocks.get } } }));
vi.mock('../signalr', () => ({
  joinUser: vi.fn(async () => {}), onMessage: vi.fn(), onReconnected: vi.fn(),
}));
vi.mock('../idb', () => ({
  idbGet: vi.fn(async () => undefined), idbSet: vi.fn(async () => {}),
  outboxAll: vi.fn(async () => []), outboxGet: vi.fn(async () => undefined),
  outboxPut: vi.fn(async () => {}), outboxDelete: vi.fn(async () => {}),
}));

type TasksModule = typeof import('../tasks');
let m: TasksModule;

function task(over: Partial<Task>): Task {
  return {
    id: 't1', title: 'T', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 1000,
    createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z', ...over,
  } as Task;
}

const light = (over: Partial<Task> = {}) => task({
  listItem: true, hasDescription: true, hasResult: true, descriptionPreview: 'нач…', ...over,
});
const full = (over: Partial<Task> = {}) => task({
  description: 'полное описание', resultMarkdown: 'итог', ...over,
});

beforeEach(async () => {
  vi.resetModules();
  mocks.listAll.mockReset();
  mocks.get.mockReset();
  m = await import('../tasks');
});

describe('keepLoadedFull — подмена облегчённым не затирает полный текст', () => {
  it('тот же updatedAt → остаётся полная версия', () => {
    const f = full();
    expect(m.keepLoadedFull([light()], [f])[0]).toBe(f);
  });

  it('updatedAt изменился на сервере → берём облегчённую (полный текст устарел)', () => {
    const l = light({ updatedAt: '2026-10-02T00:00:00Z' });
    expect(m.keepLoadedFull([l], [full()])[0]).toBe(l);
  });

  it('полной версии в сторе нет → облегчённая как есть', () => {
    const l = light();
    expect(m.keepLoadedFull([l], [])[0]).toBe(l);
  });

  it('полный элемент списка не подменяется (listItem нет)', () => {
    const srv = full({ description: 'с сервера' });
    expect(m.keepLoadedFull([srv], [full()])[0]).toBe(srv);
  });
});

describe('keepLoadedFull — задача открыта на правку: полную версию не подменяем', () => {
  it('открыта на правку + новый updatedAt → остаётся полная; после unmarkEditing подменяется как обычно', async () => {
    // Стор: сначала лёгкая, затем догружаем полную (как в реальном сценарии)
    mocks.listAll.mockResolvedValue([light()]);
    mocks.get.mockResolvedValue(full());
    await m.reloadTasks();
    await m.ensureFullTask('t1');
    expect(m.getTaskById('t1')?.listItem).toBeUndefined();

    // Задача открыта на правку
    m.markEditing('t1');
    // Сервер отдаёт облегчённый элемент с НОВЫМ updatedAt
    mocks.listAll.mockResolvedValue([light({ updatedAt: '2026-10-02T00:00:00Z' })]);
    await m.reloadTasks();
    // В сторе остаётся полная версия (не облегчённая)
    expect(m.getTaskById('t1')?.listItem).toBeUndefined();
    expect(m.getTaskById('t1')?.description).toBe('полное описание');

    // После снятия отметки о правке следующая перезагрузка подменяет как обычно
    m.unmarkEditing('t1');
    await m.reloadTasks();
    expect(m.getTaskById('t1')?.listItem).toBe(true);
  });
});

describe('ensureFullTask — догрузка полной карточки', () => {
  it('облегчённая задача заменяется полной, listItem исчезает', async () => {
    mocks.listAll.mockResolvedValue([light()]);
    mocks.get.mockResolvedValue(full());
    await m.reloadTasks();
    expect(m.getTaskById('t1')?.listItem).toBe(true);
    expect(m.getTaskById('t1')?.description).toBe('');

    await m.ensureFullTask('t1');

    const t = m.getTaskById('t1')!;
    expect(t.listItem).toBeUndefined();
    expect(t.description).toBe('полное описание');
    expect(t.resultMarkdown).toBe('итог');
  });

  it('параллельные вызовы делают один запрос, повтор после загрузки — ни одного', async () => {
    mocks.listAll.mockResolvedValue([light()]);
    mocks.get.mockResolvedValue(full());
    await m.reloadTasks();

    await Promise.all([m.ensureFullTask('t1'), m.ensureFullTask('t1')]);
    await m.ensureFullTask('t1');

    expect(mocks.get).toHaveBeenCalledTimes(1);
  });

  it('перезагрузка списка после догрузки не затирает полный текст', async () => {
    mocks.listAll.mockResolvedValue([light()]);
    mocks.get.mockResolvedValue(full());
    await m.reloadTasks();
    await m.ensureFullTask('t1');

    await m.reloadTasks();   // сервер снова отдаёт облегчённый элемент

    expect(m.getTaskById('t1')?.description).toBe('полное описание');
    expect(m.getTaskById('t1')?.listItem).toBeUndefined();
  });

  it('ошибка запроса пробрасывается, задача остаётся облегчённой и догружается повторно', async () => {
    mocks.listAll.mockResolvedValue([light()]);
    mocks.get.mockRejectedValueOnce(new Error('сеть'));
    await m.reloadTasks();

    await expect(m.ensureFullTask('t1')).rejects.toThrow('сеть');
    expect(m.getTaskById('t1')?.listItem).toBe(true);

    mocks.get.mockResolvedValue(full());
    await m.ensureFullTask('t1');
    expect(m.getTaskById('t1')?.description).toBe('полное описание');
  });

  it('задачи нет в сторе (снимок обогнал стор) — догружается и добавляется', async () => {
    mocks.get.mockResolvedValue(full());

    await m.ensureFullTask('t1');

    expect(m.getTaskById('t1')?.description).toBe('полное описание');
  });

  it('задача удалена, пока грузили, — в стор не возвращается', async () => {
    mocks.listAll.mockResolvedValue([light()]);
    let resolveGet!: (t: Task) => void;
    mocks.get.mockReturnValue(new Promise<Task>(r => { resolveGet = r; }));
    await m.reloadTasks();

    const p = m.ensureFullTask('t1');
    mocks.listAll.mockResolvedValue([]);
    await m.reloadTasks();
    resolveGet(full());
    await p;

    expect(m.getTaskById('t1')).toBeUndefined();
  });
});
