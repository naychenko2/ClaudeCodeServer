// MCP-сервер графа кода ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   CODEGRAPH_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   CODEGRAPH_API_TOKEN  — сервисный JWT владельца сессии
//   CODEGRAPH_PROJECT_ID — id проекта, чей граф доступен (сервер подключается только в чате проекта)
//   CODEGRAPH_SESSION_ID — id сессии; уезжает в X-Caller-Session-Id (наблюдаемость)
//   CODEGRAPH_ROOT_PATH  — рабочее дерево чата: отдельное worktree имеет СВОЙ граф (ADR-003).
//                          Пусто — граф корня проекта, как раньше.
//
// Инструменты:
//   codegraph_find      — найти тип по имени или части FQN
//   codegraph_neighbors — связи типа (кто зависит и от чего) с типом связи и confidence
//   codegraph_hubs      — топ типов по связности
//
// Ответы рендерятся в компактный текст, а не в JSON снимка: это токены в контексте
// модели. Жёсткие лимиты по умолчанию, «файл:строка» одной строкой, честный хвост
// «ещё N» вместо полной выгрузки.

import { createInterface } from 'node:readline';
import { AsyncLocalStorage } from 'node:async_hooks';

// Процесс сервера не имеет права умирать от одной необработанной ошибки: вместе с ним из хода
// исчезают ВСЕ его инструменты, а незавершённые вызовы падают «Stream closed».
process.on('unhandledRejection', err => {
  console.error(`[mcp] необработанное отклонение промиса: ${err?.stack ?? err}`);
});
process.on('uncaughtException', err => {
  console.error(`[mcp] необработанное исключение: ${err?.stack ?? err}`);
});
// CLI закрыл pipe (убил сервер, завершился ход) — писать больше некуда. Штатное завершение.
process.stdout.on('error', err => {
  if (err?.code === 'EPIPE') process.exit(0);
  console.error(`[mcp] ошибка записи в stdout: ${err?.message ?? err}`);
});

// Имя инструмента текущего вызова — уезжает в заголовке X-Mcp-Tool (диагностика на бэкенде).
// AsyncLocalStorage, а не переменная модуля: вызовы инструментов идут параллельно.
const callCtx = new AsyncLocalStorage();

const API_URL = (process.env.CODEGRAPH_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.CODEGRAPH_API_TOKEN ?? '';
const PROJECT_ID = process.env.CODEGRAPH_PROJECT_ID || '';
const SESSION_ID = process.env.CODEGRAPH_SESSION_ID || null;
// Рабочее дерево чата: у отдельного worktree свой граф. Пусто — граф корня проекта.
const ROOT_PATH = process.env.CODEGRAPH_ROOT_PATH || '';

// Секундная недоступность бэкенда (рестарт, деплой) не должна давать красную карточку.
const RETRY_DELAYS_MS = [300, 900];
const RETRIABLE_STATUS = new Set([408, 425, 429, 500, 502, 503, 504]);
const sleep = ms => new Promise(r => setTimeout(r, ms));

// Ошибка соединения: запрос ТОЧНО не дошёл до бэкенда
const isConnectionError = err =>
  ['ECONNREFUSED', 'ENOTFOUND', 'EAI_AGAIN'].includes(err?.cause?.code);

// Сеть или таймаут: запрос мог и дойти — но здесь все вызовы GET (чтения), повтор безопасен
const isNetworkError = err =>
  err?.name === 'TimeoutError' || err?.name === 'AbortError'
  || err?.cause?.code !== undefined || /fetch failed/i.test(err?.message ?? '');

function shouldRetry(err, method, attempt) {
  if (attempt >= RETRY_DELAYS_MS.length) return false;
  if (isConnectionError(err)) return true;
  if ((method ?? 'GET').toUpperCase() !== 'GET') return false;
  return err?.status ? RETRIABLE_STATUS.has(err.status) : isNetworkError(err);
}

// Текст ошибки для модели с ЯВНЫМ классом: временный сбой ≠ запрет.
function describeError(err) {
  if (isNetworkError(err))
    return 'Временный сбой связи с сервером'
      + (err?.name === 'TimeoutError' ? ' (таймаут)' : '')
      + '. Это не запрет — повтори вызов через несколько секунд.';
  const status = err?.status;
  if (!status) return String(err?.message ?? err);
  const body = err.bodyText ? ` ${err.bodyText}` : '';
  if (status === 409 || status === 429)
    return `Сейчас занято (HTTP ${status}).${body} Повтори позже, не чаще раза в 30 секунд.`;
  if (RETRIABLE_STATUS.has(status))
    return `Временный сбой на сервере (HTTP ${status}).${body} Это не запрет — повтори вызов через несколько секунд.`;
  return `Отказ (HTTP ${status}).${body} Повторять тот же вызов бессмысленно — само условие не изменится.`;
}

async function api(path, { timeoutMs = 60_000, ...options } = {}, attempt = 0) {
  try {
    const res = await fetch(`${API_URL}${path}`, {
      ...options,
      // Таймаут: без него подвисший бэкенд вешает вызов до дефолта undici (~300с)
      signal: AbortSignal.timeout(timeoutMs),
      headers: {
        'Content-Type': 'application/json; charset=utf-8',
        Authorization: `Bearer ${API_TOKEN}`,
        ...(callCtx.getStore()?.tool ? { 'X-Mcp-Tool': callCtx.getStore().tool } : {}),
        ...(SESSION_ID ? { 'X-Caller-Session-Id': SESSION_ID } : {}),
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

// Тело успешного ответа. Пустое тело — НЕ ошибка (ASP.NET отвечает Ok() без объекта).
async function parseBody(res) {
  if (res.status === 204) return null;
  const text = await res.text();
  if (!text) return null;
  try { return JSON.parse(text); }
  catch { return text; }
}

const GRAPH_BASE = () => `/api/projects/${encodeURIComponent(PROJECT_ID)}/code-graph`;

// Строка запроса с деревом чата: бэкенд принимает rootPath только из белого списка
// (корень проекта либо worktree его сессий), поэтому передаём как есть.
const withRoot = params => {
  if (ROOT_PATH) params.set('rootPath', ROOT_PATH);
  return params.toString();
};

const TOOLS = [
  {
    name: 'codegraph_find',
    description: 'Найти тип (класс/интерфейс/структуру/enum) в графе кода проекта по имени или части полного имени. '
      + 'Отвечает на «где объявлен X» точнее Grep: возвращает файл со строкой, вид типа и степень связности '
      + '(сколько связей у типа). Ищи так, когда нужен именно тип, а не любое текстовое вхождение имени.',
    inputSchema: {
      type: 'object',
      properties: {
        query: { type: 'string', description: 'Имя типа («ServerMessage») или часть полного имени («Services.CodeGraph»)' },
        limit: { type: 'number', description: 'Сколько записей вернуть (1-100, по умолчанию 20)', default: 20 },
      },
      required: ['query'],
    },
  },
  {
    name: 'codegraph_neighbors',
    description: 'Связи типа в графе кода: кто зависит от него (входящие) и от чего зависит он (исходящие), '
      + 'с типом связи (Calls — вызовы, Implements — реализация/наследование, References — упоминание в полях, '
      + 'параметрах, возвращаемых значениях) и уверенностью (Extracted — из символов Roslyn, Inferred — эвристика). '
      + 'Отвечает на «кто зависит от X» с типизацией, которой Grep не даёт. Узел задаётся именем типа или полным именем.',
    inputSchema: {
      type: 'object',
      properties: {
        node: { type: 'string', description: 'Тип: имя («ServerMessage») или полное имя («ClaudeHomeServer.Protocol.ServerMessage»)' },
        direction: {
          type: 'string',
          enum: ['both', 'in', 'out'],
          description: 'in — кто зависит от типа; out — от чего зависит тип; both — обе стороны (по умолчанию)',
          default: 'both',
        },
        relation: {
          type: 'string',
          enum: ['Calls', 'Implements', 'References'],
          description: 'Оставить только связи этого типа (по умолчанию — все)',
        },
        limit: { type: 'number', description: 'Сколько связей вернуть (1-100, по умолчанию 20)', default: 20 },
      },
      required: ['node'],
    },
  },
  {
    name: 'codegraph_hubs',
    description: 'Топ типов проекта по связности («god-узлы») — с чего начинать разбираться в незнакомом коде '
      + 'и что ломается больнее всего при правке. Тот же список, что в системном промпте, но по запросу и любой длины.',
    inputSchema: {
      type: 'object',
      properties: {
        limit: { type: 'number', description: 'Сколько узлов вернуть (1-100, по умолчанию 10)', default: 10 },
      },
    },
  },
];

// ======== Компактный рендер ответов ========

const staleNote = r => r?.isStale ? '\n(граф может быть устаревшим — файлы менялись после построения)' : '';

// Пустой результат на устаревшем графе — не «ничего не найдено», а «проверено по старому
// снимку»: иначе модель принимает пустоту за истину и не повторяет запрос. Предупреждение
// В НАЧАЛЕ ответа — первая строка задаёт интерпретацию всего текста.
const staleEmptyWarning = () =>
  '⚠ Граф кода устарел и перестраивается фоном — поиск шёл по СТАРОМУ снимку.\n'
  + 'Если тип обязан существовать (фронт только что добавлен и т.п.), повтори запрос через 1–2 минуты.';

// «FQN [Kind] file.cs:42 — N связей»
const nodeLine = n => `${n.fqn} [${n.kind}] ${n.location || '?'} — ${n.degree} связей`;

function renderFind(result, query, limit) {
  const rows = result.results ?? [];
  if (rows.length === 0) {
    if (result.isStale) {
      return `${staleEmptyWarning()}\nПо запросу «${query}» в старом снимке ничего не найдено.`;
    }
    return `По запросу «${query}» в графе кода ничего не найдено.`;
  }
  const lines = rows.map(nodeLine);
  const rest = result.total - rows.length;
  if (rest > 0) lines.push(`… ещё ${rest} (показаны первые ${rows.length}, limit=${limit})`);
  return `Найдено ${result.total} по запросу «${query}»:\n${lines.join('\n')}${staleNote(result)}`;
}

function renderNeighbors(result, limit) {
  const node = result.node;
  const rows = result.neighbors ?? [];
  const byRelation = Object.entries(result.byRelation ?? {})
    .map(([rel, count]) => `${rel} ${count}`)
    .join(', ');

  const head = `${node.fqn} [${node.kind}] ${node.location || '?'}\n`
    + `Связей: ${node.degree} (входящих ${result.totalIn}, исходящих ${result.totalOut})`
    + (byRelation ? `; под фильтром ${result.total}: ${byRelation}` : '');

  if (rows.length === 0) {
    if (result.isStale) {
      return `${head}\n${staleEmptyWarning()}\nПод фильтром связей в старом снимке нет.`;
    }
    return `${head}\nПод фильтром связей нет.`;
  }

  // ← кто зависит от узла, → от чего зависит узел
  const lines = rows.map(n => {
    const arrow = n.direction === 'in' ? '←' : '→';
    const conf = n.confidence === 'Inferred' ? ' (Inferred)' : '';
    return `${arrow} ${n.relation}${conf}  ${n.fqn}${n.location ? `  ${n.location}` : ''}`;
  });
  const rest = result.total - rows.length;
  if (rest > 0) lines.push(`… ещё ${rest} (показаны первые ${rows.length}, limit=${limit})`);
  return `${head}\n${lines.join('\n')}${staleNote(result)}`;
}

function renderHubs(result) {
  const rows = result.hubs ?? [];
  if (rows.length === 0) return `В графе кода нет связанных узлов (узлов ${result.nodeCount}, рёбер ${result.edgeCount}).`;
  const lines = rows.map((n, i) => `${i + 1}. ${nodeLine(n)}`);
  return `Хабы по связности (граф: ${result.nodeCount} узлов, ${result.edgeCount} рёбер):\n`
    + `${lines.join('\n')}${staleNote(result)}`;
}

// 404 «узел не найден» несёт кандидатов — отдаём их модели, а не голый отказ:
// иначе она пойдёт гадать имя вслепую.
function describeNodeMiss(err, node) {
  try {
    const body = JSON.parse(err.bodyText ?? '');
    if (!body?.message) return null;
    const candidates = (body.candidates ?? []).map(c => `  ${nodeLine(c)}`);
    if (body.building) return null; // граф строится — это не про имя узла
    return candidates.length > 0
      ? `${body.message}. Похожие узлы:\n${candidates.join('\n')}`
      : `${body.message}`;
  } catch {
    return null;
  }
}

// ======== JSON-RPC over stdio ========

const rl = createInterface({ input: process.stdin, terminal: false });

function respond(id, result) {
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
}

function respondError(id, code, message) {
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, error: { code, message } }) + '\n');
}

const text = t => ({ content: [{ type: 'text', text: t }] });

const clampLimit = (value, fallback) => {
  const n = Number(value);
  if (!Number.isFinite(n) || n <= 0) return fallback;
  return Math.min(Math.trunc(n), 100);
};

rl.on('line', async (line) => {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg === null || typeof msg !== 'object') return;

  // Нотификации JSON-RPC (нет id) ответа не требуют
  if (msg.id === undefined || msg.id === null) return;

  if (msg.method === 'initialize') {
    respond(msg.id, {
      protocolVersion: '2024-11-05',
      capabilities: { tools: {} },
      serverInfo: { name: 'codegraph-server', version: '0.1.0' },
    });
    return;
  }

  if (msg.method === 'tools/list') {
    respond(msg.id, { tools: TOOLS });
    return;
  }

  if (msg.method === 'ping') {
    respond(msg.id, { status: 'ok' });
    return;
  }

  if (msg.method === 'tools/call') {
    const { name, arguments: args } = msg.params ?? {};

    return callCtx.run({ tool: name }, async () => {
      try {
        if (!PROJECT_ID) {
          respond(msg.id, text('Граф кода доступен только в чате проекта — текущий чат вне проекта.'));
          return;
        }

        switch (name) {
          case 'codegraph_find': {
            const query = (args?.query ?? '').trim();
            if (!query) { respondError(msg.id, -32602, 'Нужен параметр query'); return; }
            const limit = clampLimit(args?.limit, 20);
            const params = new URLSearchParams({ q: query, limit: String(limit) });
            const result = await api(`${GRAPH_BASE()}/find?${withRoot(params)}`);
            respond(msg.id, text(renderFind(result, query, limit)));
            break;
          }
          case 'codegraph_neighbors': {
            const node = (args?.node ?? '').trim();
            if (!node) { respondError(msg.id, -32602, 'Нужен параметр node'); return; }
            const limit = clampLimit(args?.limit, 20);
            const params = new URLSearchParams({ node, limit: String(limit) });
            if (args?.direction) params.set('direction', String(args.direction));
            if (args?.relation) params.set('relation', String(args.relation));
            const result = await api(`${GRAPH_BASE()}/neighbors?${withRoot(params)}`);
            respond(msg.id, text(renderNeighbors(result, limit)));
            break;
          }
          case 'codegraph_hubs': {
            const limit = clampLimit(args?.limit, 10);
            const params = new URLSearchParams({ limit: String(limit) });
            const result = await api(`${GRAPH_BASE()}/hubs?${withRoot(params)}`);
            respond(msg.id, text(renderHubs(result)));
            break;
          }
          default:
            respondError(msg.id, -32601, `Unknown tool: ${name}`);
        }
      } catch (err) {
        // «Узел не найден» — не сбой инструмента, а полезный ответ с кандидатами
        if (err?.status === 404 && name === 'codegraph_neighbors') {
          const miss = describeNodeMiss(err, args?.node);
          if (miss) { respond(msg.id, text(miss)); return; }
        }
        respondError(msg.id, -32603, describeError(err));
      }
    });
  }
});

// Сигнал о готовности (по стандарту MCP — первая строка в stdout)
process.stdout.write(JSON.stringify({ jsonrpc: '2.0', method: 'log', params: { data: ['codegraph-server ready'] } }) + '\n');
