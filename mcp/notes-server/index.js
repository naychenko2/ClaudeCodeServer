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
    description: 'Поиск заметок по заголовку, тексту и тегам — по всем источникам пользователя. Поддерживает операторы в query (tag:идея source:Личный status:open) и отдельный фильтр status для комментариев к документам.',
    inputSchema: {
      type: 'object',
      required: ['query'],
      properties: {
        query: { type: 'string', description: 'Строка поиска (можно с операторами tag:/source:/status:)' },
        status: { type: 'string', enum: ['open', 'resolved', 'orphaned'], description: 'Только комментарии к документам с этим статусом (open — необработанные)' },
      },
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
        expiresAfterMinutes: { type: 'number', description: 'Время жизни в минутах. Не указывать или null — бессрочно. Пример: 1440 = сутки.' },
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
  {
    name: 'notes_annotate',
    description: 'Оставить комментарий к месту в markdown-документе (создаёт заметку-комментарий со статусом open, привязанную к блоку). anchorText — ДОСЛОВНЫЙ фрагмент текста документа (скопируй точно из прочитанного файла): сервер сверяет его посимвольно и откажет, если текст не найден или неуникален. Документ — любой .md проекта (docs/, README…) или личного vault.',
    inputSchema: {
      type: 'object',
      required: ['path', 'anchorText', 'comment'],
      properties: {
        path: { type: 'string', description: 'Путь документа: для проекта — от корня проекта (docs/architecture.md), для личного vault — внутри vault' },
        scope: { type: 'string', description: 'Область документа: id проекта или "personal". По умолчанию — контекст сессии' },
        anchorText: { type: 'string', description: 'Дословный фрагмент документа, к которому привязать комментарий (минимум несколько слов, без пересказа!)' },
        comment: { type: 'string', description: 'Текст комментария' },
        tags: { type: 'array', items: { type: 'string' }, description: 'Теги (без #)' },
      },
    },
  },
  {
    name: 'notes_annotations',
    description: 'Комментарии к документу с резолвом привязки: статус (open/resolved), состояние якоря (exact/changed/orphan), цитата и позиция блока.',
    inputSchema: {
      type: 'object',
      required: ['path'],
      properties: {
        path: { type: 'string', description: 'Путь документа внутри области' },
        scope: { type: 'string', description: 'id проекта или "personal". По умолчанию — контекст сессии' },
      },
    },
  },
  {
    name: 'notes_reply',
    description: 'Ответить в треде комментария к документу (реплика — отдельная заметка, привязанная к корневому комментарию; тред плоский, отвечать можно только на корневой).',
    inputSchema: {
      type: 'object',
      required: ['id', 'comment'],
      properties: {
        id: { type: 'string', description: 'ID корневого комментария (из notes_annotations)' },
        comment: { type: 'string', description: 'Текст ответа' },
        tags: { type: 'array', items: { type: 'string' }, description: 'Теги (без #)' },
      },
    },
  },
  {
    name: 'notes_thread',
    description: 'Тред комментария: корневая заметка-комментарий целиком + все ответы по времени.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID корневого комментария' } },
    },
  },
  {
    name: 'notes_set_status',
    description: 'Сменить статус комментария к документу: resolved — обработан («решён»), open — снова открыт.',
    inputSchema: {
      type: 'object',
      required: ['id', 'status'],
      properties: {
        id: { type: 'string', description: 'ID заметки-комментария' },
        status: { type: 'string', enum: ['open', 'resolved'] },
      },
    },
  },
  {
    name: 'notes_move',
    description: 'Переместить заметку в другую папку и/или другой источник. id заметки при этом меняется (путь входит в id) — используй возвращённый id дальше. Входящие [[wikilinks]] на неё сервер чинит автоматически. Переименование (смена заголовка) делается отдельно через notes_update.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID заметки' },
        folder: { type: 'string', description: 'Целевая папка внутри источника ("Идеи/Черновики"); пусто или отсутствует — корень источника' },
        targetSource: { type: 'string', description: 'Перенести в другой источник: "personal" или id проекта. По умолчанию — текущий источник заметки' },
      },
    },
  },
  {
    name: 'notes_daily',
    description: 'Открыть или создать дневниковую заметку (Journal/YYYY-MM-DD.md в личном vault). Если передан content — дописать его в конец заметки. Удобно для быстрых записей «в дневник за сегодня».',
    inputSchema: {
      type: 'object',
      properties: {
        date: { type: 'string', description: 'Дата в формате YYYY-MM-DD. По умолчанию — сегодня.' },
        content: { type: 'string', description: 'Текст (markdown) для дописывания в конец дневниковой заметки. Пусто — просто открыть/создать.' },
      },
    },
  },
  {
    name: 'notes_resolve',
    description: 'Резолв вики-ссылки [[Имя]] в конкретную заметку (с учётом коллизий вида [[Проект/Имя]]). При заданном anchor вернёт и фрагмент заметки по якорю "#Заголовок" или "#^блок". Отвечает на вопрос «на какую именно заметку указывает эта ссылка».',
    inputSchema: {
      type: 'object',
      required: ['name'],
      properties: {
        name: { type: 'string', description: 'Имя из вики-ссылки, как в [[…]] (можно "Проект/Имя" для устранения коллизии)' },
        anchor: { type: 'string', description: 'Якорь внутри заметки: заголовок ("#Раздел") или блок ("#^abc123") — вернёт соответствующий фрагмент' },
      },
    },
  },
  {
    name: 'notes_promote_task',
    description: 'Превратить чекбокс-пункт заметки (- [ ] …) в настоящую задачу (появится в календаре, работают напоминания). Чекбокс задаётся номером строки line (0-базовый индекс строки в markdown-содержимом из notes_read) ЛИБО его текстом text (сервер сам найдёт строку). Повторный промоут той же строки вернёт уже существующую задачу.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID заметки с чекбоксом' },
        line: { type: 'integer', minimum: 0, description: '0-базовый номер строки чекбокса в содержимом заметки (notes_read)' },
        text: { type: 'string', description: 'Текст чекбокса (без "- [ ]") — альтернатива line: строка найдётся по совпадению текста' },
      },
    },
  },
  {
    name: 'notes_semantic_search',
    description: 'Семантический поиск по заметкам (по смыслу, не по подстроке): находит близкие по содержанию заметки со score и сниппетом. Используй, когда точный текст неизвестен.',
    inputSchema: {
      type: 'object',
      required: ['query'],
      properties: {
        query: { type: 'string', description: 'Смысловой запрос' },
        topK: { type: 'integer', minimum: 1, maximum: 20, description: 'Сколько результатов (по умолчанию 8)' },
      },
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
      let q = String(args.query ?? '');
      if (args.status) q = `status:${args.status} ${q}`.trim();
      const params = new URLSearchParams({ q });
      const data = await api(`/api/notes?${params}`);
      return json(data.map(n => n.annotation ? { ...brief(n), annotation: n.annotation } : brief(n)));
    }

    case 'notes_read':
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}`));

    case 'notes_create': {
      const body = { title: args.title };
      if (args.content !== undefined) body.content = args.content;
      body.source = args.source ?? DEFAULT_SOURCE;
      if (args.expiresAfterMinutes !== undefined) body.expiresAfterMinutes = args.expiresAfterMinutes;
      // Если заметка создаётся в рамках чата — запоминаем, откуда
      if (process.env.NOTES_SESSION_ID) body.sourceSessionId = process.env.NOTES_SESSION_ID;
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

    case 'notes_annotate': {
      const scope = args.scope || PROJECT_ID || 'personal';
      const text = String(args.anchorText ?? '');
      // Офсеты — хинт: сервер сам найдёт единственное дословное вхождение
      // (verify-before-write); не нашёл/неуникально — честная ошибка без порчи документа.
      const body = {
        doc: { scope, path: String(args.path ?? '') },
        selection: { start: 0, end: text.length, text },
        comment: args.comment,
      };
      if (Array.isArray(args.tags) && args.tags.length) body.tags = args.tags;
      return json(await api('/api/notes/annotate', { method: 'POST', body: JSON.stringify(body) }));
    }

    case 'notes_annotations': {
      const scope = args.scope || PROJECT_ID || 'personal';
      const params = new URLSearchParams({ scope, path: String(args.path ?? '') });
      return json(await api(`/api/notes/annotations?${params}`));
    }

    case 'notes_reply': {
      const body = { comment: args.comment };
      if (Array.isArray(args.tags) && args.tags.length) body.tags = args.tags;
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}/reply`, {
        method: 'POST', body: JSON.stringify(body),
      }));
    }

    case 'notes_thread': {
      const [root, replies] = await Promise.all([
        api(`/api/notes/${encodeURIComponent(args.id)}`),
        api(`/api/notes/${encodeURIComponent(args.id)}/replies`),
      ]);
      return json({ root, replies });
    }

    case 'notes_set_status':
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}/status`, {
        method: 'POST', body: JSON.stringify({ status: args.status }),
      }));

    case 'notes_move': {
      const body = {};
      if (args.folder !== undefined) body.folder = args.folder;
      if (args.targetSource !== undefined) body.targetSource = args.targetSource;
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}/move`, {
        method: 'POST', body: JSON.stringify(body),
      }));
    }

    case 'notes_daily': {
      const dailyBody = {};
      if (args.date !== undefined) dailyBody.date = args.date;
      const note = await api('/api/notes/daily', { method: 'POST', body: JSON.stringify(dailyBody) });
      // Дописывание не поддержано эндпоинтом — делаем сами: читаем текущий текст и PUT-им склейку
      if (args.content) {
        const base = String(note.content ?? '');
        const merged = base.length ? `${base.replace(/\s*$/, '')}\n\n${args.content}` : String(args.content);
        return json(await api(`/api/notes/${encodeURIComponent(note.id)}`, {
          method: 'PUT', body: JSON.stringify({ content: merged }),
        }));
      }
      return json(note);
    }

    case 'notes_resolve': {
      const params = new URLSearchParams({ name: String(args.name ?? '') });
      if (args.anchor) params.set('anchor', String(args.anchor));
      return json(await api(`/api/notes/resolve?${params}`));
    }

    case 'notes_promote_task': {
      let line = args.line;
      // Строку можно задать текстом чекбокса — резолвим по списку задач заметки
      if (line === undefined || line === null) {
        if (!args.text)
          throw new Error('Укажи line (номер строки) или text (текст чекбокса)');
        const rows = await api(`/api/notes/${encodeURIComponent(args.id)}/tasks`);
        const needle = String(args.text).trim();
        const hits = rows.filter(r => r.text === needle);
        const matches = hits.length ? hits : rows.filter(r => r.text.includes(needle));
        if (matches.length === 0)
          throw new Error(`Чекбокс с текстом "${needle}" не найден в заметке`);
        if (matches.length > 1)
          throw new Error(`Найдено несколько чекбоксов "${needle}" — уточни line (строки: ${matches.map(m => m.line).join(', ')})`);
        line = matches[0].line;
      }
      return json(await api(`/api/notes/${encodeURIComponent(args.id)}/tasks/promote`, {
        method: 'POST', body: JSON.stringify({ line }),
      }));
    }

    case 'notes_semantic_search': {
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      if (args.topK) params.set('topK', String(args.topK));
      const data = await api(`/api/notes/semantic?${params}`);
      if (!data.available)
        return { content: [{ type: 'text', text: 'Семантический поиск не настроен (нет Dify) — используй notes_search.' }] };
      return json(data.results);
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
