// MCP-сервер долгой памяти персоны ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Окружение (задаёт ClaudeSession при запуске claude для персонной сессии):
//   MEMORY_API_URL     — базовый URL бэкенда (http://127.0.0.1:5000)
//   MEMORY_API_TOKEN   — сервисный JWT владельца сессии
//   MEMORY_PERSONA_ID  — id персоны, чья память доступна в этой сессии
//   MEMORY_PROJECT_ID  — id проекта персоны (③-3.4); пусто — персона глобальная,
//                        инструменты team_memory_* не регистрируются
//
// Память типизирована: semantic (устойчивые факты/предпочтения), episodic (что произошло
// в прошлых разговорах), procedural (выученные приёмы/правила поведения). Изоляция —
// на стороне backend: токен определяет владельца, persona_id — конкретную память.
//
// Память КОМАНДЫ проекта (③-3.4) — отдельное хранилище (TeamMemoryService), общее для
// ВСЕХ персон проекта: плоский список фактов без типов, recall'ится наравне с личной
// памятью в системный промпт каждого хода. team_memory_* — то же CRUD, что и у ручного
// ввода через UI «Командный центр», но доступное персоне прямо из разговора.

import { createInterface } from 'node:readline';

const API_URL = (process.env.MEMORY_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.MEMORY_API_TOKEN ?? '';
const PERSONA_ID = process.env.MEMORY_PERSONA_ID ?? '';
const PROJECT_ID = process.env.MEMORY_PROJECT_ID ?? '';

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

const base = `/api/personas/${encodeURIComponent(PERSONA_ID)}/memory`;
const teamBase = `/api/projects/${encodeURIComponent(PROJECT_ID)}/team-memory`;

const TOOLS = [
  {
    name: 'memory_remember',
    description: 'Запомнить что-то в свою долгую память. type: "semantic" — устойчивый факт или ' +
      'предпочтение пользователя; "episodic" — что произошло/обсуждалось (событие, итог разговора); ' +
      '"procedural" — выученный приём или правило поведения. Запоминай лаконично, по одной мысли на запись.',
    inputSchema: {
      type: 'object',
      required: ['type', 'text'],
      properties: {
        type: { type: 'string', enum: ['semantic', 'episodic', 'procedural'], description: 'Тип памяти' },
        text: { type: 'string', description: 'Что запомнить (кратко, одна мысль)' },
        tags: { type: 'array', items: { type: 'string' }, description: 'Необязательные теги для группировки' },
        salience: {
          type: 'number', minimum: 0, maximum: 1,
          description: 'Важность записи 0..1 (1 — критично помнить, 0.3 — мелочь); по умолчанию 1',
        },
      },
    },
  },
  {
    name: 'memory_search',
    description: 'Поиск по своей долгой памяти по смыслу: возвращает релевантные записи со score ' +
      '(учитывает свежесть и тип). Используй, когда нужно вспомнить, что известно по теме.',
    inputSchema: {
      type: 'object',
      required: ['query'],
      properties: {
        query: { type: 'string', description: 'Смысловой запрос' },
        topK: { type: 'integer', minimum: 1, maximum: 20, description: 'Сколько записей (по умолчанию 8)' },
      },
    },
  },
  {
    name: 'memory_list',
    description: 'Перечислить записи памяти (можно сузить по типу). Полезно для обзора того, что уже запомнено.',
    inputSchema: {
      type: 'object',
      properties: {
        type: { type: 'string', enum: ['semantic', 'episodic', 'procedural'], description: 'Фильтр по типу' },
      },
    },
  },
  {
    name: 'memory_forget',
    description: 'Удалить запись памяти по id (например, факт устарел или оказался неверным).',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID записи памяти' } },
    },
  },
];

// team_memory_* — только у проектных персон (PROJECT_ID задан). Память команды не типизирована
// (в отличие от personal semantic/episodic/procedural) — плоский список общих фактов проекта.
const TEAM_TOOLS = [
  {
    name: 'team_memory_remember',
    description: 'Запомнить факт в общую память КОМАНДЫ проекта — увидят и смогут использовать ' +
      'ВСЕ персоны проекта, не только ты. Пиши сюда то, что относится к проекту в целом (общие ' +
      'договорённости, структура данных, ограничения), а не личные заметки о себе.',
    inputSchema: {
      type: 'object',
      required: ['text'],
      properties: { text: { type: 'string', description: 'Общий факт/договорённость проекта (кратко)' } },
    },
  },
  {
    name: 'team_memory_list',
    description: 'Перечислить всё, что команда проекта уже знает (общая память, не личная).',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'team_memory_forget',
    description: 'Удалить запись из общей памяти команды проекта по id (устарела/оказалась неверной).',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID записи командной памяти' } },
    },
  },
];

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

async function callTool(name, args) {
  switch (name) {
    case 'memory_remember': {
      const body = { type: args.type, text: args.text };
      if (Array.isArray(args.tags) && args.tags.length) body.tags = args.tags;
      if (typeof args.salience === 'number') body.salience = args.salience;
      return json(await api(base, { method: 'POST', body: JSON.stringify(body) }));
    }

    case 'memory_search': {
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      if (args.topK) params.set('topK', String(args.topK));
      return json(await api(`${base}/search?${params}`));
    }

    case 'memory_list': {
      const params = new URLSearchParams();
      if (args.type) params.set('type', String(args.type));
      const qs = params.toString();
      return json(await api(`${base}${qs ? '?' + qs : ''}`));
    }

    case 'memory_forget':
      await api(`${base}/${encodeURIComponent(args.id)}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `Запись ${args.id} удалена из памяти.` }] };

    case 'team_memory_remember':
      return json(await api(teamBase, { method: 'POST', body: JSON.stringify({ text: args.text }) }));

    case 'team_memory_list':
      return json(await api(teamBase));

    case 'team_memory_forget':
      await api(`${teamBase}/${encodeURIComponent(args.id)}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `Запись ${args.id} удалена из памяти команды.` }] };

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
          serverInfo: { name: 'memory', version: '1.0.0' },
        });
        break;
      case 'tools/list':
        reply(id, { tools: PROJECT_ID ? [...TOOLS, ...TEAM_TOOLS] : TOOLS });
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
