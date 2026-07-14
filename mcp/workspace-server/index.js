// MCP-сервер рабочего пространства ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
// Даёт сессии доступ ко ВСЕМ проектам владельца: список, файлы, базы знаний, единый поиск.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   WORKSPACE_API_URL         — базовый URL бэкенда (http://127.0.0.1:5000)
//   WORKSPACE_API_TOKEN       — сервисный JWT владельца сессии
//   WORKSPACE_PROJECT_ID      — id проекта текущей сессии; пусто = чат вне проекта
//   WORKSPACE_SECTIONS        — csv включённых секций (projects,files,knowledge,search[,chats,destructive])
//   WORKSPACE_PROJECT_IDS     — csv разрешённых projectId; пусто = все проекты владельца
//   WORKSPACE_SELF_SESSION_ID — id самой сессии (запрет chats_send самому себе)
//   WORKSPACE_AGENT_DEPTH     — глубина делегирования; chats_send шлёт X-Agent-Depth = depth + 1

import { createInterface } from 'node:readline';

const API_URL = (process.env.WORKSPACE_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.WORKSPACE_API_TOKEN ?? '';
const PROJECT_ID = process.env.WORKSPACE_PROJECT_ID || null;
const SECTIONS = new Set(
  (process.env.WORKSPACE_SECTIONS || 'projects,files,knowledge,search')
    .split(',').map(s => s.trim().toLowerCase()).filter(Boolean),
);
const ALLOWED_PROJECT_IDS = (process.env.WORKSPACE_PROJECT_IDS || '')
  .split(',').map(s => s.trim()).filter(Boolean);
// Секция chats: id собственной сессии (self-send запрещён) и глубина делегирования
const SELF_SESSION_ID = process.env.WORKSPACE_SELF_SESSION_ID || null;
const AGENT_DEPTH = parseInt(process.env.WORKSPACE_AGENT_DEPTH || '0', 10) || 0;

// Ограничение выдачи files_tree — дерево большого проекта не должно раздувать контекст
const TREE_MAX_ENTRIES = 500;

// --- HTTP к бэкенду ---

async function api(path, options = {}) {
  const res = await fetch(`${API_URL}${path}`, {
    ...options,
    // Таймаут: без него подвисший бэкенд вешает вызов инструмента навсегда.
    // chats_send сюда не ходит (у него свой fetch без таймаута — сервер сам отвечает 202).
    signal: AbortSignal.timeout(60_000),
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

// Валидация projectId ДО похода в REST: сужение зоны через WORKSPACE_PROJECT_IDS
function checkProjectAllowed(projectId) {
  if (!projectId) throw new Error('Не указан projectId');
  if (ALLOWED_PROJECT_IDS.length && !ALLOWED_PROJECT_IDS.includes(projectId))
    throw new Error(`Проект ${projectId} вне разрешённой зоны этой сессии`);
}

// Тип сессии (проектная / чат вне проекта): единый маршрут history отдаёт projectId.
// Нужен chats_update/chats_delete — их REST-маршруты зависят от типа сессии.
async function resolveSessionProject(sessionId) {
  const id = encodeURIComponent(String(sessionId ?? ''));
  const info = await api(`/api/sessions/${id}/history?limit=1`);
  return info?.projectId ?? null;
}

// --- Инструменты (по секциям) ---

const CONTEXT_NOTE = PROJECT_ID
  ? `Текущая сессия идёт в проекте ${PROJECT_ID}.`
  : 'Текущая сессия — чат вне проекта.';

const SECTION_TOOLS = {
  projects: [
    {
      name: 'projects_list',
      description: `Список проектов пользователя (id, название, группа, путь, число чатов). ${CONTEXT_NOTE}`,
      inputSchema: {
        type: 'object',
        properties: {
          query: { type: 'string', description: 'Фильтр по названию (подстрока, без учёта регистра)' },
        },
      },
    },
    {
      name: 'projects_get',
      description: 'Карточка проекта по id: путь, системный промпт, группа, число чатов.',
      inputSchema: {
        type: 'object',
        required: ['projectId'],
        properties: { projectId: { type: 'string', description: 'ID проекта' } },
      },
    },
    {
      name: 'projects_create',
      description: 'Создать новый проект. Без rootPath на диске СОЗДАЁТСЯ папка в стандартном каталоге ' +
        'проектов пользователя; с rootPath подключается существующая папка (должна существовать).',
      inputSchema: {
        type: 'object',
        required: ['name'],
        properties: {
          name: { type: 'string', description: 'Название проекта' },
          rootPath: { type: 'string', description: 'Абсолютный путь к существующей папке (пусто — создать новую в каталоге по умолчанию)' },
          groupId: { type: 'string', description: 'ID группы проектов (пусто — без группы)' },
        },
      },
    },
    {
      name: 'projects_update',
      description: 'Обновить проект: название, системный промпт, группа. Передавай только изменяемые поля; ' +
        'groupId "" убирает проект из группы.',
      inputSchema: {
        type: 'object',
        required: ['projectId'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          name: { type: 'string', description: 'Новое название' },
          systemPrompt: { type: 'string', description: 'Системный промпт проекта (заменяется целиком)' },
          groupId: { type: 'string', description: 'ID группы ("" — убрать из группы)' },
        },
      },
    },
  ],
  files: [
    {
      name: 'files_tree',
      description: 'Дерево файлов проекта (рекурсивно). Большая выдача усекается — уточняй path/depth.',
      inputSchema: {
        type: 'object',
        required: ['projectId'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Стартовая папка (относительный путь; пусто — корень проекта)' },
          depth: { type: 'integer', minimum: 1, description: 'Максимальная глубина вложенности от стартовой папки' },
        },
      },
    },
    {
      name: 'files_read',
      description: 'Прочитать текстовый файл проекта. Для бинарных возвращаются только тип и размер.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'path'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Относительный путь файла' },
          offset: { type: 'integer', minimum: 0, description: 'С какой строки читать (0 по умолчанию)' },
          limit: { type: 'integer', minimum: 1, description: 'Сколько строк вернуть (по умолчанию весь файл)' },
        },
      },
    },
    {
      name: 'files_write',
      description: 'Записать файл в ДРУГОМ проекте (создаёт при отсутствии, содержимое заменяется целиком). ' +
        'Только для ДРУГИХ проектов! Для файлов текущего проекта используй встроенные Read/Edit/Write.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'path', 'content'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Относительный путь файла' },
          content: { type: 'string', description: 'Полное новое содержимое файла' },
        },
      },
    },
    {
      name: 'files_search',
      description: 'Поиск файлов проекта по имени (подстрока).',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'query'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          query: { type: 'string', description: 'Подстрока имени файла' },
        },
      },
    },
    {
      name: 'files_mkdir',
      description: 'Создать папку в проекте (родительские папки создаются автоматически).',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'path'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Относительный путь новой папки' },
        },
      },
    },
    {
      name: 'files_rename',
      description: 'Переименовать или переместить файл/папку проекта.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'oldPath', 'newPath'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          oldPath: { type: 'string', description: 'Текущий относительный путь' },
          newPath: { type: 'string', description: 'Новый относительный путь' },
        },
      },
    },
  ],
  knowledge: [
    {
      name: 'knowledge_search',
      description: 'Семантический поиск по базе знаний проекта (проиндексированные документы). Возвращает чанки со score.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'query'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          query: { type: 'string', description: 'Поисковый запрос (естественный язык)' },
          topK: { type: 'integer', minimum: 1, maximum: 20, description: 'Сколько чанков вернуть (по умолчанию 8)' },
        },
      },
    },
    {
      name: 'knowledge_status',
      description: 'Статус базы знаний проекта: проиндексирована ли и список документов.',
      inputSchema: {
        type: 'object',
        required: ['projectId'],
        properties: { projectId: { type: 'string', description: 'ID проекта' } },
      },
    },
    {
      name: 'knowledge_index',
      description: 'Добавить файл проекта в базу знаний: документ загружается сразу, индексация ' +
        'продолжается в фоне (статус — через knowledge_status). Поддерживаются не все форматы.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'path'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Относительный путь файла' },
        },
      },
    },
  ],
  search: [
    {
      name: 'search_unified',
      description: 'Единый поиск по рабочему пространству пользователя (заметки + задачи, по смыслу и тексту). ' +
        'Первый шаг, когда нужно найти «что-то где-то у пользователя».',
      inputSchema: {
        type: 'object',
        required: ['query'],
        properties: {
          query: { type: 'string', description: 'Поисковый запрос' },
          limit: { type: 'integer', minimum: 1, maximum: 20, description: 'Максимум результатов (по умолчанию 8)' },
        },
      },
    },
  ],
  chats: [
    {
      name: 'chats_list',
      description: 'Список чатов пользователя: без projectId — чаты вне проектов, с projectId — сессии проекта. ' +
        'Компакт: id, name, status, personaId, model, updatedAt.',
      inputSchema: {
        type: 'object',
        properties: {
          projectId: { type: 'string', description: 'ID проекта (пусто — чаты вне проектов)' },
        },
      },
    },
    {
      name: 'chats_history',
      description: 'Последние сообщения чата/сессии по id (компактно: user/assistant/tool/result, тексты усечены).',
      inputSchema: {
        type: 'object',
        required: ['sessionId'],
        properties: {
          sessionId: { type: 'string', description: 'ID сессии' },
          limit: { type: 'integer', minimum: 1, maximum: 200, description: 'Сколько последних сообщений вернуть (по умолчанию 20)' },
        },
      },
    },
    {
      name: 'chats_create',
      description: 'Создать новый чат: без projectId — вне проектов, с projectId — сессия в проекте; ' +
        'personaId — сразу назначить собеседником персону. Возвращает id созданной сессии.',
      inputSchema: {
        type: 'object',
        properties: {
          name: { type: 'string', description: 'Название чата (пусто — авто-имя по первому сообщению)' },
          projectId: { type: 'string', description: 'ID проекта (пусто — чат вне проектов)' },
          personaId: { type: 'string', description: 'ID персоны-собеседника (пусто — обычный ассистент)' },
          model: { type: 'string', description: 'Модель (пусто — по умолчанию)' },
        },
      },
    },
    {
      name: 'chats_send',
      description: 'Отправить сообщение в СУЩЕСТВУЮЩИЙ чат — полный ход, результат виден пользователю в ленте. ' +
        'Для быстрого вопроса персоне без чата используй persona_ask. wait="turn" (дефолт) ждёт ответ до timeoutSec; ' +
        'wait="none" — не ждать (результат позже через chats_history). Ответ busy — сессия занята: ' +
        'не ретраить чаще раза в 30 секунд и не более 2 раз.',
      inputSchema: {
        type: 'object',
        required: ['sessionId', 'text'],
        properties: {
          sessionId: { type: 'string', description: 'ID сессии-получателя (не своей!)' },
          text: { type: 'string', description: 'Текст сообщения' },
          wait: { type: 'string', enum: ['turn', 'none'], description: 'turn — ждать завершения хода (дефолт), none — вернуться сразу' },
          timeoutSec: { type: 'integer', minimum: 5, maximum: 240, description: 'Сколько ждать завершения хода, сек (по умолчанию 90)' },
        },
      },
    },
    {
      name: 'chats_update',
      description: 'Переименовать чат/сессию по id (работает и для чатов вне проектов, и для проектных сессий).',
      inputSchema: {
        type: 'object',
        required: ['sessionId', 'name'],
        properties: {
          sessionId: { type: 'string', description: 'ID сессии' },
          name: { type: 'string', description: 'Новое название чата' },
        },
      },
    },
  ],
  // Разрушающие операции — отдельная секция за флагом workspace-destructive
  // (у персоны дополнительно нужен tool-ключ destructive)
  destructive: [
    {
      name: 'files_delete',
      description: 'БЕЗВОЗВРАТНО удалить файл или папку проекта — восстановить нельзя. ' +
        'Используй ТОЛЬКО по явной просьбе пользователя удалить конкретный путь, никогда по своей инициативе.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'path'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Относительный путь файла или папки' },
        },
      },
    },
    {
      name: 'chats_delete',
      description: 'БЕЗВОЗВРАТНО удалить чат/сессию вместе со всей историей сообщений пользователя. ' +
        'Используй ТОЛЬКО по явной просьбе пользователя удалить конкретный чат, никогда по своей инициативе.',
      inputSchema: {
        type: 'object',
        required: ['sessionId'],
        properties: { sessionId: { type: 'string', description: 'ID сессии' } },
      },
    },
  ],
};

const TOOLS = Object.entries(SECTION_TOOLS)
  .filter(([section]) => SECTIONS.has(section))
  .flatMap(([, tools]) => tools);

// --- Реализация инструментов ---

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

// Тул → секция (для defense-in-depth в callTool)
const TOOL_SECTION = new Map(
  Object.entries(SECTION_TOOLS).flatMap(([section, tools]) => tools.map(t => [t.name, section])));

async function callTool(name, args) {
  // Секция тула обязана быть включённой: список TOOLS фильтрует экспозицию, но исполнение
  // перепроверяем отдельно — деструктив не должен выполниться даже при ошибке экспозиции
  const section = TOOL_SECTION.get(name);
  if (section && !SECTIONS.has(section))
    throw new Error(`Инструмент ${name} недоступен: секция ${section} выключена для этой сессии`);
  switch (name) {
    case 'projects_list': {
      const [projects, groups] = await Promise.all([
        api('/api/projects'),
        api('/api/project-groups').catch(() => []),
      ]);
      const groupName = new Map((groups ?? []).map(g => [g.id, g.name]));
      const query = (args.query ?? '').trim().toLowerCase();
      const items = projects
        .filter(p => !ALLOWED_PROJECT_IDS.length || ALLOWED_PROJECT_IDS.includes(p.id))
        .filter(p => !query || String(p.name ?? '').toLowerCase().includes(query))
        .map(p => ({
          id: p.id,
          name: p.name,
          groupName: p.groupId ? groupName.get(p.groupId) ?? null : null,
          rootPath: p.rootPath,
          sessionCount: p.sessionCount ?? null,
          isCurrent: p.id === PROJECT_ID || undefined,
        }));
      return json(items);
    }

    case 'projects_get': {
      checkProjectAllowed(args.projectId);
      const p = await api(`/api/projects/${args.projectId}`);
      return json({
        id: p.id,
        name: p.name,
        rootPath: p.rootPath,
        groupId: p.groupId ?? null,
        systemPrompt: p.systemPrompt ?? null,
        sessionCount: p.sessionCount ?? null,
        createdAt: p.createdAt,
        updatedAt: p.updatedAt,
      });
    }

    case 'projects_create': {
      // Сужение зоны: сессии с ограниченным списком проектов новые проекты не создают
      if (ALLOWED_PROJECT_IDS.length)
        throw new Error('Создание проектов недоступно: зона этой сессии ограничена перечисленными проектами');
      const p = await api('/api/projects', {
        method: 'POST',
        body: JSON.stringify({
          name: String(args.name ?? '').trim(),
          rootPath: args.rootPath || null,
          groupId: args.groupId || null,
        }),
      });
      return json({ id: p.id, name: p.name, rootPath: p.rootPath, groupId: p.groupId ?? null });
    }

    case 'projects_update': {
      checkProjectAllowed(args.projectId);
      // PUT частичный: null-поля бэкенд не трогает, groupId "" убирает из группы
      const p = await api(`/api/projects/${args.projectId}`, {
        method: 'PUT',
        body: JSON.stringify({
          name: args.name ?? null,
          systemPrompt: args.systemPrompt ?? null,
          groupId: args.groupId ?? null,
        }),
      });
      return json({ id: p.id, name: p.name, rootPath: p.rootPath, groupId: p.groupId ?? null });
    }

    case 'files_tree': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams();
      const base = String(args.path ?? '').replace(/\\/g, '/').replace(/\/+$/, '');
      if (base) params.set('path', base);
      const entries = await api(`/api/projects/${args.projectId}/files/tree?${params}`);
      // Глубина считается от стартовой папки по числу сегментов относительного пути
      const baseDepth = base ? base.split('/').length : 0;
      let list = entries.map(e => ({ path: e.path.replace(/\\/g, '/'), dir: e.isDirectory, size: e.size ?? null }));
      if (args.depth)
        list = list.filter(e => e.path.split('/').length - baseDepth <= args.depth);
      const truncated = list.length > TREE_MAX_ENTRIES;
      if (truncated) list = list.slice(0, TREE_MAX_ENTRIES);
      return json({ entries: list, ...(truncated ? { truncated: true, note: `Показаны первые ${TREE_MAX_ENTRIES} записей — уточни path/depth` } : {}) });
    }

    case 'files_read': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ path: String(args.path ?? '') });
      const data = await api(`/api/projects/${args.projectId}/files/content?${params}`);
      if (data.isBinary) {
        // base64 не тащим — бинарник раздул бы контекст
        return json({
          path: args.path,
          binary: true,
          mimeType: data.mimeType ?? null,
          fileSize: data.fileSize ?? null,
          note: 'Бинарный файл — содержимое не возвращается',
        });
      }
      let text = data.content ?? '';
      if (args.offset || args.limit) {
        const lines = text.split('\n');
        const start = Math.max(0, args.offset ?? 0);
        const slice = lines.slice(start, args.limit ? start + args.limit : undefined);
        return json({ path: args.path, offsetLines: start, totalLines: lines.length, content: slice.join('\n') });
      }
      return json({ path: args.path, content: text });
    }

    case 'files_write': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ path: String(args.path ?? '') });
      const put = () => api(`/api/projects/${args.projectId}/files/content?${params}`, {
        method: 'PUT', body: JSON.stringify({ content: String(args.content ?? '') }),
      });
      try {
        await put();
      } catch (err) {
        if (err.status !== 404) throw err;
        // Файла нет — создаём и повторяем запись
        await api(`/api/projects/${args.projectId}/files/create`, {
          method: 'POST', body: JSON.stringify({ path: String(args.path ?? '') }),
        });
        await put();
      }
      return { content: [{ type: 'text', text: `Файл ${args.path} записан в проект ${args.projectId}.` }] };
    }

    case 'files_search': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      const entries = await api(`/api/projects/${args.projectId}/files/search?${params}`);
      return json(entries.map(e => ({ path: e.path.replace(/\\/g, '/'), size: e.size ?? null })));
    }

    case 'files_mkdir': {
      checkProjectAllowed(args.projectId);
      await api(`/api/projects/${args.projectId}/files/mkdir`, {
        method: 'POST', body: JSON.stringify({ path: String(args.path ?? '') }),
      });
      return { content: [{ type: 'text', text: `Папка ${args.path} создана в проекте ${args.projectId}.` }] };
    }

    case 'files_rename': {
      checkProjectAllowed(args.projectId);
      await api(`/api/projects/${args.projectId}/files/rename`, {
        method: 'POST',
        body: JSON.stringify({ oldPath: String(args.oldPath ?? ''), newPath: String(args.newPath ?? '') }),
      });
      return { content: [{ type: 'text', text: `${args.oldPath} → ${args.newPath} (проект ${args.projectId}).` }] };
    }

    case 'files_delete': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ path: String(args.path ?? '') });
      await api(`/api/projects/${args.projectId}/files?${params}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `${args.path} безвозвратно удалён из проекта ${args.projectId}.` }] };
    }

    case 'knowledge_search': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      if (args.topK) params.set('topK', String(args.topK));
      return json(await api(`/api/projects/${args.projectId}/knowledge/search?${params}`));
    }

    case 'knowledge_status': {
      checkProjectAllowed(args.projectId);
      const data = await api(`/api/projects/${args.projectId}/knowledge`);
      return json({
        indexed: Boolean(data.datasetId),
        total: data.total ?? 0,
        documents: (data.documents ?? []).map(d => ({ name: d.name, indexingStatus: d.indexingStatus })),
      });
    }

    case 'knowledge_index': {
      checkProjectAllowed(args.projectId);
      const data = await api(`/api/projects/${args.projectId}/knowledge/index`, {
        method: 'POST', body: JSON.stringify({ relativePath: String(args.path ?? '') }),
      });
      // Загрузка синхронная, дальше Dify индексирует в фоне — честно отдаём статус
      return json({
        document: { id: data.document?.id, name: data.document?.name, indexingStatus: data.document?.indexingStatus },
        note: 'Документ загружен, индексация выполняется в фоне — статус через knowledge_status.',
      });
    }

    case 'chats_list': {
      let sessions;
      if (args.projectId) {
        checkProjectAllowed(args.projectId);
        sessions = await api(`/api/projects/${args.projectId}/sessions`);
      } else {
        sessions = await api('/api/chats');
      }
      return json(sessions.map(s => ({
        id: s.id,
        name: s.name ?? null,
        status: s.status,
        personaId: s.personaId ?? null,
        model: s.model ?? null,
        updatedAt: s.updatedAt,
        isSelf: s.id === SELF_SESSION_ID || undefined,
      })));
    }

    case 'chats_history': {
      const params = new URLSearchParams();
      if (args.limit) params.set('limit', String(args.limit));
      const data = await api(`/api/sessions/${encodeURIComponent(String(args.sessionId ?? ''))}/history?${params}`);
      // Зона сессии: маршрут адресуется по sessionId в обход projectId — проверяем по ответу
      // (history отдаёт projectId), иначе суженная зона читала бы чаты чужих проектов владельца
      if (data?.projectId) checkProjectAllowed(data.projectId);
      return json(data);
    }

    case 'chats_create': {
      let created;
      if (args.projectId) {
        checkProjectAllowed(args.projectId);
        created = await api(`/api/projects/${args.projectId}/sessions`, {
          method: 'POST',
          body: JSON.stringify({ name: args.name ?? null, model: args.model ?? null }),
        });
      } else {
        created = await api('/api/chats', {
          method: 'POST',
          body: JSON.stringify({ name: args.name ?? null, model: args.model ?? null }),
        });
      }
      // Собеседник назначается отдельным вызовом — маршрут зависит от типа сессии
      if (args.personaId) {
        const personaPath = args.projectId
          ? `/api/projects/${args.projectId}/sessions/${created.id}/persona`
          : `/api/chats/${created.id}/persona`;
        await api(personaPath, { method: 'POST', body: JSON.stringify({ personaId: args.personaId }) });
      }
      return json({ id: created.id, name: created.name ?? null, projectId: created.projectId ?? null });
    }

    case 'chats_update': {
      const sessionId = String(args.sessionId ?? '');
      if (!sessionId) throw new Error('Не указан sessionId');
      const name = String(args.name ?? '').trim();
      if (!name) throw new Error('Название чата пусто');
      const projectId = await resolveSessionProject(sessionId);
      let updated;
      if (projectId) {
        checkProjectAllowed(projectId);
        updated = await api(`/api/projects/${projectId}/sessions/${encodeURIComponent(sessionId)}`, {
          method: 'PUT', body: JSON.stringify({ name }),
        });
      } else {
        updated = await api(`/api/chats/${encodeURIComponent(sessionId)}`, {
          method: 'PUT', body: JSON.stringify({ name }),
        });
      }
      return json({ id: updated.id, name: updated.name ?? null, projectId: updated.projectId ?? null });
    }

    case 'chats_delete': {
      const sessionId = String(args.sessionId ?? '');
      if (!sessionId) throw new Error('Не указан sessionId');
      // Удаление собственной сессии оборвало бы текущий ход — запрещаем до запроса
      if (SELF_SESSION_ID && sessionId === SELF_SESSION_ID)
        throw new Error('Нельзя удалить собственный чат — chats_delete адресован ДРУГИМ сессиям');
      const projectId = await resolveSessionProject(sessionId);
      if (projectId) {
        checkProjectAllowed(projectId);
        await api(`/api/projects/${projectId}/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
      } else {
        await api(`/api/chats/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
      }
      return { content: [{ type: 'text', text: `Чат ${sessionId} безвозвратно удалён вместе с историей.` }] };
    }

    case 'chats_send': {
      const sessionId = String(args.sessionId ?? '');
      if (!sessionId) throw new Error('Не указан sessionId');
      // Запрет self-send — рекурсивный ход в собственную сессию (проверка ДО запроса)
      if (SELF_SESSION_ID && sessionId === SELF_SESSION_ID)
        throw new Error('Нельзя писать в собственный чат — chats_send адресован ДРУГИМ сессиям');
      const text = String(args.text ?? '').trim();
      if (!text) throw new Error('Текст сообщения пуст');
      // Зона сессии: маршрут адресуется по sessionId в обход projectId — при суженной зоне
      // резолвим проект чата и проверяем до отправки (как chats_update/chats_delete)
      if (ALLOWED_PROJECT_IDS.length) {
        const targetProjectId = await resolveSessionProject(sessionId);
        if (targetProjectId) checkProjectAllowed(targetProjectId);
      }

      // fetch без своего таймаута: сервер сам вернёт 202 по истечении timeoutSec (макс 240с,
      // меньше дефолтных таймаутов undici) — обрывать запрос раньше сервера нельзя
      const res = await fetch(`${API_URL}/api/sessions/${encodeURIComponent(sessionId)}/messages`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json; charset=utf-8',
          Authorization: `Bearer ${API_TOKEN}`,
          // Глубина делегирования растёт на каждый хоп — сервер урезает инструменты по ней
          'X-Agent-Depth': String(AGENT_DEPTH + 1),
          // Своя сессия — получатель по её PersonaId отрисует входящую реплику лицом персоны
          ...(SELF_SESSION_ID ? { 'X-Sender-Session-Id': SELF_SESSION_ID } : {}),
        },
        body: JSON.stringify({
          text,
          wait: args.wait ?? 'turn',
          ...(args.timeoutSec ? { timeoutSec: args.timeoutSec } : {}),
        }),
      });
      const body = await res.json().catch(() => null);
      // 200 completed / 202 running / 409 busy — все три отдаём модели как результат
      // (busy содержит hint про ретраи, модель решает сама)
      if (res.ok || res.status === 202 || res.status === 409)
        return json(body ?? { status: res.status });
      if (res.status === 404) throw new Error(`Сессия ${sessionId} не найдена`);
      throw new Error(`HTTP ${res.status}: ${body ? JSON.stringify(body) : ''}`);
    }

    case 'search_unified': {
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      if (args.limit) params.set('topK', String(args.limit));
      try {
        return json(await api(`/api/search?${params}`));
      } catch (err) {
        if (err.status === 403)
          throw new Error('Единый поиск выключен у пользователя (флаг «Расширенный AI»)');
        throw err;
      }
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
          serverInfo: { name: 'wsp', version: '1.0.0' },
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
