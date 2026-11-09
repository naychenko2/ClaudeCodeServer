import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Task } from '../../types';

// И3: вкладка держит не больше FULL_TASKS_CAP полных задач (LRU открытых), кроме задач
// «открыт на правку» и с pending в outbox. Полные задачи из realtime task_changed
// облегчаются на входе в стор; облегчает одна функция toListItem.

const mocks = vi.hoisted(() => ({
  listAll: vi.fn(),
  get: vi.fn(),
  handler: null as null | ((msg: unknown) => void),
  idbSet: vi.fn(async () => {}),
  idbGet: vi.fn(async (): Promise<unknown> => undefined),
  pending: new Set<string>(),
}));

vi.mock('../api', () => ({ api: { tasks: { listAll: mocks.listAll, get: mocks.get } } }));
vi.mock('../signalr', () => ({
  joinUser: vi.fn(async () => {}),
  onMessage: (fn: (msg: unknown) => void) => { mocks.handler = fn; },
  onReconnected: vi.fn(),
}));
vi.mock('../themeMode', () => ({ getEffectiveTheme: () => 'light' }));
vi.mock('../idb', () => ({
  idbGet: mocks.idbGet, idbSet: mocks.idbSet,
  idbKeys: vi.fn(async () => []), idbDelete: vi.fn(async () => {}),
  outboxAll: vi.fn(async () => []), outboxGet: vi.fn(async () => undefined),
  outboxPut: vi.fn(async () => {}), outboxDelete: vi.fn(async () => {}),
}));
vi.mock('../taskOutbox', async importOriginal => ({
  ...(await importOriginal<typeof import('../taskOutbox')>()),
  outboxHasPending: (id: string) => mocks.pending.has(id),
}));

type TasksModule = typeof import('../tasks');
let m: TasksModule;

const full = (id: string, over: Partial<Task> = {}): Task => ({
  id, title: id, description: `описание ${id}`, resultMarkdown: `итог ${id}`,
  status: 'todo', priority: 'medium', linkedFiles: [], subtasks: [], labels: [], order: 1000,
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z', ...over,
} as Task);

const fire = (task: Task) => mocks.handler?.({ type: 'task_changed', action: 'updated', task });
let readStore: () => Task[];
let ids: string[] = [];
const fulls = () => readStore().filter(t => !t.listItem);

beforeEach(async () => {
  vi.resetModules();
  mocks.listAll.mockReset();
  mocks.get.mockReset();
  mocks.idbSet.mockClear();
  mocks.idbGet.mockReset();
  mocks.idbGet.mockResolvedValue(undefined);
  mocks.pending.clear();
  mocks.handler = null;
  m = await import('../tasks');
  mocks.listAll.mockResolvedValue([]);
  await m.ensureTasksLoaded();
  // Чтение стора без React: по id, которые тест подавал через feed
  readStore = () => ids.map(id => m.getTaskById(id)).filter((t): t is Task => !!t);
  ids = [];
});

const feed = (task: Task) => { if (!ids.includes(task.id)) ids.push(task.id); fire(task); };

describe('toListItem — форма как у бэкового TaskListItem.From', () => {
  it('description пуст, resultMarkdown нет, признаки и превью', () => {
    const l = m.toListItem(full('a'));
    expect(l.listItem).toBe(true);
    expect(l.description).toBe('');
    expect('resultMarkdown' in l).toBe(false);
    expect(l.hasDescription).toBe(true);
    expect(l.hasResult).toBe(true);
    expect(l.descriptionPreview).toBe('описание a');
  });

  it('пустое описание и нет итога → hasDescription/hasResult = false', () => {
    const l = m.toListItem(full('a', { description: '', resultMarkdown: undefined }));
    expect(l.hasDescription).toBe(false);
    expect(l.hasResult).toBe(false);
    expect(l.descriptionPreview).toBe('');
  });

  it('превью ровно 200 символов — без «…»; 201 — усечено до 200 плюс «…»', () => {
    expect(m.toListItem(full('a', { description: 'x'.repeat(200) })).descriptionPreview).toBe('x'.repeat(200));
    expect(m.toListItem(full('a', { description: 'x'.repeat(201) })).descriptionPreview).toBe('x'.repeat(200) + '…');
  });

  it('суррогатная пара на границе не разрывается', () => {
    // 199 символов + эмодзи (2 code unit): high-surrogate стоит на позиции 200
    const l = m.toListItem(full('a', { description: 'x'.repeat(199) + '😀' + 'y' }));
    expect(l.descriptionPreview).toBe('x'.repeat(199) + '…');
  });

  it('уже облегчённая возвращается как есть', () => {
    const l = m.toListItem(full('a'));
    expect(m.toListItem(l)).toBe(l);
  });
});

describe('realtime: прилетевшие полные задачи облегчаются', () => {
  it('2000 task_changed → полных ≤ cap + _editing + pending', () => {
    m.markEditing('t0');
    // Открытые человеком — через ensureFullTask-путь (touch) делаем отдельным тестом ниже
    for (let i = 0; i < 2000; i++) {
      if (i === 5) { feed(full('t1')); mocks.pending.add('t1'); m.upsertTaskLocal(full('t1')); }
      feed(full(`t${i}`));
    }
    const f = fulls();
    expect(f.length).toBeLessThanOrEqual(m.FULL_TASKS_CAP + 1 + 1);
    expect(f.map(t => t.id).sort()).toEqual(['t0', 't1']);
    expect(readStore()).toHaveLength(2000);
  });

  it('задача в _editing и задача с pending не понижаются никогда', () => {
    m.markEditing('e');
    feed(full('e', { description: 'правлю' }));
    feed(full('p', { description: 'офлайн-правка' }));   // realtime по pending-задаче игнорируется
    mocks.pending.add('p');
    m.upsertTaskLocal(full('p', { description: 'офлайн-правка' }));
    for (let i = 0; i < 100; i++) feed(full(`x${i}`));
    for (let i = 0; i < 100; i++) feed(full('e', { description: 'правлю ' + i }));
    expect(m.getTaskById('e')?.listItem).toBeUndefined();
    expect(m.getTaskById('p')?.listItem).toBeUndefined();
    expect(m.getTaskById('p')?.description).toBe('офлайн-правка');
    // После правки остаётся полной как недавно открытая (LRU), а не вечно
    m.unmarkEditing('e');
    expect(m.getTaskById('e')?.listItem).toBeUndefined();
  });

  it('realtime по другой задаче не трогает открытую (LRU)', async () => {
    mocks.get.mockResolvedValue(full('open'));
    feed({ ...m.toListItem(full('open')) });
    await m.ensureFullTask('open');
    expect(m.getTaskById('open')?.listItem).toBeUndefined();
    for (let i = 0; i < 500; i++) feed(full(`o${i}`));
    expect(m.getTaskById('open')?.listItem).toBeUndefined();
    expect(m.getTaskById('open')?.description).toBe('описание open');
  });
});

describe('LRU открытых задач', () => {
  it('открыто cap+5 задач → полных ровно cap, самые старые облегчены', async () => {
    const n = m.FULL_TASKS_CAP + 5;
    for (let i = 0; i < n; i++) feed(m.toListItem(full(`k${i}`)));
    mocks.get.mockImplementation(async (id: string) => full(id));
    for (let i = 0; i < n; i++) await m.ensureFullTask(`k${i}`);
    const f = fulls();
    expect(f).toHaveLength(m.FULL_TASKS_CAP);
    expect(m.getTaskById('k0')?.listItem).toBe(true);
    expect(m.getTaskById(`k${n - 1}`)?.listItem).toBeUndefined();
  });
});

describe('снапшот tasks:all — тоже облегчённый', () => {
  it('в IndexedDB уходят только лёгкие (кроме защищённых)', async () => {
    vi.useFakeTimers();
    try {
      vi.resetModules();
      m = await import('../tasks');
      mocks.listAll.mockResolvedValue([]);
      await m.ensureTasksLoaded();
      feed(full('p'));
      mocks.pending.add('p');
      m.upsertTaskLocal(full('p'));
      for (let i = 0; i < 50; i++) feed(full(`s${i}`));
      await vi.advanceTimersByTimeAsync(11_000);
      const call = mocks.idbSet.mock.calls.find(c => (c as unknown[])[0] === 'tasks:all') as unknown[] | undefined;
      const data = (call?.[1] as { data: Task[] }).data;
      expect(data.filter(t => !t.listItem).map(t => t.id)).toEqual(['p']);
      expect(JSON.stringify(data)).not.toContain('"description":"описание s');
    } finally { vi.useRealTimers(); }
  });

  it('гидрация старого снапшота с полными задачами: полных не больше cap', async () => {
    vi.resetModules();
    mocks.idbGet.mockResolvedValue({
      data: Array.from({ length: 80 }, (_, i) => full(`h${i}`)), savedAt: 1,
    });
    mocks.listAll.mockRejectedValue(Object.assign(new Error('offline'), {}));
    m = await import('../tasks');
    mocks.listAll.mockImplementation(() => new Promise(() => {}));
    void m.ensureTasksLoaded();
    await vi.waitFor(() => expect(m.getTaskById('h79')).toBeDefined());
    const all = Array.from({ length: 80 }, (_, i) => m.getTaskById(`h${i}`)!);
    expect(all.filter(t => !t.listItem)).toHaveLength(m.FULL_TASKS_CAP);
    // Недавние (хвост снапшота) остались полными, у остальных — превью
    expect(m.getTaskById('h79')?.listItem).toBeUndefined();
    expect(m.getTaskById('h0')?.descriptionPreview).toBe('описание h0');
  });
});
