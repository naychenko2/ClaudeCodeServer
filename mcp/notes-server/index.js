// MCP-сервер заметок ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   NOTES_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   NOTES_API_TOKEN  — сервисный JWT владельца сессии
//   NOTES_PROJECT_ID — id проекта сессии; пусто = контекст личного vault
//
// Заметки — это .md файлы (Obsidian-совместимо): личный vault + notes/ проектов
// владельца. Граф связей [[wikilinks]] единый per-owner поверх всех источников.

import { createInterface } from 'node:readline';

const API_URL = (process.env.NOTES_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.NOTES_API_TOKEN ?? '';
const PROJECT_ID = process.env.NOTES_PROJECT_ID || null;

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

// Компактное представление заметки для списков
function brief(n) {
  return {
    id: n.id,
    title: n.title,
    source: n.source,
    sourceLabel: n.sourceLabel,
    tags: n.tags,
    updatedAt: n.updatedAt,
  };
}

// Куда по умолчанию создаётся заметка
const DEFAULT_SOURCE = PROJECT_ID || 'personal';
const CONTEXT_NOTE = PROJECT_ID
  ? 'По умолчанию заметки создаются в notes/ текущего проекта. source="personal" — в личный vault.'
  : 'По умолчанию заметки создаются в личный vault пользователя. source=<projectId> — в notes/ проекта.';

const TOOLS = [
  {
    name: 'notes_list',
    description: 'Список заметок пользователя по всем источникам (личный vault + notes/ его проектов). Можно сузить фильтром source.',
    inputSchema: {
      type: 'object',
      properties: {
        source: { type: 'string', description: 'Фильтр по источнику: "personal" или id проекта' },
      },
    },
  },
  {
    name: 'notes_search',
    description: 'Поиск заметок по заголовку, тексту и тегам — по всем источникам пользователя.',
    inputSchema: {
      type: 'object',
      required: ['query'],
      properties: { query: { type: 'string', description: 'Строка поиска' } },
    },
  },
  {
    name: 'notes_read',
    description: 'Прочитать заметку целиком по id: markdown-содержимое, теги, исходящие связи [[...]] и обратные ссылки.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID заметки' } },
    },
  },
  {
    name: 'notes_create',
    description: `Создать заметку (.md). В тексте связывай с другими заметками через [[Заголовок]]. ${CONTEXT_NOTE}`,
    inputSchema: {
      type: 'object',
      required: ['title'],
      properties: {
        title: { type: 'string', description: 'Заголовок (= имя файла)' },
        content: { type: 'string', description: 'Текст заметки (markdown, можно с [[wikilinks]] и frontmatter)' },
        source: { type: 'string', description: 'Куда создать: "personal" или id проекта. По умолчанию — контекст сессии' },
      },
    },
  },
  {
    name: 'notes_update',
    description: 'Обновить заметку: заменить содержимое и/или переименовать (смена title переименует файл). Передавай только изменяемые поля.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID заметки' },
        title: { type: 'string', description: 'Новый заголовок (переименует файл)' },
        content: { type: 'string', description: 'Новое содержимое (markdown), заменяет целиком' },
      },
    },
  },
  {
    name: 'notes_backlinks',
    description: 'Обратные ссылки заметки: какие заметки ссылаются на неё через [[...]], с контекстом.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID заметки' } },
    },
  },
  {
    name: 'notes_graph',
    description: 'Граф связей всех заметок пользователя: узлы (заметки + «призрачные» несозданные) и рёбра.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'notes_delete',
    description: 'Удалить заметку (файл) безвозвратно.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID заметки' } },
    },
  },
];

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

async function callTool(name, args) {
  switch (name) {
    case 'notes_list': {
      const params = new URLSearchParams();
      if (args.source) params.set('source', String(args.source));
      const qs = params.toString();
      const data = await api(`/api/notes${qs ? '?' + qs : ''}`);
      return json(data.map(brief));
    }

    case 'notes_search': {
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      const data = await api(`/api/notes?${params}`);
      return json(data.map(brief));
    }

    case 'notes_read':
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}`));

    case 'notes_create': {
      const body = { title: args.title };
      if (args.content !== undefined) body.content = args.content;
      body.source = args.source ?? DEFAULT_SOURCE;
      return json(await api('/api/notes', { method: 'POST', body: JSON.stringify(body) }));
    }

    case 'notes_update': {
      const body = {};
      if (args.title !== undefined) body.title = args.title;
      if (args.content !== undefined) body.content = args.content;
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}`, {
        method: 'PUT', body: JSON.stringify(body),
      }));
    }

    case 'notes_backlinks':
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}/backlinks`));

    case 'notes_graph':
      return json(await api('/api/notes/graph'));

    case 'notes_delete':
      await api(`/api/notes/${encodeURIComponent(args.id)}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `Заметка ${args.id} удалена.` }] };

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
  if (id === undefined || id === null) return;

  try {
    switch (method) {
      case 'initialize':
        reply(id, {
          protocolVersion: params?.protocolVersion ?? '2024-11-05',
          capabilities: { tools: {} },
          serverInfo: { name: 'notes', version: '1.0.0' },
        });
        break;
      case 'tools/list':
        reply(id, { tools: TOOLS });
        break;
      case 'tools/call': {
        try {
          reply(id, await callTool(params.name, params.arguments ?? {}));
        } catch (err) {
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
