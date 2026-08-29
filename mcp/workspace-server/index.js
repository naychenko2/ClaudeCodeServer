// MCP-сервер рабочего пространства ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
// Даёт сессии доступ ко ВСЕМ проектам владельца: список, файлы, базы знаний, единый поиск.
//
// ЗАМОРОЖЕН (ветка отката, ADR-012 фаза 2 волна 3): источник контракта —
// backend/ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs (+ .Schemas.cs); правки
// схем/поведения обязаны ехать парой с http-веткой (сторож парности —
// WorkspaceToolsetParityTests, оси секций сверяются с ЖИВЫМ этим файлом).
// Файл не удалять: Mcp:HttpTransport=false возвращает wsp на stdio с этим env.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   WORKSPACE_API_URL         — базовый URL бэкенда (http://127.0.0.1:5000)
//   WORKSPACE_API_TOKEN       — сервисный JWT владельца сессии
//   WORKSPACE_PROJECT_ID      — id проекта текущей сессии; пусто = чат вне проекта
//   WORKSPACE_SECTIONS        — csv включённых секций (projects,files,knowledge,search[,chats,
//                               git,git_write,knowledge_bases,destructive,deploy]). git — чтение истории
//                               (status/diff/log), git_write — её запись (stage/commit): секцию
//                               записи даёт только явно включённый персоне ключ git, пресет
//                               по роли оставляет чтение (см. SessionManager.BuildWorkspaceContext)
//                               deploy — выкатка прода (ADR-010): только на машине с настроенным
//                               контуром Deploy и только владельцу-админу
//   WORKSPACE_PROJECT_IDS     — csv разрешённых projectId; пусто = все проекты владельца
//   WORKSPACE_SELF_SESSION_ID — id самой сессии: запрет chats_send самому себе + заголовок
//                               X-Caller-Session-Id (по нему бэкенд сам определяет глубину
//                               делегирования идущего хода и гейтит chats_send/удаление)
//   WORKSPACE_CHAT_CONTEXT    — "1" = у владельца включён флаг chat-context: в составе появляется
//                               context_list (материалы, закреплённые за чатом). Гейт по ВЛАДЕЛЬЦУ,
//                               не по ходу: значение постоянно в рамках сессии, состав tools/list
//                               от содержимого контекста не зависит (см. BASE_TOOLS ниже)
//   WORKSPACE_WRITE           — "0" = скрыть write-инструменты (projects_create/update,
//                               files_write/mkdir/rename, knowledge_index, git_commit/stage,
//                               kb_add_document). ClaudeSession выключает их на ходах без интента
//                               записи в рабочее пространство — read-инструменты
//                               (list/tree/read/search/status/history) остаются всегда. Дефолт —
//                               включено; выключается только явным "0".
//                               (destructive-инструменты гейтятся отдельной секцией сверх этого.)
//                               chats_create/send/update под этот гейт НЕ попадают — см. WRITE_TOOLS.

import { createInterface } from 'node:readline';
import { AsyncLocalStorage } from 'node:async_hooks';

// Процесс сервера не имеет права умирать от одной необработанной ошибки: вместе с ним из хода
// исчезают ВСЕ его инструменты, а незавершённые вызовы падают «Stream closed» — ровно та
// картина, на которую жаловались. Логируем и продолжаем жить: обработчики вызовов независимы,
// поэтому уцелевший процесс отработает следующий вызов, а причина останется в stderr.
process.on('unhandledRejection', err => {
  console.error(`[mcp] необработанное отклонение промиса: ${err?.stack ?? err}`);
});
process.on('uncaughtException', err => {
  console.error(`[mcp] необработанное исключение: ${err?.stack ?? err}`);
});
// CLI закрыл pipe (убил сервер, завершился ход) — писать больше некуда. Это штатное
// завершение, а не авария: без обработчика EPIPE всплыл бы как uncaughtException.
process.stdout.on('error', err => {
  if (err?.code === 'EPIPE') process.exit(0);
  console.error(`[mcp] ошибка записи в stdout: ${err?.message ?? err}`);
});


// Имя инструмента текущего вызова — уезжает в заголовке X-Mcp-Tool: без него на бэкенде
// не видно, ЧТО именно отказало (диагностика MCP шла только разбором истории чатов вручную).
// AsyncLocalStorage, а не переменная модуля: вызовы инструментов идут параллельно и
// перетирали бы её друг у друга.
const callCtx = new AsyncLocalStorage();


const API_URL = (process.env.WORKSPACE_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.WORKSPACE_API_TOKEN ?? '';
const PROJECT_ID = process.env.WORKSPACE_PROJECT_ID || null;
const SECTIONS = new Set(
  (process.env.WORKSPACE_SECTIONS || 'projects,files,knowledge,search')
    .split(',').map(s => s.trim().toLowerCase()).filter(Boolean),
);
const ALLOWED_PROJECT_IDS = (process.env.WORKSPACE_PROJECT_IDS || '')
  .split(',').map(s => s.trim()).filter(Boolean);
// Секция chats: id собственной сессии (self-send запрещён; он же — X-Caller-Session-Id)
const SELF_SESSION_ID = process.env.WORKSPACE_SELF_SESSION_ID || null;
// Контекст чата (флаг владельца chat-context): включает context_list в BASE_TOOLS
const CHAT_CONTEXT = process.env.WORKSPACE_CHAT_CONTEXT === '1';
// Write-инструменты рабочего пространства — скрыты при WORKSPACE_WRITE="0" (гейт по интенту хода).
// Выключается только явным "0" (обратная совместимость прямых запусков). Тяжёлые схемы записи
// (files_write с content, projects_create/update) не грузятся в контекст на ходах чтения.
//
// chats_create/chats_send/chats_update СЮДА НЕ ВХОДЯТ, хотя и пишут: их состав обязан быть
// детерминированным на всех ходах сессии. Под гейтом по тексту хода они то появлялись, то
// исчезали (ход «отправь сообщение в чат» поднимал их, следующий «что там по задаче» — нет),
// а вместе с ними менялась сигнатура MCP → claude переподключал wsp-сервер. Персона-постановщик
// с ProjectPersonas-привязкой видела это как «функции то есть, то нет». Гейт им и не нужен:
// схемы лёгкие, а сама секция chats монтируется только персоне, допущенной к чужим чатам
// (tool-ключ chats / ProjectPersonas), и снимается вовсе на агентном ходу (анти-рекурсия).
const WRITE = process.env.WORKSPACE_WRITE !== '0';
const WRITE_TOOLS = new Set([
  'projects_create', 'projects_update', 'files_write', 'files_mkdir', 'files_rename',
  'files_to_markdown',
  'knowledge_index',
  'git_commit', 'git_stage', 'kb_add_document',
]);

// Ограничение выдачи files_tree — дерево большого проекта не должно раздувать контекст
const TREE_MAX_ENTRIES = 500;
// Потолок выдачи files_read, как у встроенного Read: файл целиком живёт в контексте
// до конца сессии, а читают им обычно ради куска. Явный limit больше потолка уважаем —
// модель попросила осознанно.
const READ_MAX_LINES = 2000;

// --- HTTP к бэкенду ---

// Секундная недоступность бэкенда (рестарт, деплой, перезапуск прокси) превращалась в серию
// красных карточек: ретраев не было ни одного. Паузы короткие — вызов инструмента не должен
// висеть минутами, а бэкенд поднимается быстро.
const RETRY_DELAYS_MS = [300, 900];
// За частью эндпоинтов стоит вызов модели (локальная Ollama или Claude) — им дефолта мало
const LLM_TIMEOUT_MS = 180_000;
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

async function api(path, { timeoutMs = 60_000, ...options } = {}, attempt = 0) {
  try {
    const res = await fetch(`${API_URL}${path}`, {
      ...options,
      // Таймаут: без него подвисший бэкенд вешает вызов инструмента до дефолта undici (~300с)
      signal: AbortSignal.timeout(timeoutMs),
      headers: {
        'Content-Type': 'application/json; charset=utf-8',
        Authorization: `Bearer ${API_TOKEN}`,
        ...(callCtx.getStore()?.tool ? { 'X-Mcp-Tool': callCtx.getStore().tool } : {}),
        // Сессия, в которой работает модель: по ней бэкенд определяет, идёт ли делегированный
        // ход, и сам решает, что на нём запрещено (запись в третьи чаты, удаление). Состав
        // инструментов сервера при этом остаётся неизменным — иначе процесс CLI перезапускается.
        ...(SELF_SESSION_ID ? { 'X-Caller-Session-Id': SELF_SESSION_ID } : {}),
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

// Тело успешного ответа. Пустое тело — НЕ ошибка: на запись ASP.NET отвечает `Ok()`
// без объекта (files_write, files_mkdir, files_rename, files_create), а `res.json()`
// кидал на нём «Unexpected end of JSON input» — удавшаяся запись выглядела для модели
// провалом, и она повторяла её второй раз.
async function parseBody(res) {
  if (res.status === 204) return null;
  const text = await res.text();
  if (!text) return null;
  try { return JSON.parse(text); }
  catch { return text; }
}

// Заявка на выкатку/откат прода: POST + расшифровка отказа (ADR-010). Коды /api/deploy
// содержательны и требуют РАЗНЫХ действий человека, а общий describeError свёл бы их к
// «сейчас занято» и «отказ». Модель не должна принять отказ за принятую заявку, поэтому
// причина и следующий шаг проговариваются явно, а ошибка остаётся ошибкой (isError).
async function deployCall(path, body, action) {
  try {
    return await api(path, { method: 'POST', body: JSON.stringify(body) });
  } catch (err) {
    let parsed = null;
    try { parsed = err?.bodyText ? JSON.parse(err.bodyText) : null; } catch { /* тело не JSON */ }
    const reason = parsed?.error ?? (err?.bodyText || '');
    if (err?.status === 409)
      throw new Error(`${action} НЕ запущена: ${reason || 'на сервере уже идёт другая выкатка'}. `
        + 'Двух одновременно не бывает — посмотри deploy_status и дождись конца текущей.');
    if (err?.status === 400 && Array.isArray(parsed?.files))
      throw new Error(`${action} НЕ запущена: в рабочем дереве незакоммиченные изменения `
        + `(${parsed.files.length}): ${parsed.files.slice(0, 20).join(', ')}`
        + (parsed.files.length > 20 ? ', …' : '') + '. Покажи список пользователю и спроси, '
        + 'коммитить ли их. Ехать как есть — только по его явному согласию, повторным вызовом '
        + 'с allowDirty=true: тогда в прод уедет ровно то, что лежит в дереве.');
    if (err?.status === 400)
      throw new Error(`${action} НЕ запущена: ${reason || 'запрос отклонён сервером'}. `
        + 'Повторять тот же вызов бессмысленно — исправь параметры или спроси пользователя.');
    if (err?.status === 503)
      throw new Error(`${action} НЕ запущена: механизм выкатки на этой машине не настроен `
        + `(${reason}). Это не временный сбой и не занятость — включить контур может только `
        + 'администратор в appsettings.Local.json. Сообщи это пользователю и не повторяй вызов.');
    if (err?.status === 403)
      throw new Error(`${action} НЕ запущена: нужны права администратора. ${reason} `
        + 'Повторять вызов бессмысленно — сообщи пользователю.');
    throw err;
  }
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

// Объединение списков строк с дедупом по регистру: сохраняем первое вхождение имени,
// «Bug» и «bug» не плодят дубль. Реестр тегов и теги/метки сущности — списки строк,
// регистр в них не должен создавать визуальных и логических дубликатов.
function unionStrings(existing, incoming) {
  const seen = new Set();
  const result = [];
  for (const raw of [...(existing ?? []), ...(incoming ?? [])]) {
    const name = String(raw ?? '').trim();
    if (!name) continue;
    const key = name.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(name);
  }
  return result;
}

// Вычитание списков строк по регистру (ключ тот же, что у unionStrings): снимаемые имена
// сравниваются без учёта регистра, остальные не трогаются. removed — реально снятые имена
// в каноническом написании сущности; отсутствующий тег — не ошибка (идемпотентность).
function subtractStrings(existing, removing) {
  const set = new Set(
    (removing ?? []).map(r => String(r ?? '').trim().toLowerCase()).filter(Boolean));
  const kept = [];
  const removed = [];
  for (const raw of existing ?? []) {
    const name = String(raw ?? '').trim();
    if (!name) continue;
    if (set.has(name.toLowerCase())) removed.push(name);
    else kept.push(name);
  }
  return { kept, removed };
}

// Автосоздание тегов в реестре проекта: недостающие (case-insensitive) имена добавляются
// с order = max+1 и color = null, реестр сохраняется целиком (PUT .../tags). Бэкенд сам
// перенормирует order по позиции массива и требует уникальность имён без учёта регистра —
// поэтому additions проверяем и против существующих, и между собой до отправки.
async function ensureRegistryTags(projectId, tags) {
  const project = await api(`/api/projects/${projectId}`);
  const registry = Array.isArray(project?.tagRegistry) ? project.tagRegistry : [];
  const known = new Set(registry.map(t => String(t.name ?? '').toLowerCase()));
  const maxOrder = registry.reduce((m, t) => Math.max(m, Number(t.order ?? -1)), -1);
  let nextOrder = maxOrder + 1;
  const additions = [];
  for (const name of tags) {
    const key = String(name).toLowerCase();
    if (known.has(key)) continue;
    known.add(key);
    additions.push({ name, order: nextOrder++, color: null });
  }
  if (!additions.length) return [];
  await api(`/api/projects/${projectId}/tags`, {
    method: 'PUT', body: JSON.stringify([...registry, ...additions]),
  });
  return additions.map(a => a.name);
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
    {
      name: 'tags_apply',
      description: 'Присвоить теги сущности (сессии или задаче): новые теги объединяются с уже ' +
        'висящими, дубликаты без учёта регистра не плодятся. Недостающие теги автоматически ' +
        'попадают в реестр тегов проекта. entityType="session" — projectId обязателен; ' +
        'entityType="task" — projectId опционален (нужен только для автосоздания в реестре, ' +
        'личные задачи работают без него).',
      inputSchema: {
        type: 'object',
        required: ['entityType', 'entityId', 'tags'],
        properties: {
          entityType: { type: 'string', enum: ['session', 'task'], description: 'Тип сущности' },
          entityId: { type: 'string', description: 'ID сессии или задачи' },
          projectId: {
            type: 'string',
            description: 'ID проекта (для session обязательно; для task опционально — для автосоздания тегов в реестре)',
          },
          tags: {
            type: 'array',
            items: { type: 'string' },
            description: 'Теги для присвоения (добавляются к существующим, без удаления)',
          },
        },
      },
    },
    {
      name: 'tags_remove',
      description: 'Снять теги с сущности (сессии или задачи): перечисленные теги удаляются, остальные ' +
        'не трогаются; сравнение без учёта регистра, отсутствующий тег — не ошибка ' +
        '(идемпотентно). Реестр тегов проекта не чистится — это словарь доступных имён. ' +
        'entityType="session" — projectId обязателен; entityType="task" — projectId ' +
        'опционален.',
      inputSchema: {
        type: 'object',
        required: ['entityType', 'entityId', 'tags'],
        properties: {
          entityType: { type: 'string', enum: ['session', 'task'], description: 'Тип сущности' },
          entityId: { type: 'string', description: 'ID сессии или задачи' },
          projectId: {
            type: 'string',
            description: 'ID проекта (для session обязательно; для task опционален)',
          },
          tags: {
            type: 'array',
            items: { type: 'string' },
            description: 'Теги для снятия (остальные теги сущности не трогаются)',
          },
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
      description: `Прочитать текстовый файл проекта. Для бинарных возвращаются только тип и размер. ` +
        `Выдача обрезается до ${READ_MAX_LINES} строк — читай длинный файл кусками (offset/limit), ` +
        `а нужное место ищи files_search/knowledge_search: прочитанное остаётся в контексте до конца сессии.`,
      inputSchema: {
        type: 'object',
        required: ['projectId', 'path'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Относительный путь файла' },
          offset: { type: 'integer', minimum: 0, description: 'С какой строки читать (0 по умолчанию)' },
          limit: { type: 'integer', minimum: 1, description: `Сколько строк вернуть (по умолчанию ${READ_MAX_LINES})` },
        },
      },
    },
    {
      name: 'files_document_read',
      description: 'Конвертировать бинарный документ проекта (pdf/docx/xlsx/pptx) в Markdown и вернуть текст ' +
        '(markitdown, без модели). Так можно «прочитать» офисный документ, который files_read не отдаёт.',
      inputSchema: {
        type: 'object', required: ['projectId', 'path'],
        properties: { projectId: { type: 'string' }, path: { type: 'string', description: 'Путь к документу' } },
      },
    },
    {
      name: 'files_document_summary',
      description: 'Краткое содержание документа проекта (pdf/docx/xlsx/pptx): 5-8 пунктов сути. ' +
        'Бесплатная локальная модель, если настроена.',
      inputSchema: {
        type: 'object', required: ['projectId', 'path'],
        properties: { projectId: { type: 'string' }, path: { type: 'string' } },
      },
    },
    {
      name: 'files_document_extract',
      description: 'Структурная выжимка из документа: {decisions, dates, people, actionItems}. Локальная модель.',
      inputSchema: {
        type: 'object', required: ['projectId', 'path'],
        properties: { projectId: { type: 'string' }, path: { type: 'string' } },
      },
    },
    {
      name: 'files_to_markdown',
      description: 'Трансформировать ЛЮБОЙ файл (pdf/docx/xlsx/pptx/html/csv и др.) в Markdown и СОХРАНИТЬ его ' +
        '(markitdown, без модели). По умолчанию .md кладётся рядом с исходником; можно указать targetDir — ' +
        'папку назначения (относительно корня проекта, создаётся при отсутствии). Возвращает { savedPath }. ' +
        'Используй, когда просят «трансформировать/переделать файл в markdown» и (опционально) сохранить в папку.',
      inputSchema: {
        type: 'object', required: ['projectId', 'path'],
        properties: {
          projectId: { type: 'string' },
          path: { type: 'string', description: 'Путь исходного файла' },
          targetDir: { type: 'string', description: 'Папка назначения (относительно корня проекта). Пусто — рядом с исходником.' },
          enhance: { type: 'boolean', description: 'Восстановить Markdown-разметку локальной моделью (заголовки/списки/выделения) — полезно для PDF, дающих плоский текст. По умолчанию false.' },
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
  // Секция git — ЧТЕНИЕ истории git-репозитория ЛЮБОГО проекта владельца. Все инструменты
  // требуют projectId и уважают WORKSPACE_PROJECT_IDS (checkProjectAllowed).
  // git_blame и git_file_log отсюда убраны: за две недели наблюдений их не звали ни разу,
  // а схемы висели в контексте каждого хода (REST-эндпоинты бэкенда остались на месте).
  git: [
    {
      name: 'git_status',
      description: 'Статус git-репозитория проекта: текущая ветка, upstream, staged/unstaged/untracked файлы. ' +
        'Если папка проекта не git-репозиторий — возвращается ошибка.',
      inputSchema: {
        type: 'object',
        required: ['projectId'],
        properties: { projectId: { type: 'string', description: 'ID проекта' } },
      },
    },
    {
      name: 'git_diff',
      description: 'Diff файла проекта: рабочие правки (staged=false, по умолчанию) либо проиндексированные (staged=true). ' +
        'path — относительный путь файла.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'path'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          path: { type: 'string', description: 'Относительный путь файла' },
          staged: { type: 'boolean', description: 'true — diff проиндексированных изменений (git diff --staged); false — рабочих (по умолчанию)' },
        },
      },
    },
    {
      name: 'git_log',
      description: 'История коммитов проекта (последние limit, по умолчанию 100). branch — конкретная ветка (пусто — текущая).',
      inputSchema: {
        type: 'object',
        required: ['projectId'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          limit: { type: 'integer', minimum: 1, description: 'Сколько коммитов вернуть (по умолчанию 100)' },
          branch: { type: 'string', description: 'Ветка (пусто — текущая)' },
        },
      },
    },
  ],
  // Секция git_write — запись истории (индексация и коммит). Отдельно от чтения: коммитят
  // обычно через Bash, а ролям «только чтение» запись не нужна по определению.
  git_write: [
    {
      name: 'git_commit',
      description: 'Зафиксировать проиндексированные изменения проекта коммитом с сообщением message. ' +
        'Возвращает sha созданного коммита. Требует непустого сообщения.',
      inputSchema: {
        type: 'object',
        required: ['projectId', 'message'],
        properties: {
          projectId: { type: 'string', description: 'ID проекта' },
          message: { type: 'string', description: 'Сообщение коммита' },
        },
      },
    },
    {
      name: 'git_stage',
      description: 'Проиндексировать (git add) файл проекта по относительному пути path перед коммитом.',
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
  // Секция knowledge_bases — личные и публичные базы знаний Dify владельца (раздел «Знания»),
  // НЕ проектные базы (для тех — секция knowledge). Инструменты — уровня владельца, projectId нет.
  knowledge_bases: [
    {
      name: 'kb_list',
      description: 'Список баз знаний владельца (личные + публичные): id, название, тип, видимость, число документов. ' +
        'Не путать с базой знаний текущего проекта (knowledge_status/knowledge_search).',
      inputSchema: { type: 'object', properties: {} },
    },
    {
      name: 'kb_get',
      description: 'Карточка базы знаний по id: метаданные + список документов с их статусом индексации.',
      inputSchema: {
        type: 'object',
        required: ['id'],
        properties: { id: { type: 'string', description: 'ID базы знаний' } },
      },
    },
    {
      name: 'kb_search',
      description: 'Поиск по базе знаний: method="semantic" (по смыслу, по умолчанию) либо "fulltext" (точные совпадения). ' +
        'Возвращает чанки со score.',
      inputSchema: {
        type: 'object',
        required: ['id', 'query'],
        properties: {
          id: { type: 'string', description: 'ID базы знаний' },
          query: { type: 'string', description: 'Поисковый запрос' },
          topK: { type: 'integer', minimum: 1, maximum: 20, description: 'Сколько чанков вернуть (по умолчанию 8)' },
          method: { type: 'string', enum: ['semantic', 'fulltext'], description: 'Стратегия поиска (по умолчанию semantic)' },
        },
      },
    },
    {
      name: 'kb_add_document',
      description: 'Добавить документ в базу знаний текстом: name — имя документа, text — содержимое. ' +
        'Индексация продолжается в фоне (статус — через kb_get).',
      inputSchema: {
        type: 'object',
        required: ['id', 'name', 'text'],
        properties: {
          id: { type: 'string', description: 'ID базы знаний' },
          name: { type: 'string', description: 'Имя документа' },
          text: { type: 'string', description: 'Текстовое содержимое документа' },
        },
      },
    },
  ],
  chats: [
    {
      name: 'chats_list',
      description: 'Список чатов пользователя: без projectId — чаты вне проектов, с projectId — сессии проекта. ' +
        'Компакт: id, name, status, personaId, model, updatedAt, isArchived. ' +
        'Архивные чаты по умолчанию НЕ отдаются — чтобы увидеть их, передай includeArchived: true.',
      inputSchema: {
        type: 'object',
        properties: {
          projectId: { type: 'string', description: 'ID проекта (пусто — чаты вне проектов)' },
          includeArchived: { type: 'boolean', description: 'Показывать и архивные чаты (по умолчанию false — только живые)' },
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
        'wait="none" — не ждать (результат позже через chats_history). Ответ queued — чат был занят, ' +
        'сообщение ПРИНЯТО и уйдёт само после текущего хода: не отправляй повторно, ответ смотри через chats_history.',
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
      name: 'chats_report_up',
      description: 'Отчитаться в ВЫШЕСТОЯЩИЙ чат — тот, из которого пришла твоя задача (или в который ' +
        'тебя сгруппировали). Адресата вычисляет сервер, sessionId указывать не нужно. Отчёт ложится ' +
        'карточкой в его ленту и НЕ запускает там ход — человек и агент увидят его, когда дойдут. ' +
        'Для промежуточных докладов: «нашёл блокер», «нужен доступ», «сделал половину». Итоговый отчёт ' +
        'по завершении задачи слать не нужно — сервер отправит его сам при tasks_complete. ' +
        'blocker: true — ты застрял и работа стоит: постановщику запускается ход СРАЗУ, а человек ' +
        'получает карточку с твоим текстом и кнопками. Ставь только когда реально не можешь продолжать.',
      inputSchema: {
        type: 'object',
        required: ['text'],
        properties: {
          text: { type: 'string', description: 'Текст отчёта: что сделано, что мешает, что нужно' },
          blocker: {
            type: 'boolean',
            description: 'Доклад о блокере: будит постановщика немедленно (по умолчанию false — ' +
              'обычный отчёт, его увидят на следующем ходу)',
          },
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
    {
      name: 'chats_archive',
      description: 'Убрать чат в архив (archived: true) или вернуть из архива (archived: false). ' +
        'Архив ПРЯЧЕТ чат, а не удаляет: история, переписка и настройки целы, возврат — этим же ' +
        'инструментом. Отказ, если в чате идёт ход или работают фоновые агенты. ' +
        'Разбор залежей — это chats_list с includeArchived и chats_archive по каждому чату; ' +
        'для безвозвратного удаления есть отдельный chats_delete, архив его не заменяет.',
      inputSchema: {
        type: 'object',
        required: ['sessionId', 'archived'],
        properties: {
          sessionId: { type: 'string', description: 'ID сессии' },
          archived: { type: 'boolean', description: 'true — убрать в архив, false — вернуть из архива' },
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
  // Секция deploy — пересборка и переопубликация ПРОДА из чата (ADR-010). Монтируется только
  // на машине с настроенным контуром и только владельцу-админу; и то, и другое постоянно
  // в рамках сессии, поэтому состав tools/list не мерцает между ходами.
  // Сервер тут тонкий: вся валидация, guard'ы (грязное дерево, «один деплой за раз») и права
  // живут на бэкенде, инструменты только передают параметры и излагают ответ.
  deploy: [
    {
      name: 'deploy_start',
      description: 'Запустить выкатку прода: собрать фронт и бэк и переопубликовать сервер. ' +
        'ВАЖНО: выкатка ПЕРЕЗАПУСКАЕТ сервер — этот чат и все идущие ходы оборвутся на несколько ' +
        'секунд, а итог придёт отдельным сообщением уже от нового процесса. Зови ТОЛЬКО по явной ' +
        'просьбе пользователя выкатить или опубликовать прод, никогда по своей инициативе и никогда ' +
        'после правок кода «на всякий случай». Собирает и переключает внешний агент: ответ приходит ' +
        'сразу и означает приём заявки, а не результат выкатки.',
      inputSchema: {
        type: 'object',
        properties: {
          ref: { type: 'string', description: 'Ветка, на которой СЕЙЧАС стоит рабочее дерево — как подтверждение того, что едет именно она (пусто — без проверки). Переключение веток не поддерживается: агент собирает рабочее дерево как есть, чужой ref = отказ' },
          skipFrontend: { type: 'boolean', description: 'Не пересобирать фронт (только бэк)' },
          skipSandbox: { type: 'boolean', description: 'Не пересобирать образ песочницы' },
          allowDirty: { type: 'boolean', description: 'Ехать с незакоммиченными изменениями в рабочем дереве. По умолчанию такая выкатка отклоняется — ставь только после явного согласия пользователя' },
        },
      },
    },
    {
      name: 'deploy_status',
      description: 'Ход текущей выкатки прода, история прошлых и список снимков релизов, на которые ' +
        'можно откатиться. Ничего не запускает и сервер не трогает — безопасно звать в любой момент, ' +
        'в том числе чтобы узнать, чем закончилась прошлая выкатка.',
      inputSchema: { type: 'object', properties: {} },
    },
    {
      name: 'deploy_rollback',
      description: 'Вернуть прод на прошлый снимок релиза (по умолчанию предыдущий). ВАЖНО: как и ' +
        'выкатка, ПЕРЕЗАПУСКАЕТ сервер — этот чат и идущие ходы оборвутся. Данные (чаты, задачи, ' +
        'заметки) при этом НЕ откатываются, возвращается только код. Зови ТОЛЬКО по явной просьбе ' +
        'пользователя откатить прод. Список доступных снимков — deploy_status.',
      inputSchema: {
        type: 'object',
        properties: {
          releaseId: { type: 'string', description: 'ID снимка релиза из deploy_status (пусто — предыдущий)' },
        },
      },
    },
  ],
};

// Инструменты, живущие при ЛЮБОМ составе секций: они относятся к самому чату, а не к
// доступу в чужие проекты, и делить их по секциям (projects/files/knowledge/…) не за что.
// Гейт у каждого свой и решается на СТАРТЕ сессии (флаг владельца), а не по ходу: состав
// tools/list обязан быть постоянным — от содержимого контекста он не зависит вовсе,
// иначе добавление материала мид-сессию перезапускало бы CLI со всеми MCP-серверами.
const BASE_TOOLS = [
  ...(CHAT_CONTEXT ? [{
    name: 'context_list',
    description: 'Материалы, закреплённые пользователем за ЭТИМ чатом («контекст чата»: файлы, ' +
      'ссылки, задачи — то, что видно вкладками справа). Это ручной выбор человека, а не история ' +
      'чата: зови в начале работы и когда речь заходит «про этот файл/задачу», чтобы понять, о чём ' +
      'именно речь. Возвращает projectId чата и список записей с подсказкой, чем каждую раскрыть.',
    inputSchema: { type: 'object', properties: {} },
  }] : []),
];

const TOOLS = [
  ...BASE_TOOLS,
  ...Object.entries(SECTION_TOOLS)
    .filter(([section]) => SECTIONS.has(section))
    .flatMap(([, tools]) => tools),
].filter(t => WRITE || !WRITE_TOOLS.has(t.name));

// --- Реализация инструментов ---

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

// Тул → секция (для defense-in-depth в callTool)
const TOOL_SECTION = new Map(
  Object.entries(SECTION_TOOLS).flatMap(([section, tools]) => tools.map(t => [t.name, section])));

// Инструмент из BASE_TOOLS → его собственный гейт (секции у них нет): defense-in-depth
// того же рода, что проверка секции ниже — вызов выключённого инструмента не должен
// исполниться даже при ошибке экспозиции.
const BASE_TOOL_GATES = new Map([
  ['context_list', () => CHAT_CONTEXT],
]);

async function callTool(name, args) {
  // Секция тула обязана быть включённой: список TOOLS фильтрует экспозицию, но исполнение
  // перепроверяем отдельно — деструктив не должен выполниться даже при ошибке экспозиции
  const section = TOOL_SECTION.get(name);
  if (section && !SECTIONS.has(section))
    throw new Error(`Инструмент ${name} недоступен: секция ${section} выключена для этой сессии`);
  if (BASE_TOOL_GATES.get(name)?.() === false)
    throw new Error(`Инструмент ${name} недоступен: фича выключена у пользователя`);
  if (!WRITE && WRITE_TOOLS.has(name))
    throw new Error(`Инструмент ${name} недоступен в этом ходе (только чтение). Попроси пользователя явно сформулировать запрос на запись/создание — тогда инструмент появится.`);
  switch (name) {
    // Контекст ЭТОГО чата: сессию определяет бэкенд по заголовку X-Caller-Session-Id
    // (его ставит общий api()), параметра нет намеренно — иначе модель спросила бы состав
    // чужого чата. Признак «не найден» тоже считает бэкенд (одна точка с полосой вкладок).
    case 'context_list': {
      const data = await api('/api/mcp/session-context');
      const projectId = data?.projectId ?? null;
      const all = Array.isArray(data?.entries) ? data.entries : [];
      // Битый адрес в состав не отдаём: модель пошла бы его читать и получила отказ.
      // Вместо записи — строка-предупреждение: материал человек добавлял осознанно,
      // и молчать о том, что он отвалился, нельзя.
      const warnings = all.filter(e => e.missing).map(e =>
        `не найден: ${e.type} «${e.title || e.id}» (${e.id}) — `
        + (e.type === 'task'
          ? 'задача не резолвится в этом проекте. '
          : 'файла нет по этому пути. ')
        + 'Скажи пользователю: запись в контексте чата устарела.');
      const entries = all.filter(e => !e.missing).map(e => ({
        type: e.type,
        id: e.id,
        title: e.title ?? null,
        open: e.type === 'file'
          ? (SECTIONS.has('files') && projectId
            ? `files_read(projectId: "${projectId}", path: "${e.id}")`
            : 'встроенным Read — путь указан от корня проекта')
          : e.type === 'task'
            ? 'mcp__tasks__tasks_get(id) — если сервер задач доступен в этом чате'
            : 'открой у себя, если доступен WebFetch',
      }));
      return json({
        projectId,
        entries,
        ...(warnings.length ? { warnings } : {}),
        note: entries.length
          ? 'Материалы выбрал сам пользователь — это то, о чём он говорит «этот файл», '
            + '«эта задача». Раскрывай их по мере надобности инструментом из поля open.'
          : 'К этому чату материалы не приложены.',
      });
    }

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
      // Читаем кусками всегда: без потолка полная выдача большого файла оседала в контексте
      // до конца сессии. Явный limit сильнее потолка — модель попросила осознанно.
      const lines = (data.content ?? '').split('\n');
      const start = Math.max(0, args.offset ?? 0);
      // limit: 0 (модель «обнулила» вместо «не указывать») трактуем как «не задан»: иначе
      // выдача пуста, а nextOffset равен offset — модель ходит по кругу
      const limit = args.limit > 0 ? args.limit : READ_MAX_LINES;
      const slice = lines.slice(start, start + limit);
      const nextOffset = start + slice.length;
      const truncated = nextOffset < lines.length;
      return json({
        path: args.path,
        offsetLines: start,
        totalLines: lines.length,
        content: slice.join('\n'),
        ...(truncated
          ? { truncated: true, nextOffset, note: `Показаны строки ${start}–${nextOffset - 1} из ${lines.length} — продолжение через offset: ${nextOffset}` }
          : {}),
      });
    }

    case 'files_document_read': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ path: String(args.path ?? '') });
      const r = await api(`/api/projects/${args.projectId}/files/document/convert?${params}`, { method: 'POST' });
      return json({ path: args.path, markdown: r.markdown });
    }

    case 'files_document_summary': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ path: String(args.path ?? '') });
      const r = await api(`/api/projects/${args.projectId}/files/document/summary?${params}`,
        { method: 'POST', timeoutMs: LLM_TIMEOUT_MS });
      return json({ path: args.path, summary: r.summary });
    }

    case 'files_document_extract': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ path: String(args.path ?? '') });
      const r = await api(`/api/projects/${args.projectId}/files/document/extract?${params}`,
        { method: 'POST', timeoutMs: LLM_TIMEOUT_MS });
      return json(r);
    }

    case 'files_to_markdown': {
      checkProjectAllowed(args.projectId);
      const body = { path: String(args.path ?? ''), targetDir: args.targetDir ?? null, enhance: Boolean(args.enhance) };
      const r = await api(`/api/projects/${args.projectId}/files/document/to-markdown`, {
        method: 'POST', body: JSON.stringify(body), timeoutMs: LLM_TIMEOUT_MS,
      });
      return json({ savedPath: r.savedPath, note: `Файл трансформирован в Markdown → ${r.savedPath}` });
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
      // Загрузка документа в Dify — сетевой поход к внешнему сервису, дефолта может не хватить
      const data = await api(`/api/projects/${args.projectId}/knowledge/index`, {
        method: 'POST', body: JSON.stringify({ relativePath: String(args.path ?? '') }),
        timeoutMs: LLM_TIMEOUT_MS,
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
      // Архив по умолчанию спрятан — как в интерфейсе; признак производный, считает сервер
      const includeArchived = args.includeArchived === true;
      return json(sessions
        .filter(s => includeArchived || !s.isArchived)
        .map(s => ({
          id: s.id,
          name: s.name ?? null,
          status: s.status,
          personaId: s.personaId ?? null,
          model: s.model ?? null,
          updatedAt: s.updatedAt,
          isArchived: s.isArchived === true,
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

    case 'chats_archive': {
      const sessionId = String(args.sessionId ?? '');
      if (!sessionId) throw new Error('Не указан sessionId');
      // Направление обязано быть явным: пропущенный ключ трактовать как «вернуть» нельзя
      if (typeof args.archived !== 'boolean')
        throw new Error('Не указан archived: true — убрать в архив, false — вернуть');
      // Зона сессии: маршрут адресуется по sessionId в обход projectId (как chats_update)
      const projectId = await resolveSessionProject(sessionId);
      if (projectId) checkProjectAllowed(projectId);
      // Один эндпоинт на оба направления и на оба типа чатов (проектный резолвится внутри)
      const updated = await api(`/api/chats/${encodeURIComponent(sessionId)}/archived`, {
        method: 'PUT', body: JSON.stringify({ archived: args.archived }),
      });
      return json({ id: updated.id, isArchived: updated.isArchived === true });
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
          // Глубину делегирования для целевого чата считает бэкенд — по НАШЕЙ сессии
          // (заголовок ниже): из env она протухала при переиспользовании живого прогона.
          // Своя сессия — получатель по её PersonaId отрисует входящую реплику лицом персоны
          ...(SELF_SESSION_ID ? {
            'X-Sender-Session-Id': SELF_SESSION_ID,
            'X-Caller-Session-Id': SELF_SESSION_ID,
          } : {}),
        },
        body: JSON.stringify({
          text,
          wait: args.wait ?? 'turn',
          ...(args.timeoutSec ? { timeoutSec: args.timeoutSec } : {}),
        }),
      });
      const body = await res.json().catch(() => null);
      // 200 completed / 202 running|queued / 409 busy / 429 queue_full — отдаём модели как
      // результат: в теле есть status и hint, решение (ждать/не ретраить) принимает она
      if (res.ok || res.status === 202 || res.status === 409 || res.status === 429)
        return json(body ?? { status: res.status });
      if (res.status === 404) throw new Error(`Сессия ${sessionId} не найдена`);
      throw new Error(`HTTP ${res.status}: ${body ? JSON.stringify(body) : ''}`);
    }

    case 'chats_report_up': {
      const text = String(args.text ?? '').trim();
      if (!text) throw new Error('Пустой текст отчёта');
      if (!SELF_SESSION_ID) throw new Error('Отчёт наверх недоступен: сессия не определена');
      // Адресат — родительский чат, его вычисляет сервер по текущей сессии.
      // Идём через общий api(): ретраи по сети, таймаут и X-Caller-Session-Id как у всех.
      try {
        return json(await api(`/api/sessions/${encodeURIComponent(SELF_SESSION_ID)}/report`, {
          method: 'POST',
          // blocker — доклад «я застрял»: сервер будит постановщика ходом, а в режиме
          // «Командная реализация» ещё и показывает человеку карточку остановки
          body: JSON.stringify({ text, blocker: args.blocker === true }),
        }));
      } catch (err) {
        // 400 — не сбой вызова, а ответ по существу: «отчитываться некуда» либо «цепочка
        // слишком длинная». Отдаём модели телом со status/hint, чтобы она не ретраила.
        if (err?.status === 400 && err.bodyText) {
          try { return json(JSON.parse(err.bodyText)); } catch { /* не JSON — падаем ниже */ }
        }
        throw err;
      }
    }

    case 'git_status': {
      checkProjectAllowed(args.projectId);
      return json(await api(`/api/projects/${args.projectId}/git/status`));
    }

    case 'git_diff': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams({ path: String(args.path ?? '') });
      if (args.staged) params.set('staged', 'true');
      // Ответ — { diff } (строка унифицированного diff)
      return json(await api(`/api/projects/${args.projectId}/git/diff?${params}`));
    }

    case 'git_log': {
      checkProjectAllowed(args.projectId);
      const params = new URLSearchParams();
      if (args.limit) params.set('limit', String(args.limit));
      if (args.branch) params.set('branch', String(args.branch));
      return json(await api(`/api/projects/${args.projectId}/git/log?${params}`));
    }

    case 'git_commit': {
      checkProjectAllowed(args.projectId);
      const message = String(args.message ?? '').trim();
      if (!message) throw new Error('Пустое сообщение коммита');
      // Тело — GitCommitRequest(Message, Amend); ответ — { sha }
      return json(await api(`/api/projects/${args.projectId}/git/commit`, {
        method: 'POST', body: JSON.stringify({ message }),
      }));
    }

    case 'git_stage': {
      checkProjectAllowed(args.projectId);
      // Тело — GitPathRequest(Path); ответ — свежий git-статус
      const status = await api(`/api/projects/${args.projectId}/git/stage`, {
        method: 'POST', body: JSON.stringify({ path: String(args.path ?? '') }),
      });
      return json(status);
    }

    case 'kb_list': {
      // Базы знаний владельца — уровень пользователя, projectId не участвует
      return json(await api('/api/knowledge'));
    }

    case 'kb_get': {
      const id = String(args.id ?? '');
      if (!id) throw new Error('Не указан id базы знаний');
      return json(await api(`/api/knowledge/${encodeURIComponent(id)}`));
    }

    case 'kb_search': {
      const id = String(args.id ?? '');
      if (!id) throw new Error('Не указан id базы знаний');
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      if (args.topK) params.set('topK', String(args.topK));
      if (args.method) params.set('method', String(args.method));
      return json(await api(`/api/knowledge/${encodeURIComponent(id)}/search?${params}`));
    }

    case 'kb_add_document': {
      const id = String(args.id ?? '');
      if (!id) throw new Error('Не указан id базы знаний');
      const name = String(args.name ?? '').trim();
      if (!name) throw new Error('Не задано имя документа');
      const text = String(args.text ?? '');
      if (!text) throw new Error('Пустой текст документа');
      // Тело — AddDocumentTextRequest(Name, Text); ответ — { id, name, indexingStatus }
      return json(await api(`/api/knowledge/${encodeURIComponent(id)}/documents`, {
        method: 'POST', body: JSON.stringify({ name, text }),
      }));
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

    case 'tags_apply': {
      const entityType = String(args.entityType ?? '').trim();
      if (entityType !== 'session' && entityType !== 'task')
        throw new Error('entityType должен быть "session" или "task"');
      const entityId = String(args.entityId ?? '').trim();
      if (!entityId) throw new Error('Не указан entityId');
      const incoming = Array.isArray(args.tags)
        ? args.tags.map(t => String(t ?? '').trim()).filter(Boolean)
        : [];
      if (!incoming.length) throw new Error('Список tags пуст');
      const projectId = args.projectId ? String(args.projectId).trim() : null;

      // session: теги живут на проектной сессии — projectId нужен и для маршрута, и для реестра
      if (entityType === 'session') {
        if (!projectId) throw new Error('Для session обязателен projectId');
        checkProjectAllowed(projectId);
        const registryAdded = await ensureRegistryTags(projectId, incoming);
        const sessions = await api(`/api/projects/${projectId}/sessions`);
        const found = (sessions ?? []).find(s => s.id === entityId);
        if (!found) throw new Error(`Сессия ${entityId} не найдена в проекте ${projectId}`);
        const merged = unionStrings(found.tags, incoming);
        const updated = await api(
          `/api/projects/${projectId}/sessions/${encodeURIComponent(entityId)}`,
          { method: 'PUT', body: JSON.stringify({ tags: merged }) },
        );
        return json({
          entityType: 'session',
          id: updated?.id ?? entityId,
          projectId,
          tags: updated?.tags ?? merged,
          ...(registryAdded.length ? { registryAdded } : {}),
        });
      }

      // task: projectId опционален — только для автосоздания в реестре. Сама задача
      // принадлежит владельцу по токену (бэкенд проверит), личные задачи (без проекта) валидны.
      let registryAdded = [];
      if (projectId) {
        checkProjectAllowed(projectId);
        registryAdded = await ensureRegistryTags(projectId, incoming);
      }
      const task = await api(`/api/tasks/${encodeURIComponent(entityId)}`);
      const mergedLabels = unionStrings(task?.labels, incoming);
      await api(`/api/tasks/${encodeURIComponent(entityId)}`, {
        method: 'PUT', body: JSON.stringify({ labels: mergedLabels }),
      });
      return json({
        entityType: 'task',
        id: entityId,
        projectId: projectId ?? task?.projectId ?? null,
        labels: mergedLabels,
        ...(registryAdded.length ? { registryAdded } : {}),
      });
    }

    case 'tags_remove': {
      const entityType = String(args.entityType ?? '').trim();
      if (entityType !== 'session' && entityType !== 'task')
        throw new Error('entityType должен быть "session" или "task"');
      const entityId = String(args.entityId ?? '').trim();
      if (!entityId) throw new Error('Не указан entityId');
      const removing = Array.isArray(args.tags)
        ? args.tags.map(t => String(t ?? '').trim()).filter(Boolean)
        : [];
      if (!removing.length) throw new Error('Список tags пуст');
      const projectId = args.projectId ? String(args.projectId).trim() : null;

      // session: теги живут на проектной сессии — projectId нужен для маршрута.
      // Реестр проекта НЕ чистим: это словарь доступных имён, а не «висящие» теги.
      if (entityType === 'session') {
        if (!projectId) throw new Error('Для session обязателен projectId');
        checkProjectAllowed(projectId);
        const sessions = await api(`/api/projects/${projectId}/sessions`);
        const found = (sessions ?? []).find(s => s.id === entityId);
        if (!found) throw new Error(`Сессия ${entityId} не найдена в проекте ${projectId}`);
        const { kept, removed } = subtractStrings(found.tags, removing);
        const updated = await api(
          `/api/projects/${projectId}/sessions/${encodeURIComponent(entityId)}`,
          { method: 'PUT', body: JSON.stringify({ tags: kept }) },
        );
        return json({
          entityType: 'session',
          id: updated?.id ?? entityId,
          projectId,
          tags: updated?.tags ?? kept,
          removed,
        });
      }

      // task: projectId фактически не нужен (реестр не трогаем), при переданном —
      // проверяем зону, чтобы параметр не был тихим обходом проверок
      if (projectId) checkProjectAllowed(projectId);
      const task = await api(`/api/tasks/${encodeURIComponent(entityId)}`);
      const { kept, removed } = subtractStrings(task?.labels, removing);
      await api(`/api/tasks/${encodeURIComponent(entityId)}`, {
        method: 'PUT', body: JSON.stringify({ labels: kept }),
      });
      return json({
        entityType: 'task',
        id: entityId,
        projectId: projectId ?? task?.projectId ?? null,
        labels: kept,
        removed,
      });
    }

    // --- Выкатка прода (ADR-010) ---
    // Обёртки тонкие: параметры уходят в REST как есть, решения принимает бэкенд.
    // Наша единственная задача сверх этого — изложить ответ так, чтобы отказ не выглядел
    // успехом: коды 409/400/503 здесь содержательны и требуют разных действий человека.

    case 'deploy_start': {
      const res = await deployCall('/api/deploy/start', {
        ref: args.ref ? String(args.ref) : null,
        skipFrontend: args.skipFrontend === true,
        skipSandbox: args.skipSandbox === true,
        allowDirty: args.allowDirty === true,
      }, 'Выкатка');
      const active = Number(res?.activeTurns ?? 0);
      return json({
        accepted: true,
        deployId: res?.deployId ?? null,
        activeTurns: active,
        warning: 'Выкатка принята и пойдёт своим ходом. Сервер будет перезапущен: этот чат '
          + `прервётся, а вместе с ним оборвутся идущие сейчас ходы (${active}). Скажи об этом пользователю.`,
        next: 'Итог выкатки придёт отдельным сообщением от уже нового процесса. '
          + 'Ход выкатки виден через deploy_status, пока сервер жив.',
      });
    }

    case 'deploy_rollback': {
      const res = await deployCall('/api/deploy/rollback', {
        releaseId: args.releaseId ? String(args.releaseId) : null,
      }, 'Откат');
      const active = Number(res?.activeTurns ?? 0);
      return json({
        accepted: true,
        deployId: res?.deployId ?? null,
        activeTurns: active,
        warning: 'Откат принят. Сервер будет перезапущен: этот чат прервётся, вместе с ним '
          + `оборвутся идущие сейчас ходы (${active}). Данные не откатываются — возвращается только код.`,
        next: 'Итог придёт отдельным сообщением от нового процесса.',
      });
    }

    case 'deploy_status': {
      const state = await api('/api/deploy/status');
      if (state?.enabled === false)
        return json({
          enabled: false,
          note: 'Механизм выкатки на этой машине не настроен (секция Deploy выключена). '
            + 'deploy_start и deploy_rollback здесь работать не будут — это настройка администратора, а не сбой.',
        });
      const brief = rec => rec && {
        deployId: rec.id,
        kind: rec.kind,
        phase: rec.phase,
        ref: rec.ref ?? null,
        sha: rec.sha ?? null,
        dirty: rec.dirty === true || undefined,
        steps: (rec.steps ?? []).map(s => `${s.name}: ${s.status}`),
        result: rec.result
          ? { ok: rec.result.ok === true, status: rec.result.status, message: rec.result.message ?? null }
          : null,
        reported: rec.reported === true || undefined,
      };
      return json({
        enabled: true,
        current: brief(state?.current) ?? null,
        running: state?.current ? !state.current.result : false,
        history: (state?.history ?? []).slice(0, 5).map(brief),
        releases: (state?.releases ?? []).slice(0, 10)
          .map(r => ({ releaseId: r.id, sha: r.sha ?? null, createdAt: r.createdAt ?? null })),
      });
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

// Обработка ОДНОГО JSON-RPC сообщения. Вынесена из обработчика строки, чтобы поддержать
// batch (массив сообщений): раньше массив уходил в деструктуризацию, id оказывался undefined,
// и запрос молча терялся — ответа клиент не получал вовсе и ждал до своего таймаута.
async function handleMessage(msg) {
  // null — валидный JSON: деструктуризация его роняла процесс (unhandled rejection),
  // а вместе с процессом исчезали ВСЕ инструменты сервера («Stream closed»)
  if (msg === null || typeof msg !== 'object') return;

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
          reply(id, await callCtx.run({ tool: params.name },
            () => callTool(params.name, params.arguments ?? {})));
        } catch (err) {
          // Ошибка инструмента — валидный результат с isError, не protocol error
          reply(id, {
            content: [{ type: 'text', text: `Ошибка: ${describeError(err)}` }],
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
}

rl.on('line', async line => {
  line = line.trim();
  if (!line) return;
  let msg;
  try { msg = JSON.parse(line); }
  catch (err) {
    // Раньше битая строка проглатывалась молча — сбой было невозможно связать с причиной
    console.error(`[mcp] пропущена нечитаемая строка stdin: ${err?.message ?? err}`);
    return;
  }
  // Batch: по спецификации это массив сообщений (мы эхом подтверждаем protocolVersion
  // клиента, включая версии, где batch разрешён) — обрабатываем каждое.
  if (Array.isArray(msg)) { for (const one of msg) await handleMessage(one); return; }
  await handleMessage(msg);
});
