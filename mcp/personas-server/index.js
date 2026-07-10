// MCP-сервер персон ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Даёт сессии инструменты «команды персон»: узнать, кто доступен (persona_list),
// и спросить другую персону (persona_ask) — она ответит one-shot'ом от своего лица,
// со своим характером, памятью и моделью. Глубина делегирования строго 1:
// one-shot, запущенный persona_ask, этот сервер уже не получает.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   PERSONAS_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   PERSONAS_API_TOKEN  — сервисный JWT владельца сессии
//   PERSONAS_PROJECT_ID — проект сессии (пусто = чат вне проекта: только глобальные персоны)
//   PERSONAS_SELF_ID    — id персоны текущей сессии (исключается из списка; пусто = обычный чат)

import { createInterface } from 'node:readline';

const API_URL = (process.env.PERSONAS_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.PERSONAS_API_TOKEN ?? '';
const PROJECT_ID = process.env.PERSONAS_PROJECT_ID ?? '';
const SELF_ID = process.env.PERSONAS_SELF_ID ?? '';

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

const TOOLS = [
  {
    name: 'persona_list',
    description: 'Список персон-ассистентов пользователя, доступных в этом контексте, — кого можно ' +
      'спросить через persona_ask. Возвращает handle, роль, имя и описание каждой.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'persona_ask',
    description: 'Спросить другую персону: она ответит от своего лица, в своём характере и со своей ' +
      'долгой памятью. Используй, когда пользователь упоминает @handle персоны или когда нужна её ' +
      'экспертиза (например, мнение ревьюера о плане). Вопрос формулируй самодостаточно — персона ' +
      'не видит этот разговор; важный контекст передай в поле context.',
    inputSchema: {
      type: 'object',
      required: ['handle', 'question'],
      properties: {
        handle: { type: 'string', description: 'handle персоны (без @), см. persona_list' },
        question: { type: 'string', description: 'Самодостаточный вопрос к персоне' },
        context: { type: 'string', description: 'Необязательный контекст разговора (кратко, только нужное для ответа)' },
      },
    },
  },
];

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

async function callTool(name, args) {
  switch (name) {
    case 'persona_list': {
      const params = new URLSearchParams({ scope: 'context' });
      if (PROJECT_ID) params.set('projectId', PROJECT_ID);
      const personas = await api(`/api/personas?${params}`);
      const list = (personas ?? [])
        .filter(p => p.id !== SELF_ID)
        .map(p => ({ handle: p.handle, role: p.role ?? null, name: p.name, description: p.description ?? null }));
      return json(list);
    }

    case 'persona_ask': {
      const body = {
        handle: String(args.handle ?? '').replace(/^@/, ''),
        question: String(args.question ?? ''),
      };
      if (args.context) body.context = String(args.context);
      const res = await api('/api/personas/ask', { method: 'POST', body: JSON.stringify(body) });
      return { content: [{ type: 'text', text: res?.answer ?? '' }] };
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
  if (id === undefined || id === null) return;

  try {
    switch (method) {
      case 'initialize':
        reply(id, {
          protocolVersion: params?.protocolVersion ?? '2024-11-05',
          capabilities: { tools: {} },
          serverInfo: { name: 'personas', version: '1.0.0' },
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
