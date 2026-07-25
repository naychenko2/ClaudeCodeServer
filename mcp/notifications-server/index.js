// MCP-сервер уведомлений ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   NOTIFICATIONS_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   NOTIFICATIONS_API_TOKEN  — сервисный JWT владельца сессии
//   NOTIFICATIONS_PROJECT_ID — id текущей сессии (для контекста)
//
// Инструменты:
//   notifications_create   — создать уведомление (из персоны, задачи, системы)
//   notifications_list     — список уведомлений с фильтрацией
//   notifications_mark_read — отметить прочитанным
//   notifications_delete   — удалить уведомление

import { createInterface } from 'node:readline';

const API_URL = (process.env.NOTIFICATIONS_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.NOTIFICATIONS_API_TOKEN ?? '';
const PROJECT_ID = process.env.NOTIFICATIONS_PROJECT_ID || null;
// Персона текущей сессии — проставляется в уведомление как personaId (лицо персоны).
const SELF_PERSONA_ID = process.env.NOTIFICATIONS_SELF_PERSONA_ID || null;

// Секундная недоступность бэкенда (рестарт, деплой, перезапуск прокси) превращалась в серию
// красных карточек: ретраев не было ни одного. Паузы короткие — вызов инструмента не должен
// висеть минутами, а бэкенд поднимается быстро.
const RETRY_DELAYS_MS = [300, 900];
const RETRIABLE_STATUS = new Set([408, 425, 429, 500, 502, 503, 504]);
const sleep = ms => new Promise(r => setTimeout(r, ms));

// Ошибка соединения: запрос ТОЧНО не дошёл до бэкенда — повторять безопасно даже для мутаций
const isConnectionError = err =>
  ['ECONNREFUSED', 'ENOTFOUND', 'EAI_AGAIN'].includes(err?.cause?.code);

// Сеть или таймаут: запрос мог и дойти — повторяем только идемпотентные чтения
const isNetworkError = err =>
  err?.name === 'TimeoutError' || err?.name === 'AbortError'
  || err?.cause?.code !== undefined || /fetch failed/i.test(err?.message ?? '');

function shouldRetry(err, method, attempt) {
  if (attempt >= RETRY_DELAYS_MS.length) return false;
  if (isConnectionError(err)) return true;
  // Повтор POST/PUT/DELETE мог бы задвоить задачу или запустить второго исполнителя —
  // мутации повторяем только когда точно известно, что запрос не дошёл (выше)
  if ((method ?? 'GET').toUpperCase() !== 'GET') return false;
  return err?.status ? RETRIABLE_STATUS.has(err.status) : isNetworkError(err);
}

// Текст ошибки для модели с ЯВНЫМ классом. Без него «HTTP 503», «HTTP 403» и «задача
// принадлежит другому проекту» выглядят одинаково — в истории прода видно, как после первой
// красной карточки модель бросала попытки, даже когда сбой был временный.
function describeError(err) {
  if (isNetworkError(err))
    return 'Временный сбой связи с сервером'
      + (err?.name === 'TimeoutError' ? ' (таймаут)' : '')
      + '. Это не запрет — повтори вызов через несколько секунд.';
  const status = err?.status;
  if (!status) return String(err?.message ?? err);
  const body = err.bodyText ? ` ${err.bodyText}` : '';
  // 409/429 — «занято» (в т.ч. переполненная очередь сообщений занятого чата). Проверяем ДО
  // общего 5xx: совет «повтори через несколько секунд» гнал бы модель спамить в эту очередь.
  if (status === 409 || status === 429)
    return `Сейчас занято (HTTP ${status}).${body} Повтори позже, не чаще раза в 30 секунд.`;
  if (RETRIABLE_STATUS.has(status))
    return `Временный сбой на сервере (HTTP ${status}).${body} Это не запрет — повтори вызов через несколько секунд.`;
  return `Отказ (HTTP ${status}).${body} Повторять тот же вызов бессмысленно — само условие не изменится.`;
}

async function api(path, { timeoutMs = 30_000, ...options } = {}, attempt = 0) {
  try {
    const res = await fetch(`${API_URL}${path}`, {
      ...options,
      // Таймаут: без него подвисший бэкенд вешает вызов инструмента до дефолта undici (~300с)
      signal: AbortSignal.timeout(timeoutMs),
      headers: {
        'Content-Type': 'application/json; charset=utf-8',
        Authorization: `Bearer ${API_TOKEN}`,
        ...(options.headers ?? {}),
      },
    });
    if (!res.ok) {
      const body = await res.text();
      const err = new Error(`HTTP ${res.status}: ${body}`);
      err.status = res.status;
      err.bodyText = body;
      throw err;
    }
    return parseBody(res);
  } catch (err) {
    if (!shouldRetry(err, options.method, attempt)) throw err;
    await sleep(RETRY_DELAYS_MS[attempt]);
    return api(path, { timeoutMs, ...options }, attempt + 1);
  }
}

// Тело успешного ответа. Пустое тело — НЕ ошибка: ASP.NET на части операций отвечает
// `Ok()` без объекта, а `res.json()` кидал на нём «Unexpected end of JSON input» —
// удавшееся действие выглядело для модели провалом, и она повторяла его второй раз.
async function parseBody(res) {
  if (res.status === 204) return null;
  const text = await res.text();
  if (!text) return null;
  try { return JSON.parse(text); }
  catch { return text; }
}

// Словарь kind → описание
const KIND_HELP = [
  { kind: 'reminder', icon: '⏰', desc: 'Напоминания о задачах, сроках, событиях' },
  { kind: 'claude', icon: '●', desc: 'Ответы агентов, результаты задач, сообщения персон' },
  { kind: 'info', icon: 'ℹ', desc: 'Системные: дайджесты, саммари, конвейеры' },
  { kind: 'success', icon: '✓', desc: 'Успешное завершение: задача выполнена, процесс окончен' },
  { kind: 'meeting', icon: '🏁', desc: 'Совещания: завершены, готовы итоги' },
];

const COMMON_TAGS = 'Напоминание, Персона, Исполнитель, Дайджест, Саммари, Совещание, Конвейер, Планировщик, Система';

const TOOLS = [
  {
    name: 'notifications_create',
    description: `Создать уведомление пользователю. Используй когда нужно привлечь внимание: задача выполнена, персона ответила, готов дайджест, требуется действие. kind указывает иконку/цвет: ${KIND_HELP.map(k => `${k.kind} (${k.icon}) — ${k.desc}`).join('; ')}. tag — краткая метка: ${COMMON_TAGS}. url — hash-диплинк для перехода по клику.`,
    inputSchema: {
      type: 'object',
      properties: {
        title: { type: 'string', description: 'Заголовок уведомления (коротко, ёмко)' },
        body: { type: 'string', description: 'Текст уведомления (1-2 предложения)' },
        kind: {
          type: 'string',
          enum: ['reminder', 'claude', 'info', 'success', 'meeting'],
          description: 'Категория для иконки/цвета',
          default: 'info',
        },
        type: {
          type: 'string',
          description: 'Подтип для классификации: system, agent_reply, task_done, briefing, summary, meeting_complete, pipeline_complete, custom',
          default: 'system',
        },
        url: { type: 'string', description: 'Hash-диплинк для перехода: #/chats/{id}, #/project/{pid}/task/{tid}, #/notes/{nid}' },
        source: { type: 'string', description: 'Источник: название проекта, чата, персоны' },
        tag: { type: 'string', description: `Краткая метка: ${COMMON_TAGS}` },
        projectId: { type: 'string', description: 'ID проекта (если уведомление про проект)' },
        sessionId: { type: 'string', description: 'ID сессии/чата (для ссылки)' },
        taskId: { type: 'string', description: 'ID задачи (для ссылки)' },
      },
      required: ['title', 'body'],
    },
  },
  {
    name: 'notifications_list',
    description: 'Получить список уведомлений пользователя с фильтрацией и пагинацией.',
    inputSchema: {
      type: 'object',
      properties: {
        limit: { type: 'number', description: 'Сколько вернуть (1-100)', default: 20 },
        offset: { type: 'number', description: 'Смещение от начала', default: 0 },
        kind: { type: 'string', enum: ['all', 'reminder', 'claude', 'info', 'success', 'meeting'], description: 'Фильтр по категории', default: 'all' },
        unreadOnly: { type: 'boolean', description: 'Только непрочитанные', default: false },
      },
    },
  },
  {
    name: 'notifications_mark_read',
    description: 'Отметить уведомление как прочитанное.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'ID уведомления' },
        all: { type: 'boolean', description: 'Отметить все как прочитанные' },
      },
    },
  },
  {
    name: 'notifications_delete',
    description: 'Удалить уведомление.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'ID уведомления' },
      },
      required: ['id'],
    },
  },
];

// ======== JSON-RPC over stdio ========

const rl = createInterface({ input: process.stdin, terminal: false });

function respond(id, result) {
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
}

function respondError(id, code, message) {
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, error: { code, message } }) + '\n');
}

rl.on('line', async (line) => {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }

  // Нотификации JSON-RPC (нет id, в т.ч. notifications/initialized) не требуют ответа —
  // ответ с отсутствующим id нарушает протокол (workspace-server ведёт себя так же)
  if (msg.id === undefined || msg.id === null) return;

  // JSON-RPC 2.0 initialize
  if (msg.method === 'initialize') {
    respond(msg.id, {
      protocolVersion: '2024-11-05',
      capabilities: {
        tools: {},
      },
      serverInfo: {
        name: 'notifications-server',
        version: '0.1.0',
      },
    });
    return;
  }

  if (msg.method === 'tools/list') {
    respond(msg.id, { tools: TOOLS });
    return;
  }

  if (msg.method === 'tools/call') {
    const { name, arguments: args } = msg.params ?? {};

    try {
      switch (name) {
        case 'notifications_create': {
          // Уведомление от персоны — проставляем её personaId (если сессия персоны и
          // модель не указала явно), бэкенд денормализует имя/роль/аватар.
          const body = { ...args };
          if (SELF_PERSONA_ID && !body.personaId) body.personaId = SELF_PERSONA_ID;
          const notif = await api('/api/notifications', {
            method: 'POST',
            body: JSON.stringify(body),
          });
          respond(msg.id, {
            content: [{ type: 'text', text: JSON.stringify(notif, null, 2) }],
          });
          break;
        }
        case 'notifications_list': {
          const params = new URLSearchParams();
          if (args?.limit) params.set('limit', String(args.limit));
          if (args?.offset) params.set('offset', String(args.offset));
          if (args?.kind && args.kind !== 'all') params.set('kind', args.kind);
          if (args?.unreadOnly) params.set('unreadOnly', 'true');
          const result = await api(`/api/notifications?${params}`);
          respond(msg.id, {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          });
          break;
        }
        case 'notifications_mark_read': {
          if (args?.all) {
            const result = await api('/api/notifications/read-all', { method: 'PUT' });
            respond(msg.id, {
              content: [{ type: 'text', text: JSON.stringify(result) }],
            });
          } else if (args?.id) {
            await api(`/api/notifications/${args.id}/read`, { method: 'PUT' });
            respond(msg.id, {
              content: [{ type: 'text', text: 'OK' }],
            });
          } else {
            respondError(msg.id, -32602, 'Need id or all=true');
          }
          break;
        }
        case 'notifications_delete': {
          await api(`/api/notifications/${args.id}`, { method: 'DELETE' });
          respond(msg.id, {
            content: [{ type: 'text', text: 'OK' }],
          });
          break;
        }
        default:
          respondError(msg.id, -32601, `Unknown tool: ${name}`);
      }
    } catch (err) {
      respondError(msg.id, -32603, err.message || 'Internal error');
    }
    return;
  }

  // Ping / health
  if (msg.method === 'ping') {
    respond(msg.id, { status: 'ok' });
    return;
  }
});

// Сигнал о готовности (по стандарту MCP — первая строка в stdout)
process.stdout.write(JSON.stringify({ jsonrpc: '2.0', method: 'log', params: { data: ['notifications-server ready'] } }) + '\n');
