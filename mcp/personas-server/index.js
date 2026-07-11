// MCP-сервер персон ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   PERSONAS_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   PERSONAS_API_TOKEN  — сервисный JWT владельца сессии
//   PERSONAS_PROJECT_ID — id проекта сессии; пусто = чат вне проекта (глобальный контекст)
//   PERSONAS_SELF_ID    — id персоны текущего чата (для persona_ask: себя не спрашивают)
//   PERSONAS_MENTIONS   — "1" = включены @упоминания (флаг persona-mentions): добавляется
//                         инструмент persona_ask — спросить другую персону от её лица
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
  systemPrompt: { type: 'string', description: 'Характер персоны — тело системного промпта на «ты» («Ты — …»), 2-5 предложений' },
  model: { type: 'string', description: 'Модель LLM (пусто = дефолт сервера)' },
  effort: { type: 'string', description: 'Усилие рассуждения модели' },
  color: { type: 'string', enum: COLORS, description: 'Цвет аватара из палитры' },
  greeting: { type: 'string', description: 'Приветствие — первое сообщение от лица персоны' },
  memoryEnabled: { type: 'boolean', description: 'Долгая память персоны (по умолчанию включена)' },
};

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
      'Характер пиши в systemPrompt на «ты», приветствие — в greeting от лица персоны.',
    inputSchema: {
      type: 'object',
      required: ['name'],
      properties: {
        name: { type: 'string', description: 'Имя персоны' },
        ...PERSONA_FIELDS,
        scope: { type: 'string', enum: ['global', 'project'], description: 'Зона персоны (по умолчанию global)' },
        projectId: { type: 'string', description: 'ID проекта для scope=project (по умолчанию — проект текущей сессии)' },
      },
    },
  },
  {
    name: 'personas_update',
    description: 'Изменить персону: передавай только изменяемые поля. Пустая строка очищает ' +
      'role/model/effort/color/greeting. Смена scope на "project" требует projectId.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID персоны' },
        name: { type: 'string', description: 'Новое имя' },
        ...PERSONA_FIELDS,
        scope: { type: 'string', enum: ['global', 'project'], description: 'Новая зона персоны' },
        projectId: { type: 'string', description: 'ID проекта для scope=project' },
      },
    },
  },
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
      body.scope = args.scope ?? 'global';
      if (body.scope === 'project') {
        body.projectId = args.projectId ?? PROJECT_ID;
        if (!body.projectId)
          throw new Error('Для проектной персоны нужен projectId: текущая сессия вне проекта — укажи projectId явно или создай глобальную (scope "global").');
      } else {
        delete body.projectId;
      }
      return json(await api('/api/personas', { method: 'POST', body: JSON.stringify(body) }));
    }

    case 'personas_update': {
      const body = personaBody(args, FIELD_KEYS);
      if (body.scope === 'project' && !('projectId' in body)) {
        if (!PROJECT_ID)
          throw new Error('Для смены зоны на проектную нужен projectId: текущая сессия вне проекта.');
        body.projectId = PROJECT_ID;
      }
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}`, { method: 'PUT', body: JSON.stringify(body) }));
    }

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
