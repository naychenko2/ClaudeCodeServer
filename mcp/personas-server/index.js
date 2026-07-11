// MCP-сервер персон ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   PERSONAS_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   PERSONAS_API_TOKEN  — сервисный JWT владельца сессии
//   PERSONAS_PROJECT_ID — id проекта сессии; пусто = чат вне проекта (глобальный контекст)
//   PERSONAS_SELF_ID    — id персоны текущего чата (для persona_ask: себя не спрашивают;
//                         для привязок — запрет самоэскалации: свои привязки менять нельзя)
//   PERSONAS_MENTIONS   — "1" = включены @упоминания (флаг persona-mentions): добавляется
//                         инструмент persona_ask — спросить другую персону от её лица
//   PERSONAS_BINDINGS   — "1" = включены привязки (флаг persona-bindings): добавляются
//                         инструменты personas_bindings_list/suggest/set и параметры
//                         bindings/autoBindings в personas_create/personas_update
//
// Персона — AI-собеседник с именем, ролью, характером и аватаром; бывает глобальной
// или привязанной к проекту. Изоляция per-owner — на стороне backend (токен определяет
// владельца, чужие персоны и проекты недоступны).

import { createInterface } from 'node:readline';

const API_URL = (process.env.PERSONAS_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.PERSONAS_API_TOKEN ?? '';
const PROJECT_ID = process.env.PERSONAS_PROJECT_ID || null;
const SELF_ID = process.env.PERSONAS_SELF_ID || null;
const MENTIONS = process.env.PERSONAS_MENTIONS === '1';
const BINDINGS = process.env.PERSONAS_BINDINGS === '1';

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
    const err = new Error(`HTTP ${res.status}: ${body}`);
    err.status = res.status;
    throw err;
  }
  if (res.status === 204) return null;
  return res.json();
}

// --- Инструменты ---

const CONTEXT_NOTE = PROJECT_ID
  ? 'Контекст — текущий проект: для проектной персоны projectId можно не указывать.'
  : 'Контекст — чат вне проекта: по умолчанию создаются глобальные персоны.';

const COLORS = ['yellow', 'orange', 'blue', 'green', 'purple', 'red', 'brown', 'cyan', 'pink'];

// Общие поля персоны для create/update (кроме name — у create он обязателен)
const PERSONA_FIELDS = {
  role: { type: 'string', description: 'Роль — главное в отображении («Роль (Имя)»), например «Дизайнер»' },
  description: { type: 'string', description: 'Короткое описание, кто это (для карточки)' },
  // Контракт характера — по слотам (собирается в contract на бэкенде)
  character: { type: 'string', description: 'Характер и ценности персоны на «ты» («Ты — …»), 2-5 предложений' },
  tone: { type: 'string', description: 'Тон общения одной короткой фразой (напр. «сухо и по делу»)' },
  mustDo: { type: 'array', items: { type: 'string' }, description: 'Правила «что делать всегда», 2-4 коротких пункта' },
  mustNot: { type: 'array', items: { type: 'string' }, description: 'Анти-паттерны «чего не делать никогда», 2-4 пункта' },
  outputFormat: { type: 'string', description: 'Требования к формату ответов, 1-2 предложения' },
  speechExamples: { type: 'array', items: { type: 'string' }, description: '1-2 характерные реплики от лица персоны (образцы стиля)' },
  systemPrompt: { type: 'string', description: 'УСТАРЕЛО: единый текст характера — используй character и остальные слоты' },
  model: { type: 'string', description: 'Модель LLM (пусто = дефолт сервера)' },
  effort: { type: 'string', description: 'Усилие рассуждения модели' },
  color: { type: 'string', enum: COLORS, description: 'Цвет аватара из палитры' },
  greeting: { type: 'string', description: 'Приветствие — первое сообщение от лица персоны' },
  memoryEnabled: { type: 'boolean', description: 'Долгая память персоны (по умолчанию включена)' },
};

// Привязка персоны (флаг persona-bindings): источник знаний или правило с условием применения
const BINDING_ITEM_SCHEMA = {
  type: 'object',
  required: ['type', 'target'],
  properties: {
    type: {
      type: 'string',
      enum: ['project', 'projectPath', 'knowledge', 'notes', 'tool', 'skill'],
      description: 'Тип: project — проект целиком; projectPath — папка/файл проекта; knowledge — база знаний (datasetId); notes — источник заметок; tool — рубильник инструмента; skill — глобальный скилл',
    },
    target: { type: 'string', description: 'Цель: projectId | datasetId | source заметок ("personal"/projectId) | ключ инструмента (tasks/notes/web/…) | имя скилла' },
    path: { type: 'string', description: 'Путь внутри цели (для projectPath — обязателен; для notes — папка источника)' },
    condition: { type: 'string', description: 'Когда персоне применять источник (1-2 предложения; пусто = «всегда под рукой»)' },
    mode: { type: 'string', enum: ['auto', 'always', 'off'], description: 'Режим: auto — по условию (дефолт); always — выжимка в каждый ход; off — выключена' },
  },
};

// Параметры привязок в create/update — только при включённом флаге persona-bindings
const BINDING_CREATE_FIELDS = BINDINGS ? {
  bindings: { type: 'array', items: BINDING_ITEM_SCHEMA, description: 'Явные привязки источников знаний и правил' },
  autoBindings: { type: 'boolean', description: 'true — после создания AI сам подберёт привязки под роль персоны' },
} : {};

const TOOLS = [
  {
    name: 'personas_list',
    description: `Перечислить персон пользователя. ${CONTEXT_NOTE} scope: "context" — доступные здесь ` +
      '(глобальные + текущего проекта, по умолчанию); "project" — только текущего проекта; ' +
      '"global" — только глобальные; "all" — все персоны владельца.',
    inputSchema: {
      type: 'object',
      properties: {
        scope: { type: 'string', enum: ['context', 'project', 'global', 'all'], description: 'Какие персоны показать (по умолчанию context)' },
      },
    },
  },
  {
    name: 'personas_get',
    description: 'Получить полный профиль персоны по id (роль, характер, модель, зона, приветствие).',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID персоны' } },
    },
  },
  {
    name: 'personas_create',
    description: `Создать персону — AI-собеседника с именем, ролью и характером. ${CONTEXT_NOTE} ` +
      'scope: "global" — доступна во всех чатах (по умолчанию); "project" — привязана к проекту. ' +
      'Заполняй ВСЕ слоты характера: character (на «ты»), tone, mustDo, mustNot, outputFormat, ' +
      'speechExamples; приветствие — в greeting от лица персоны.',
    inputSchema: {
      type: 'object',
      required: ['name'],
      properties: {
        name: { type: 'string', description: 'Имя персоны' },
        ...PERSONA_FIELDS,
        scope: { type: 'string', enum: ['global', 'project'], description: 'Зона персоны (по умолчанию global)' },
        projectId: { type: 'string', description: 'ID проекта для scope=project (по умолчанию — проект текущей сессии)' },
        ...BINDING_CREATE_FIELDS,
      },
    },
  },
  {
    name: 'personas_update',
    description: 'Изменить персону: передавай только изменяемые поля. Пустая строка очищает ' +
      'role/model/effort/color/greeting. Смена scope на "project" требует projectId.' +
      (BINDINGS ? ' bindings — ПОЛНАЯ замена набора привязок (свои собственные привязки менять нельзя).' : ''),
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID персоны' },
        name: { type: 'string', description: 'Новое имя' },
        ...PERSONA_FIELDS,
        scope: { type: 'string', enum: ['global', 'project'], description: 'Новая зона персоны' },
        projectId: { type: 'string', description: 'ID проекта для scope=project' },
        ...(BINDINGS ? {
          bindings: { type: 'array', items: BINDING_ITEM_SCHEMA, description: 'Полная замена набора привязок персоны' },
        } : {}),
      },
    },
  },
  // Привязки персон (флаг persona-bindings): источники знаний и правила с условиями применения
  ...(BINDINGS ? [
    {
      name: 'personas_bindings_list',
      description: 'Привязки персоны: источники знаний (проекты, папки, базы знаний, заметки, скиллы) ' +
        'и правила инструментов с условиями «когда применять».',
      inputSchema: {
        type: 'object',
        required: ['id'],
        properties: { id: { type: 'string', description: 'ID персоны' } },
      },
    },
    {
      name: 'personas_suggest_bindings',
      description: 'AI-подбор привязок под роль персоны (по каталогу проектов/баз/заметок/скиллов ' +
        'владельца). Возвращает кандидатов, НЕ сохраняет — сохрани нужные через personas_bindings_set.',
      inputSchema: {
        type: 'object',
        required: ['id'],
        properties: { id: { type: 'string', description: 'ID персоны' } },
      },
    },
    {
      name: 'personas_bindings_set',
      description: 'Полная замена набора привязок персоны (пустой массив — убрать все). ' +
        'Свои собственные привязки персона менять не может.',
      inputSchema: {
        type: 'object',
        required: ['id', 'bindings'],
        properties: {
          id: { type: 'string', description: 'ID персоны' },
          bindings: { type: 'array', items: BINDING_ITEM_SCHEMA, description: 'Новый набор привязок' },
        },
      },
    },
    {
      name: 'knowledge_search',
      description: 'Гибридный поиск (смысловой + полнотекстовый по ключевым словам) по привязанной ' +
        'базе знаний (Dify) по её datasetId. Используй, когда выполняется условие привязки-«базы ' +
        'знаний» из твоего контекста: подставь datasetId из строки привязки и запрос по смыслу ' +
        'вопроса пользователя (можно включать точные термины/имена — их найдёт полнотекстовая часть). ' +
        'Возвращает релевантные выдержки: документ, score, текст и метаданные (напр. дата встречи, ' +
        'источник) — используй их, чтобы датировать и атрибутировать факты.',
      inputSchema: {
        type: 'object',
        required: ['datasetId', 'query'],
        properties: {
          datasetId: { type: 'string', description: 'ID датасета из строки привязки (mcp__personas__knowledge_search datasetId "…")' },
          query: { type: 'string', description: 'Поисковый запрос на естественном языке (по смыслу вопроса)' },
          topK: { type: 'integer', minimum: 1, maximum: 20, description: 'Сколько выдержек вернуть (по умолчанию 6)' },
        },
      },
    },
  ] : []),
  {
    name: 'personas_delete',
    description: 'Удалить персону по id. Действие необратимо: долгая память персоны тоже удаляется.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID персоны' } },
    },
  },
  {
    name: 'personas_generate_avatar',
    description: 'Сгенерировать персоне фото-аватар (AI, fal.ai) и сразу применить его. ' +
      'prompt — описание внешности (лучше по-английски); без prompt портрет строится по ' +
      'имени/роли/описанию персоны. Занимает ~10-30 секунд.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID персоны' },
        prompt: { type: 'string', description: 'Описание внешности для генерации (необязательно)' },
      },
    },
  },
  // @упоминания (флаг persona-mentions): спросить другую персону — она ответит one-shot'ом
  // от своего лица, со своим характером, памятью и моделью. Глубина делегирования строго 1:
  // one-shot, запущенный persona_ask, этот сервер уже не получает.
  ...(MENTIONS ? [{
    name: 'persona_ask',
    description: 'Спросить другую персону: она ответит от своего лица, в своём характере и со своей ' +
      'долгой памятью. Используй, когда пользователь упоминает @handle персоны или когда нужна её ' +
      'экспертиза (например, мнение ревьюера о плане). handle персоны есть в personas_list. ' +
      'Вопрос формулируй самодостаточно — персона не видит этот разговор; важный контекст передай ' +
      'в поле context.',
    inputSchema: {
      type: 'object',
      required: ['handle', 'question'],
      properties: {
        handle: { type: 'string', description: 'handle персоны (без @), см. personas_list' },
        question: { type: 'string', description: 'Самодостаточный вопрос к персоне' },
        context: { type: 'string', description: 'Необязательный контекст разговора (кратко, только нужное для ответа)' },
      },
    },
  }] : []),
];

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

// Тело create/update из аргументов: только присутствующие ключи (partial-update)
function personaBody(args, keys) {
  const body = {};
  for (const key of keys) if (key in args) body[key] = args[key];
  return body;
}

const FIELD_KEYS = ['name', 'role', 'description', 'systemPrompt', 'model', 'effort', 'color', 'greeting', 'memoryEnabled', 'scope', 'projectId'];

// Запрет самоэскалации: персона не может менять СОБСТВЕННЫЕ привязки
// (проверка до любого fetch — изменение прав себе блокируется по построению)
function assertNotSelfBindings(id) {
  if (SELF_ID && String(id) === SELF_ID)
    throw new Error('Персона не может менять собственные привязки — попроси об этом пользователя.');
}

// Слоты контракта характера (P1): плоские аргументы инструмента → объект contract API
const CONTRACT_KEYS = ['character', 'tone', 'mustDo', 'mustNot', 'outputFormat', 'speechExamples'];

// Собрать contract из аргументов; systemPrompt — legacy-алиас character.
// null — слоты не переданы (contract в body не включаем)
function contractFrom(args) {
  const c = {};
  for (const key of CONTRACT_KEYS) if (key in args) c[key] = args[key];
  if (!('character' in c) && typeof args.systemPrompt === 'string' && args.systemPrompt.trim())
    c.character = args.systemPrompt;
  return Object.keys(c).length ? c : null;
}

async function callTool(name, args) {
  switch (name) {
    case 'personas_list': {
      const scope = args.scope ?? 'context';
      if (scope === 'all') return json(await api('/api/personas'));
      if (scope === 'project' && !PROJECT_ID)
        throw new Error('Текущая сессия вне проекта — проектных персон здесь нет (используй scope "global" или "all").');
      const params = new URLSearchParams({ scope });
      if (scope !== 'global') params.set('projectId', PROJECT_ID ?? '');
      return json(await api(`/api/personas?${params}`));
    }

    case 'personas_get':
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}`));

    case 'personas_create': {
      const body = personaBody(args, FIELD_KEYS);
      // Характер — сразу контрактом по слотам; legacy systemPrompt мапится в character
      const contract = contractFrom(args);
      if (contract) {
        body.contract = contract;
        delete body.systemPrompt;
      }
      body.scope = args.scope ?? 'global';
      if (body.scope === 'project') {
        body.projectId = args.projectId ?? PROJECT_ID;
        if (!body.projectId)
          throw new Error('Для проектной персоны нужен projectId: текущая сессия вне проекта — укажи projectId явно или создай глобальную (scope "global").');
      } else {
        delete body.projectId;
      }
      // Привязки при создании (флаг persona-bindings): явный список и/или AI-подбор
      if (BINDINGS) {
        if (Array.isArray(args.bindings)) body.bindings = args.bindings;
        if ('autoBindings' in args) body.autoBindings = Boolean(args.autoBindings);
      }
      return json(await api('/api/personas', { method: 'POST', body: JSON.stringify(body) }));
    }

    case 'personas_update': {
      // Изменение привязок — отдельным PUT-эндпоинтом; себе — запрещено (анти-самоэскалация)
      if ('bindings' in args) {
        if (!BINDINGS) throw new Error('Привязки персон выключены (флаг persona-bindings).');
        assertNotSelfBindings(args.id);
        await api(`/api/personas/${encodeURIComponent(args.id)}/bindings`, {
          method: 'PUT',
          body: JSON.stringify({ bindings: args.bindings ?? [] }),
        });
      }
      const body = personaBody(args, FIELD_KEYS);
      // Частичная правка контракта: мержим с текущим, иначе передача одного слота
      // (напр. только tone) затёрла бы остальные — API заменяет contract целиком
      const contract = contractFrom(args);
      if (contract) {
        const current = await api(`/api/personas/${encodeURIComponent(args.id)}`);
        body.contract = { ...(current?.contract ?? {}), ...contract };
        delete body.systemPrompt;
      }
      // Изменены только привязки (body пуст) — просто вернуть актуальную персону
      if (Object.keys(body).length === 0)
        return json(await api(`/api/personas/${encodeURIComponent(args.id)}`));
      if (body.scope === 'project' && !('projectId' in body)) {
        if (!PROJECT_ID)
          throw new Error('Для смены зоны на проектную нужен projectId: текущая сессия вне проекта.');
        body.projectId = PROJECT_ID;
      }
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}`, { method: 'PUT', body: JSON.stringify(body) }));
    }

    case 'personas_bindings_list':
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}/bindings`));

    case 'personas_suggest_bindings':
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}/bindings/suggest`, { method: 'POST' }));

    case 'personas_bindings_set': {
      assertNotSelfBindings(args.id);
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}/bindings`, {
        method: 'PUT',
        body: JSON.stringify({ bindings: args.bindings ?? [] }),
      }));
    }

    case 'knowledge_search':
      return json(await api('/api/personas/knowledge-search', {
        method: 'POST',
        body: JSON.stringify({
          datasetId: args.datasetId,
          query: args.query,
          topK: args.topK ?? null,
        }),
      }));

    case 'personas_delete':
      await api(`/api/personas/${encodeURIComponent(args.id)}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `Персона ${args.id} удалена (вместе с её памятью).` }] };

    case 'personas_generate_avatar': {
      const id = encodeURIComponent(args.id);
      try {
        const gen = await api(`/api/personas/${id}/avatar/generate`, {
          method: 'POST',
          body: JSON.stringify({ prompt: args.prompt ?? null, count: 1 }),
        });
        const file = gen?.candidates?.[0];
        if (!file) throw new Error('Генерация не вернула ни одного кандидата.');
        return json(await api(`/api/personas/${id}/avatar/select`, {
          method: 'POST',
          body: JSON.stringify({ file }),
        }));
      } catch (err) {
        if (err?.status === 400)
          throw new Error('AI-генерация аватара недоступна: на сервере не настроен fal.ai (Fal:ApiKey).');
        if (err?.status === 502)
          throw new Error('Сервис генерации изображений не ответил — попробуй позже.');
        throw err;
      }
    }

    case 'persona_ask': {
      if (!MENTIONS) throw new Error('Инструмент persona_ask выключен (флаг persona-mentions).');
      const body = {
        handle: String(args.handle ?? '').replace(/^@/, ''),
        question: String(args.question ?? ''),
      };
      if (SELF_ID && body.handle) {
        // Себя не спрашивают — подсказка вместо бессмысленного one-shot
        const self = await api(`/api/personas/${encodeURIComponent(SELF_ID)}`).catch(() => null);
        if (self && String(self.handle).toLowerCase() === body.handle.toLowerCase())
          throw new Error('Это твой собственный handle — отвечай сам, спрашивать себя не нужно.');
      }
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
