import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../../types';
import {
  computeTodos,
  computeAgents,
  computeArtifacts,
  countFiles,
} from '../useSessionArtifacts';

// --- Фикстуры ChatItem ---

let _id = 0;
const nextId = () => `tu_${++_id}`;

const text = (t: string): ChatItem => ({ kind: 'text', text: t });

const fileChanged = (path: string, added = 1, removed = 0): ChatItem =>
  ({ kind: 'file_changed', path, added, removed });

type ToolExtra = Partial<Extract<ChatItem, { kind: 'tool_use' }>>;
const tool = (name: string, input: unknown, extra: ToolExtra = {}): ChatItem =>
  ({ kind: 'tool_use', id: nextId(), name, input, ...extra });

const result = (): ChatItem =>
  ({ kind: 'result', subtype: 'success', durationMs: 100, numTurns: 1 });

const planReview = (plan: string, resolved: boolean, approved?: boolean): ChatItem =>
  ({ kind: 'plan_review', requestId: nextId(), plan, resolved, approved });

const ROOT = 'C:\\Sources\\MyProject';

// toRelative переехала в src/lib/paths.ts — её тесты в src/lib/__tests__/paths.test.ts

// --- computeTodos ---

describe('computeTodos', () => {
  it('пустая лента → пустой список', () => {
    expect(computeTodos([])).toEqual([]);
  });

  it('TodoWrite: каждый вызов несёт полный список, последний побеждает', () => {
    const items: ChatItem[] = [
      tool('TodoWrite', { todos: [{ content: 'A', status: 'pending' }] }),
      tool('TodoWrite', {
        todos: [
          { content: 'A', status: 'completed' },
          { content: 'B', status: 'in_progress', activeForm: 'Делаю B' },
        ],
      }),
    ];
    expect(computeTodos(items)).toEqual([
      { content: 'A', status: 'completed' },
      { content: 'B', status: 'in_progress', activeForm: 'Делаю B' },
    ]);
  });

  it('TodoWrite с пустым списком не затирает предыдущий', () => {
    const items: ChatItem[] = [
      tool('TodoWrite', { todos: [{ content: 'A', status: 'pending' }] }),
      tool('TodoWrite', { todos: [] }),
    ];
    expect(computeTodos(items)).toEqual([{ content: 'A', status: 'pending' }]);
  });

  it('TaskCreate/TaskUpdate: инкрементальные, id из результата', () => {
    const items: ChatItem[] = [
      tool('TaskCreate', { subject: 'Задача раз' }, { result: 'Task #7 created successfully: Задача раз' }),
      tool('TaskUpdate', { taskId: '7', status: 'in_progress' }),
      tool('TaskUpdate', { taskId: '7', subject: 'Задача раз (уточнено)' }),
    ];
    expect(computeTodos(items)).toEqual([
      { content: 'Задача раз (уточнено)', status: 'in_progress', activeForm: undefined },
    ]);
  });

  it('TaskUpdate cancelled/deleted удаляет задачу', () => {
    const items: ChatItem[] = [
      tool('TaskCreate', { subject: 'A' }, { result: 'Task #1 created' }),
      tool('TaskCreate', { subject: 'B' }, { result: 'Task #2 created' }),
      tool('TaskUpdate', { taskId: '1', status: 'cancelled' }),
    ];
    expect(computeTodos(items)).toEqual([{ content: 'B', status: 'pending', activeForm: undefined }]);
  });

  it('TaskCreate без результата (стрим) — порядковый номер как id', () => {
    const items: ChatItem[] = [
      tool('TaskCreate', { subject: 'A' }),
      tool('TaskUpdate', { taskId: '1', status: 'in_progress' }),
    ];
    expect(computeTodos(items)).toEqual([{ content: 'A', status: 'in_progress', activeForm: undefined }]);
  });

  it('если есть и TodoWrite, и Task* — Task* побеждает', () => {
    const items: ChatItem[] = [
      tool('TodoWrite', { todos: [{ content: 'старое', status: 'pending' }] }),
      tool('TaskCreate', { subject: 'новое' }, { result: 'Task #1 created' }),
    ];
    expect(computeTodos(items).map(t => t.content)).toEqual(['новое']);
  });
});

// --- computeAgents ---

describe('computeAgents', () => {
  it('субагент без result и без конца хода → running, виден lastTool', () => {
    const parent = tool('Task', { description: 'Поиск по коду', subagent_type: 'Explore', prompt: 'Найди X' });
    const parentId = (parent as { id: string }).id;
    const items: ChatItem[] = [
      parent,
      tool('Grep', { pattern: 'foo' }, { parentToolUseId: parentId, result: 'ok' }),
      tool('Read', { file_path: 'C:\\p\\a.ts' }, { parentToolUseId: parentId }),
    ];
    const { agents, workflows } = computeAgents(items);
    expect(workflows).toEqual([]);
    expect(agents).toHaveLength(1);
    const a = agents[0];
    expect(a.status).toBe('running');
    expect(a.kind).toBe('subagent');
    expect(a.type).toBe('Explore');
    expect(a.label).toBe('Поиск по коду');
    expect(a.toolCount).toBe(2);
    expect(a.lastTool).toBe('Read');
    expect(a.calls?.map(c => c.name)).toEqual(['Grep', 'Read']);
    expect(a.calls?.[1].running).toBe(true);
  });

  it('субагент с result → done, resultText заполнен', () => {
    const items: ChatItem[] = [
      tool('Task', { description: 'Сборка', prompt: 'Собери' }, { result: 'Всё собралось' }),
    ];
    const a = computeAgents(items).agents[0];
    expect(a.status).toBe('done');
    expect(a.resultText).toBe('Всё собралось');
    expect(a.lastTool).toBeUndefined();
  });

  it('isError → error', () => {
    const items: ChatItem[] = [
      tool('Agent', { prompt: 'Сделай' }, { result: 'упало', isError: true }),
    ];
    expect(computeAgents(items).agents[0].status).toBe('error');
  });

  it('конец хода (result в ленте) закрывает незавершённого субагента', () => {
    const items: ChatItem[] = [
      tool('Task', { description: 'Оборванный' }),
      result(),
    ];
    expect(computeAgents(items).agents[0].status).toBe('done');
  });

  it('фоновый агент: result — подтверждение запуска, статус running, resultText пуст', () => {
    const items: ChatItem[] = [
      tool('Task', { description: 'Фоновый', run_in_background: true }, { result: 'started' }),
    ];
    const a = computeAgents(items).agents[0];
    expect(a.background).toBe(true);
    expect(a.status).toBe('running');
    expect(a.resultText).toBeUndefined();
  });

  it('фоновый агент остаётся running и после конца хода — пока нет bgDone', () => {
    const items: ChatItem[] = [
      tool('Task', { description: 'Фоновый', run_in_background: true }, { result: 'Async agent launched successfully' }),
      result(),
    ];
    expect(computeAgents(items).agents[0].status).toBe('running');
  });

  it('bgDone гасит running фонового агента → done', () => {
    const items: ChatItem[] = [
      tool('Task', { description: 'Фоновый', run_in_background: true },
        { result: 'Async agent launched successfully', bgDone: true }),
    ];
    const a = computeAgents(items).agents[0];
    expect(a.background).toBe(true);
    expect(a.status).toBe('done');
  });

  it('bgAborted → error (агент умер вместе с процессом, не доработав)', () => {
    const items: ChatItem[] = [
      tool('Task', { description: 'Фоновый', run_in_background: true },
        { result: 'Async agent launched successfully', bgDone: true, bgAborted: true }),
    ];
    expect(computeAgents(items).agents[0].status).toBe('error');
  });

  it('фоновость видна и без run_in_background — по квитанции запуска в result', () => {
    // resume агента: input без run_in_background, но result — квитанция CLI
    const items: ChatItem[] = [
      tool('Task', { description: 'Продолженный' }, { result: 'Agent resumed from transcript in the background' }),
    ];
    const a = computeAgents(items).agents[0];
    expect(a.background).toBe(true);
    expect(a.status).toBe('running');
  });

  it('label из первой строки prompt, если нет description', () => {
    const items: ChatItem[] = [
      tool('Task', { prompt: 'Первая строка\nвторая строка' }),
    ];
    expect(computeAgents(items).agents[0].label).toBe('Первая строка');
  });

  it('workflow: группа с агентами, doneCount и settled', () => {
    const wf = tool('Workflow', { script: "export const meta = { name: 'my-flow', description: 'Мой процесс' }" }, {
      workflowDone: true,
      workflowAgents: [
        { id: 'a1', prompt: 'Агент один', summary: 'Готово один', isDone: true, tools: [{ name: 'Read', count: 2 }] },
        { id: 'a2', prompt: 'Агент два', isDone: false },
      ],
    });
    const { agents, workflows } = computeAgents([wf]);
    expect(agents).toEqual([]);
    expect(workflows).toHaveLength(1);
    const g = workflows[0];
    expect(g.name).toBe('Мой процесс');
    expect(g.settled).toBe(true);
    expect(g.agents).toHaveLength(2);
    // workflow завершён — оба агента считаются done
    expect(g.doneCount).toBe(2);
    expect(g.agents[0].kind).toBe('workflow');
    expect(g.agents[0].label).toBe('Готово один');
    expect(g.agents[0].toolCount).toBe(2);
  });

  it('дочерние tool_use (parentToolUseId) не становятся отдельными агентами', () => {
    const parent = tool('Task', { description: 'Родитель' });
    const child = tool('Task', { description: 'Вложенный' }, { parentToolUseId: (parent as { id: string }).id });
    expect(computeAgents([parent, child]).agents).toHaveLength(1);
  });

  it('ФОНОВЫЙ workflow не settled по квитанции запуска — только по workflowDone', () => {
    // result — мгновенная квитанция («launched in background… Transcript dir:»), по ней
    // судить нельзя; и конец хода фоновому workflow не помеха — он живёт дальше
    const wf = tool('Workflow', { script: 'x' }, {
      result: 'Workflow launched in background.\nTranscript dir: C:\\tmp\\wf',
      workflowDone: false,
      workflowAgents: [{ id: 'a1', prompt: 'Агент', isDone: true }],
    });
    const items: ChatItem[] = [wf, result()];
    const g = computeAgents(items).workflows[0];
    expect(g.settled).toBe(false);
    // Пауза между волнами: все агенты isDone, но workflowDone=false — агент волны done, группа нет
    expect(g.doneCount).toBe(1);

    // Пришёл workflowDone=true → settled
    const done = tool('Workflow', { script: 'x' }, {
      result: 'Workflow launched in background.\nTranscript dir: C:\\tmp\\wf',
      workflowDone: true,
      workflowAgents: [{ id: 'a1', prompt: 'Агент', isDone: true }],
    });
    expect(computeAgents([done, result()]).workflows[0].settled).toBe(true);
  });

  it('блокирующий workflow settled по обычному result (не квитанции)', () => {
    const wf = tool('Workflow', { script: 'x' }, { result: 'Готово: 3 агента отработали' });
    expect(computeAgents([wf]).workflows[0].settled).toBe(true);
  });

  it('workflowAborted → settled (агенты уже не завершатся)', () => {
    const wf = tool('Workflow', { script: 'x' }, {
      result: 'Workflow launched in background.\nTranscript dir: C:\\tmp\\wf',
      workflowDone: false,
      workflowAborted: true,
      workflowAgents: [{ id: 'a1', prompt: 'Агент', isDone: false }],
    });
    expect(computeAgents([wf]).workflows[0].settled).toBe(true);
  });
});

// --- computeArtifacts: файлы и ссылки ---

describe('computeArtifacts', () => {
  it('file_changed → изменённый файл с дельтой', () => {
    const { files } = computeArtifacts([fileChanged('src/App.tsx', 5, 2)], ROOT);
    expect(files).toEqual([
      { path: 'src/App.tsx', added: 5, removed: 2, hasDelta: true, changed: true, external: false },
    ]);
  });

  it('tool_use Write с абсолютным путём внутри корня → относительный изменённый файл', () => {
    const items = [tool('Write', { file_path: 'C:\\Sources\\MyProject\\src\\new.ts' })];
    const { files } = computeArtifacts(items, ROOT);
    expect(files).toHaveLength(1);
    expect(files[0]).toMatchObject({ path: 'src/new.ts', changed: true, hasDelta: false });
  });

  it('Write вне корня проекта не попадает в файлы', () => {
    const items = [tool('Write', { file_path: 'D:\\elsewhere\\x.ts' })];
    expect(computeArtifacts(items, ROOT).files).toEqual([]);
  });

  it('дедуп по регистру и разделителям: file_changed + Edit одного файла — одна запись', () => {
    const items = [
      fileChanged('src/App.tsx', 3, 1),
      tool('Edit', { file_path: 'C:\\Sources\\MyProject\\src\\app.tsx' }),
    ];
    const { files } = computeArtifacts(items, ROOT);
    expect(files).toHaveLength(1);
    expect(files[0]).toMatchObject({ path: 'src/App.tsx', added: 3, removed: 1, hasDelta: true });
  });

  it('дельты нескольких file_changed суммируются', () => {
    const items = [fileChanged('a.ts', 1, 1), fileChanged('a.ts', 2, 3)];
    expect(computeArtifacts(items, ROOT).files[0]).toMatchObject({ added: 3, removed: 4 });
  });

  it('упомянутый в тексте путь внутри проекта → mentioned (changed=false)', () => {
    const items = [text('Смотри src/lib/api.ts и C:\\Sources\\MyProject\\src\\main.tsx')];
    const { files } = computeArtifacts(items, ROOT);
    expect(files.map(f => f.path).sort()).toEqual(['src/lib/api.ts', 'src/main.tsx']);
    expect(files.every(f => !f.changed && !f.external)).toBe(true);
  });

  it('абсолютный путь вне проекта в тексте → external, backslash нормализован', () => {
    const items = [text('Конфиг лежит в C:\\Other\\settings.json')];
    const { files } = computeArtifacts(items, ROOT);
    expect(files).toEqual([
      { path: 'C:/Other/settings.json', added: 0, removed: 0, hasDelta: false, changed: false, external: true },
    ]);
  });

  it('упоминание не перетирает изменённый файл', () => {
    const items = [fileChanged('src/App.tsx', 1, 0), text('Правил src/App.tsx')];
    const { files } = computeArtifacts(items, ROOT);
    expect(files).toHaveLength(1);
    expect(files[0].changed).toBe(true);
  });

  it('изменённые файлы сортируются выше упомянутых, последний тронутый — первым', () => {
    const items = [
      text('Про src/readme.md'),
      fileChanged('src/a.ts'),
      fileChanged('src/b.ts'),
    ];
    const { files } = computeArtifacts(items, ROOT);
    expect(files.map(f => f.path)).toEqual(['src/b.ts', 'src/a.ts', 'src/readme.md']);
  });

  it('ссылки: из текста (с обрезкой хвостовой пунктуации) и из WebFetch, с дедупом', () => {
    const items = [
      text('Читай https://www.example.com/docs. И ещё (https://other.io/page)'),
      tool('WebFetch', { url: 'https://www.example.com/docs' }),
    ];
    const { links } = computeArtifacts(items, ROOT);
    expect(links).toEqual([
      { url: 'https://www.example.com/docs', domain: 'example.com' },
      { url: 'https://other.io/page', domain: 'other.io' },
    ]);
  });

  it('URL не принимается за путь к файлу', () => {
    const items = [text('Скрипт на https://cdn.example.com/lib/app.min.js подключён')];
    const { files, links } = computeArtifacts(items, ROOT);
    expect(files).toEqual([]);
    expect(links).toHaveLength(1);
  });

  it('планы из plan_review со статусами', () => {
    const items = [
      planReview('План 1', true, true),
      planReview('План 2', true, false),
      planReview('План 3', false),
    ];
    expect(computeArtifacts(items, ROOT).plans).toEqual([
      { plan: 'План 1', status: 'approved' },
      { plan: 'План 2', status: 'rejected' },
      { plan: 'План 3', status: 'pending' },
    ]);
  });

  it('пустой rootPath (чат вне проекта): абсолютные пути инструментов отсекаются', () => {
    const items = [tool('Write', { file_path: 'C:\\anything\\a.ts' })];
    expect(computeArtifacts(items, '').files).toEqual([]);
  });
});

describe('countFiles', () => {
  it('совпадает с числом файлов из computeArtifacts на смешанной ленте', () => {
    const items: ChatItem[] = [
      fileChanged('src/App.tsx', 1, 0),
      tool('Write', { file_path: 'C:\\Sources\\MyProject\\src\\new.ts' }),
      tool('Edit', { file_path: 'C:\\Sources\\MyProject\\src\\app.tsx' }), // дубль App.tsx
      text('Упомянут src/lib/api.ts и внешний C:\\Other\\x.json'),
    ];
    const { files } = computeArtifacts(items, ROOT);
    expect(countFiles(items, ROOT)).toBe(files.length);
    expect(countFiles(items, ROOT)).toBe(4);
  });

  it('пустая лента → 0', () => {
    expect(countFiles([], ROOT)).toBe(0);
  });
});
