// MCP-сервер задач ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   TASKS_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   TASKS_API_TOKEN  — сервисный JWT владельца сессии
//   TASKS_PROJECT_ID — id проекта сессии; пусто = контекст личных задач

import { createInterface } from 'node:readline';

const API_URL = (process.env.TASKS_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.TASKS_API_TOKEN ?? '';
const PROJECT_ID = process.env.TASKS_PROJECT_ID || null;

// --- HTTP к бэкенду ---

async function api(path, options = {}) {
  const res = await fetch(`${API_URL}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      Authorization: `Bearer ${API_TOKEN}`,
      ...(options.headers ?? {}),
    },
  });
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`HTTP ${res.status}: ${body}`);
  }
  if (res.status === 204) return null;
  return res.json();
}

// Компактное представление задачи для ответа модели
function brief(t) {
  return {
    id: t.id,
    title: t.title,
    status: t.status,
    priority: t.priority,
    dueDate: t.dueDate ?? null,
    dueTime: t.dueTime ?? null,
    assignee: t.assignee ?? null,
    projectId: t.projectId ?? null,
    labels: t.labels,
    subtasks: `${(t.subtasks ?? []).filter(s => s.isDone).length}/${(t.subtasks ?? []).length}`,
  };
}

// --- Инструменты ---

const CONTEXT_NOTE = PROJECT_ID
  ? 'Контекст — текущий проект.'
  : 'Контекст — личные задачи пользователя (вне проектов).';

const ENUMS = {
  status: ['todo', 'inProgress', 'done'],
  priority: ['urgent', 'high', 'medium', 'low'],
  assignee: ['me', 'claude'],
};

const TOOLS = [
  {
    name: 'tasks_list',
    description: `Список задач. ${CONTEXT_NOTE} scope=all — все задачи пользователя по всем проектам и личные.`,
    inputSchema: {
      type: 'object',
      properties: {
        status: { type: 'string', enum: ENUMS.status, description: 'Фильтр по статусу' },
        priority: { type: 'string', enum: ENUMS.priority, description: 'Фильтр по приоритету' },
        assignee: { type: 'string', enum: ENUMS.assignee, description: 'Фильтр по исполнителю (me — пользователь, claude — Claude)' },
        scope: { type: 'string', enum: ['context', 'all'], description: 'context (по умолчанию) — текущий проект/личные; all — все задачи пользователя' },
      },
    },
  },
  {
    name: 'tasks_search',
    description: 'Поиск задач по названию, описанию и меткам — по всем задачам пользователя (все проекты + личные).',
    inputSchema: {
      type: 'object',
      required: ['query'],
      properties: {
        query: { type: 'string', description: 'Строка поиска' },
      },
    },
  },
  {
    name: 'tasks_get',
    description: 'Полная карточка задачи по id: описание (markdown), подзадачи, метки, срок.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID задачи' } },
    },
  },
  {
    name: 'tasks_create',
    description: `Создать задачу. ${CONTEXT_NOTE}`,
    inputSchema: {
      type: 'object',
      required: ['title'],
      properties: {
        title: { type: 'string', description: 'Название задачи' },
        description: { type: 'string', description: 'Описание (markdown)' },
        priority: { type: 'string', enum: ENUMS.priority, description: 'Приоритет (по умолчанию medium)' },
        dueDate: { type: 'string', description: 'Срок YYYY-MM-DD' },
        dueTime: { type: 'string', description: 'Время HH:MM' },
        assignee: { type: 'string', enum: ENUMS.assignee, description: 'Исполнитель' },
        subtasks: { type: 'array', items: { type: 'string' }, description: 'Названия подзадач' },
        labels: { type: 'array', items: { type: 'string' }, description: 'Метки' },
      },
    },
  },
  {
    name: 'tasks_update',
    description: 'Обновить поля задачи (передавать только изменяемые). Пустая строка в dueDate/dueTime очищает поле.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID задачи' },
        title: { type: 'string' },
        description: { type: 'string', description: 'Описание (markdown), заменяет целиком' },
        status: { type: 'string', enum: ENUMS.status },
        priority: { type: 'string', enum: ENUMS.priority },
        dueDate: { type: 'string', description: 'YYYY-MM-DD или "" чтобы убрать срок' },
        dueTime: { type: 'string', description: 'HH:MM или "" чтобы убрать время' },
        assignee: { type: 'string', enum: ENUMS.assignee },
        labels: { type: 'array', items: { type: 'string' }, description: 'Метки (заменяют список целиком)' },
      },
    },
  },
  {
    name: 'tasks_complete',
    description: 'Пометить задачу выполненной (status → done).',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID задачи' } },
    },
  },
  {
    name: 'tasks_delete',
    description: 'Удалить задачу безвозвратно.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID задачи' } },
    },
  },
  {
    name: 'tasks_add_subtask',
    description: 'Добавить подзадачу к задаче.',
    inputSchema: {
      type: 'object',
      required: ['taskId', 'title'],
      properties: {
        taskId: { type: 'string', description: 'ID родительской задачи' },
        title: { type: 'string', description: 'Название подзадачи' },
      },
    },
  },
  {
    name: 'tasks_toggle_subtask',
    description: 'Отметить подзадачу выполненной или снять отметку. Подзадачу можно указать по id или точному названию.',
    inputSchema: {
      type: 'object',
      required: ['taskId', 'isDone'],
      properties: {
        taskId: { type: 'string', description: 'ID задачи' },
        subtaskId: { type: 'string', description: 'ID подзадачи' },
        subtaskTitle: { type: 'string', description: 'Точное название подзадачи (если id неизвестен)' },
        isDone: { type: 'boolean', description: 'true — выполнена' },
      },
    },
  },
];

// --- Реализация инструментов ---

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

async function callTool(name, args) {
  switch (name) {
    case 'tasks_list': {
      const params = new URLSearchParams();
      for (const k of ['status', 'priority', 'assignee'])
        if (args[k]) params.set(k, String(args[k]));
      if (args.scope !== 'all') {
        if (PROJECT_ID) params.set('projectId', PROJECT_ID);
        else params.set('personal', 'true');
      }
      const data = await api(`/api/tasks?${params}`);
      return json(data.map(brief));
    }

    case 'tasks_search': {
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      const data = await api(`/api/tasks?${params}`);
      return json(data.map(brief));
    }

    case 'tasks_get':
      return json(await api(`/api/tasks/${args.id}`));

    case 'tasks_create': {
      const body = { title: args.title };
      for (const k of ['description', 'priority', 'dueDate', 'dueTime', 'assignee', 'labels'])
        if (args[k] !== undefined) body[k] = args[k];
      if (Array.isArray(args.subtasks) && args.subtasks.length)
        body.subtasks = args.subtasks.map(t => ({ title: String(t) }));
      const path = PROJECT_ID ? `/api/projects/${PROJECT_ID}/tasks` : '/api/tasks';
      return json(await api(path, { method: 'POST', body: JSON.stringify(body) }));
    }

    case 'tasks_update': {
      const { id, ...rest } = args;
      return json(await api(`/api/tasks/${id}`, { method: 'PUT', body: JSON.stringify(rest) }));
    }

    case 'tasks_complete':
      return json(await api(`/api/tasks/${args.id}`, {
        method: 'PUT', body: JSON.stringify({ status: 'done' }),
      }));

    case 'tasks_delete':
      await api(`/api/tasks/${args.id}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `Задача ${args.id} удалена.` }] };

    case 'tasks_add_subtask': {
      // Подзадачи обновляются списком целиком: читаем, добавляем, сохраняем
      const task = await api(`/api/tasks/${args.taskId}`);
      const subtasks = [...task.subtasks, { id: '', title: String(args.title), isDone: false }];
      return json(await api(`/api/tasks/${args.taskId}`, {
        method: 'PUT', body: JSON.stringify({ subtasks }),
      }));
    }

    case 'tasks_toggle_subtask': {
      const task = await api(`/api/tasks/${args.taskId}`);
      const match = s =>
        (args.subtaskId && s.id === args.subtaskId) ||
        (args.subtaskTitle && s.title === args.subtaskTitle);
      if (!task.subtasks.some(match))
        throw new Error('Подзадача не найдена — проверьте subtaskId/subtaskTitle через tasks_get');
      const subtasks = task.subtasks.map(s => match(s) ? { ...s, isDone: Boolean(args.isDone) } : s);
      return json(await api(`/api/tasks/${args.taskId}`, {
        method: 'PUT', body: JSON.stringify({ subtasks }),
      }));
    }

    default:
      throw new Error(`Неизвестный инструмент: ${name}`);
  }
}

// --- JSON-RPC поверх stdio (newline-delimited) ---

function send(msg) {
  process.stdout.write(JSON.stringify(msg) + '\n');
}

function reply(id, result) {
  send({ jsonrpc: '2.0', id, result });
}

function replyError(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

const rl = createInterface({ input: process.stdin, terminal: false });

rl.on('line', async line => {
  line = line.trim();
  if (!line) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }

  const { id, method, params } = msg;
  // Нотификации (без id) не требуют ответа
  if (id === undefined || id === null) return;

  try {
    switch (method) {
      case 'initialize':
        reply(id, {
          protocolVersion: params?.protocolVersion ?? '2024-11-05',
          capabilities: { tools: {} },
          serverInfo: { name: 'tasks', version: '1.0.0' },
        });
        break;
      case 'tools/list':
        reply(id, { tools: TOOLS });
        break;
      case 'tools/call': {
        try {
          reply(id, await callTool(params.name, params.arguments ?? {}));
        } catch (err) {
          // Ошибка инструмента — валидный результат с isError, не protocol error
          reply(id, {
            content: [{ type: 'text', text: `Ошибка: ${err?.message ?? err}` }],
            isError: true,
          });
        }
        break;
      }
      case 'ping':
        reply(id, {});
        break;
      default:
        replyError(id, -32601, `Метод не поддерживается: ${method}`);
    }
  } catch (err) {
    replyError(id, -32603, String(err?.message ?? err));
  }
});
