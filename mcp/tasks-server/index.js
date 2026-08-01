// MCP-сервер задач ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   TASKS_API_URL    — базовый URL бэкенда (http://127.0.0.1:5000)
//   TASKS_API_TOKEN  — сервисный JWT владельца сессии
//   TASKS_PROJECT_ID — id проекта сессии; пусто = контекст личных задач
//   TASKS_EXECUTE    — "0" = аварийно убрать tasks_run_executor (прямые запуски сервера).
//                      В продукте инструмент подключён всегда: можно ли запускать исполнителя
//                      на конкретном ходу, решает бэкенд ([DenyOnDelegatedTurn])
//   TASKS_SESSION_ID — id чата-источника: проставляется в sourceSessionId создаваемых задач
//   TASKS_SELF_PERSONA_ID — персона текущего чата: постановщик (createdByPersonaId) создаваемых задач
//   TASKS_EXTRA_PROJECT_IDS          — CSV id проектов из кросс-проектных привязок ProjectTasks
//                                      текущей персоны: их задачи доступны в дополнение к TASKS_PROJECT_ID
//   TASKS_EXTRA_PROJECT_IDS_READONLY — CSV подмножество TASKS_EXTRA_PROJECT_IDS только для чтения
//                                      (create/update/delete там запрещены)

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


const API_URL = (process.env.TASKS_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.TASKS_API_TOKEN ?? '';
const PROJECT_ID = process.env.TASKS_PROJECT_ID || null;
// tasks_run_executor подключён ВСЕГДА (состав инструментов не зависит от хода): можно ли
// запускать исполнителя ИМЕННО СЕЙЧАС, решает бэкенд по идущему ходу вызывающей сессии
// ([DenyOnDelegatedTurn] на /api/tasks/{id}/execute). Переменная осталась аварийным
// рубильником для прямых запусков сервера и выключается только явным "0" — при пустом env
// инструмент обязан быть на месте, иначе состав «мерцает» и процесс CLI перезапускается.
const EXECUTE_ENABLED = process.env.TASKS_EXECUTE !== '0';
const SESSION_ID = process.env.TASKS_SESSION_ID || null;
const SELF_PERSONA_ID = process.env.TASKS_SELF_PERSONA_ID || null;
const EXTRA_PROJECT_IDS = new Set((process.env.TASKS_EXTRA_PROJECT_IDS || '').split(',').map(s => s.trim()).filter(Boolean));
const EXTRA_PROJECT_IDS_READONLY = new Set((process.env.TASKS_EXTRA_PROJECT_IDS_READONLY || '').split(',').map(s => s.trim()).filter(Boolean));

// Все проекты, чьи задачи видны в этом ходу (текущий + кросс-проектные привязки)
function allowedProjectIds() {
  const set = new Set(EXTRA_PROJECT_IDS);
  if (PROJECT_ID) set.add(PROJECT_ID);
  return set;
}

// Проект доступен для ЧТЕНИЯ (текущий или любая ProjectTasks-привязка, включая readonly).
// PROJECT_ID пуст (личный/глобальный контекст) — сужения нет вовсе (как у tasks_list scope=all):
// владение всё равно перепроверяет бэкенд, а тут это уже весь воркспейс владельца по дизайну.
function assertProjectReadable(projectId) {
  if (!PROJECT_ID || projectId === PROJECT_ID) return;
  if (!EXTRA_PROJECT_IDS.has(projectId))
    throw new Error(`Нет доступа к проекту ${projectId} — нужна привязка ProjectTasks персоне (см. tasks_list_projects).`);
}

// Проект доступен для ЗАПИСИ (текущий или полная — не readonly — привязка ProjectTasks)
function assertProjectWritable(projectId) {
  if (!PROJECT_ID) return;
  assertProjectReadable(projectId);
  if (projectId !== PROJECT_ID && EXTRA_PROJECT_IDS_READONLY.has(projectId))
    throw new Error(`Доступ к проекту ${projectId} только для чтения (привязка ProjectTasks с readonly) — создавать/менять задачи нельзя.`);
}

// Задача принадлежит проекту, видимому в этом ходу (проверка при непустом PROJECT_ID —
// закрывает дыру, когда tasks_get/tasks_list(scope=all)/tasks_search по чужому проекту
// возвращали задачу любого проекта владельца безотносительно контекста чата)
function assertTaskAccessible(task) {
  if (!PROJECT_ID) return; // чат вне проекта / глобальная персона — весь воркспейс, как и раньше
  // ЛИЧНАЯ задача владельца (без проекта) доступна из любого его чата. Контекст проекта нужен,
  // чтобы не лезть в ЧУЖИЕ проекты, а личные дела пользователя чужими не являются — владение
  // уже проверил бэкенд по токену. Раньше запрет бил и по ним: задача читалась, а tasks_update
  // по ней падал «недоступна в этом контексте» — со стороны это выглядело как отвалившийся
  // инструмент («минуту назад работало»).
  if (!task.projectId) return;
  if (!allowedProjectIds().has(task.projectId))
    throw new Error(`Задача ${task.id} недоступна в этом контексте — она принадлежит другому проекту.`);
}

// Задача видна в scope=all/tasks_search текущего контекста (та же проверка, но фильтрующая,
// не бросающая — используется на списках, где недоступные записи просто отфильтровываются)
function isTaskInScope(task) {
  if (!PROJECT_ID) return true;
  // Личные задачи владельца видны и из проектного чата — иначе scope=all обещал «все задачи
  // пользователя по всем проектам и личные», а отдавал один текущий проект (и tasks_search
  // молча терял личные находки). Дефолтный scope=context остаётся строго проектным.
  if (!task.projectId) return true;
  return allowedProjectIds().has(task.projectId);
}

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
        // ход, и сам решает, что на нём запрещено (запуск исполнителя). Состав инструментов
        // сервера при этом остаётся неизменным — иначе процесс CLI перезапускается.
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

// Компактное представление задачи для ответа модели
function brief(t) {
  return {
    id: t.id,
    title: t.title,
    status: t.status,
    priority: t.priority,
    dueDate: t.dueDate ?? null,
    dueTime: t.dueTime ?? null,
    reminderMinutes: t.reminderMinutes ?? null,
    // Правило повторения показываем, только если оно активно
    recurrence: t.recurrence && t.recurrence.type !== 'none' ? t.recurrence : null,
    assignee: t.assignee ?? null,
    // Исполнитель-персона (id): задача выполняется силами Claude от её лица
    personaId: t.personaId ?? null,
    projectId: t.projectId ?? null,
    columnId: t.columnId ?? null,
    // Дата+время завершения (когда статус стал done); null — не завершена или неизвестно.
    // В режиме списка «Готово» задачи идут сверху вниз от свежих к старым по этому полю.
    completedAt: t.completedAt ?? null,
    labels: t.labels,
    subtasks: `${(t.subtasks ?? []).filter(s => s.isDone).length}/${(t.subtasks ?? []).length}`,
  };
}

// --- Инструменты ---

const CONTEXT_NOTE = PROJECT_ID
  ? 'Контекст — текущий проект.'
  : 'Контекст — личные задачи пользователя (вне проектов).';

const ENUMS = {
  status: ['todo', 'inProgress', 'done'],
  priority: ['urgent', 'high', 'medium', 'low'],
  assignee: ['me', 'claude'],
};

// Дефолтные колонки доски (когда у проекта нет кастомных, а также для личных задач)
const DEFAULT_COLUMNS = [
  { id: 'todo', name: 'К выполнению', category: 'todo' },
  { id: 'inProgress', name: 'В работе', category: 'inProgress' },
  { id: 'done', name: 'Готово', category: 'done' },
];

// Ссылка на справочник сервера (поле instructions ответа initialize): развёрнутые пояснения
// к повторению, уровню модели, персоне-исполнителю и worktree лежат ТАМ — один раз на сервер,
// а не в каждой из схем create/update, где раньше дублировались слово в слово.
const SEE_INSTRUCTIONS = 'см. инструкции сервера tasks';

// Схема columnId: колонка доски проекта. Статус выводится из категории колонки на бэке —
// достаточно указать columnId (список — через tasks_board_columns).
const COLUMN_ID_SCHEMA = {
  type: 'string',
  description: 'ID колонки доски проекта (см. tasks_board_columns); статус выставится по её категории',
};

// Правило повторения задачи (соответствует TaskRecurrence на бэке).
// Требует заданного dueDate: серия ведётся одним экземпляром — при переводе
// текущего в done автоматически создаётся следующий (нужен фич-флаг task-recurrence).
const RECURRENCE_SCHEMA = {
  type: 'object',
  description: `Повторение задачи, требует dueDate (${SEE_INSTRUCTIONS})`,
  required: ['type'],
  properties: {
    type: { type: 'string', enum: ['none', 'daily', 'weekly', 'monthly', 'yearly'] },
    interval: { type: 'integer', minimum: 1, description: 'Каждые N периодов (дефолт 1)' },
    weekdays: { type: 'array', items: { type: 'integer', minimum: 1, maximum: 7 }, description: 'Только для weekly: ISO-дни (1=Пн … 7=Вс)' },
    until: { type: 'string', description: 'Последняя дата серии YYYY-MM-DD; опустить — бессрочно' },
  },
};

const REMINDER_MINUTES_SCHEMA = {
  type: 'integer',
  description: 'Напоминание: за сколько минут до срока уведомить (0 = в момент срока). Требует dueDate.',
};

// Персона-исполнитель: id персоны, которая выполнит задачу от своего лица (с её
// характером, моделью и памятью). Список доступных персон и их id — personas_list
// (MCP personas-server). Назначение персоны автоматически ставит исполнителя Claude.
const PERSONA_ID_SCHEMA = {
  type: 'string',
  description: `ID персоны-исполнителя (см. personas_list); assignee выставится сам, "" — снять`,
};

// Время жизни чата исполнения (актуально только при исполнителе Claude/персона):
// чат исполнения удалится сам вместе с историей, если не будет активности N минут.
// Не указано — дефолт 1440 (сутки).
const EXECUTION_TTL_SCHEMA = {
  type: 'integer',
  description: 'TTL чата исполнения в минутах от последней активности (дефолт 1440)',
};

// Уровень модели исполнителя: под какую работу задача. Резолвится в конкретную модель
// на бэке (слоты «сильная/средняя/слабая» владельца). Не задан — уровень берётся
// от персоны-исполнителя и назначения места «исполнитель задач».
const MODEL_TIER_SCHEMA = {
  type: 'string',
  enum: ['strong', 'medium', 'weak'],
  description: `Уровень модели исполнения (${SEE_INSTRUCTIONS}); сомневаешься — не указывай`,
};

// Отдельное git worktree для чата-исполнителя: дерево должно УЖЕ существовать (тумблер
// worktree в чате или git worktree add). Бэкенд сверяет путь с «git worktree list» репы
// проекта; не сошлось — исполнитель просто стартует в корне проекта.
const WORKTREE_PATH_SCHEMA = {
  type: 'string',
  description: `Абсолютный путь СУЩЕСТВУЮЩЕГО git worktree проекта (${SEE_INSTRUCTIONS}); "" — убрать`,
};

const WORKTREE_BRANCH_SCHEMA = {
  type: 'string',
  description: 'Ветка этого worktree (метка в git-баре чата); пусто — из самого дерева',
};

// Markdown-итог выполнения задачи — прикрепляет исполнитель при завершении/обновлении.
// null/отсутствует = не менять, "" = очистить (как у description).
const RESULT_MARKDOWN_SCHEMA = {
  type: 'string',
  description: 'Markdown-описание итога выполнения (заменяет целиком). "" — очистить.',
};

// Ссылки на файлы проекта — относительные пути от корня проекта через / (напр. "src/index.ts").
// Заменяют список целиком.
const LINKED_FILES_SCHEMA = {
  type: 'array',
  items: { type: 'string' },
  description: 'Пути файлов проекта (от корня проекта, через /). Заменяют список целиком.',
};

// Справочник сервера (поле instructions ответа initialize): клиент кладёт его в контекст один
// раз на сервер, тогда как повторённый в схемах текст оплачивается в каждой из них — правило
// повторения, уровень модели и worktree шли ×2 (tasks_create + tasks_update).
const INSTRUCTIONS = [
  'Справочники сервера «Задачи» (на них ссылаются описания инструментов).',
  '',
  'ПОВТОРЕНИЕ (recurrence): требует dueDate; существует ОДИН экземпляр серии, следующий создаётся',
  '  при завершении текущего. type: daily|weekly|monthly|yearly; "none" — только в tasks_update,',
  '  «убрать повторение». interval — каждые N периодов (2 недели = weekly + interval 2).',
  '',
  'УРОВЕНЬ МОДЕЛИ (modelTier): strong — проектирование, архитектура, ревью, запутанный баг,',
  '  многофайловый код; medium — реализация по плану, тесты, документация; weak — механическая',
  '  правка, переименование, коммит. Не задан — от персоны-исполнителя и настроек системы.',
  '',
  'WORKTREE (worktreePath): дерево должно УЖЕ существовать — бэкенд сверяет путь с «git worktree',
  '  list» проекта, чужой путь игнорируется и исполнитель стартует в корне проекта.',
  '',
  'ОЧИСТКА ПОЛЕЙ в tasks_update: "" в dueDate/dueTime убирает срок, отрицательное значение в',
  '  reminderMinutes убирает напоминание, а в executionExpiresAfterMinutes — делает чат бессрочным.',
].join('\n');

const TOOLS = [
  {
    name: 'tasks_list_projects',
    description: 'Проекты, чьи задачи доступны в этом ходу (текущий плюс кросс-проектные привязки): ' +
      'id, имя, readOnly. По ним выбирай projectId для tasks_create/tasks_list.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'tasks_list',
    description: `Список задач. ${CONTEXT_NOTE} scope=all — все задачи пользователя по всем проектам и личные.`,
    inputSchema: {
      type: 'object',
      properties: {
        status: { type: 'string', enum: ENUMS.status, description: 'Фильтр по статусу' },
        priority: { type: 'string', enum: ENUMS.priority, description: 'Фильтр по приоритету' },
        assignee: { type: 'string', enum: ENUMS.assignee, description: 'Фильтр по исполнителю (me — пользователь, claude — Claude)' },
        from: { type: 'string', description: 'Срок от даты включительно, YYYY-MM-DD (задачи без срока не попадают)' },
        to: { type: 'string', description: 'Срок до даты включительно, YYYY-MM-DD (задачи без срока не попадают)' },
        scope: { type: 'string', enum: ['context', 'all'], description: 'context (дефолт) — текущий проект/личные; all — все задачи пользователя' },
        projectId: { type: 'string', description: 'Явный проект — переопределяет scope, только его задачи' },
      },
    },
  },
  {
    name: 'tasks_search',
    description: 'Поиск задач по названию, описанию и меткам — по всем задачам пользователя (все проекты + личные).',
    inputSchema: {
      type: 'object',
      required: ['query'],
      properties: {
        query: { type: 'string', description: 'Строка поиска' },
      },
    },
  },
  {
    name: 'tasks_get',
    description: 'Полная карточка задачи по id: описание (markdown), подзадачи, метки, срок.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID задачи' } },
    },
  },
  {
    name: 'tasks_create',
    description: `Создать задачу. ${CONTEXT_NOTE}`,
    inputSchema: {
      type: 'object',
      required: ['title'],
      properties: {
        title: { type: 'string', description: 'Название задачи' },
        description: { type: 'string', description: 'Описание (markdown)' },
        priority: { type: 'string', enum: ENUMS.priority, description: 'Приоритет (по умолчанию medium)' },
        dueDate: { type: 'string', description: 'Срок YYYY-MM-DD' },
        dueTime: { type: 'string', description: 'Время HH:MM' },
        reminderMinutes: REMINDER_MINUTES_SCHEMA,
        recurrence: RECURRENCE_SCHEMA,
        assignee: { type: 'string', enum: ENUMS.assignee, description: 'Исполнитель' },
        personaId: PERSONA_ID_SCHEMA,
        modelTier: MODEL_TIER_SCHEMA,
        worktreePath: WORKTREE_PATH_SCHEMA,
        worktreeBranch: WORKTREE_BRANCH_SCHEMA,
        subtasks: { type: 'array', items: { type: 'string' }, description: 'Названия подзадач' },
        labels: { type: 'array', items: { type: 'string' }, description: 'Метки' },
        columnId: COLUMN_ID_SCHEMA,
        executionExpiresAfterMinutes: EXECUTION_TTL_SCHEMA,
        projectId: { type: 'string', description: 'Проект задачи, если не текущий (см. tasks_list_projects; нужен полный доступ)' },
      },
    },
  },
  {
    name: 'tasks_update',
    description: 'Обновить поля задачи: поля как при создании, передавай только изменяемые. ' +
      `Очистка значений и сброс повторения — ${SEE_INSTRUCTIONS}.`,
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID задачи' },
        title: { type: 'string' },
        description: { type: 'string', description: 'Описание (markdown), заменяет целиком' },
        status: { type: 'string', enum: ENUMS.status },
        priority: { type: 'string', enum: ENUMS.priority },
        dueDate: { type: 'string', description: 'YYYY-MM-DD или "" чтобы убрать срок' },
        dueTime: { type: 'string', description: 'HH:MM или "" чтобы убрать время' },
        reminderMinutes: REMINDER_MINUTES_SCHEMA,
        recurrence: RECURRENCE_SCHEMA,
        assignee: { type: 'string', enum: ENUMS.assignee },
        personaId: PERSONA_ID_SCHEMA,
        modelTier: MODEL_TIER_SCHEMA,
        worktreePath: WORKTREE_PATH_SCHEMA,
        worktreeBranch: WORKTREE_BRANCH_SCHEMA,
        resultMarkdown: RESULT_MARKDOWN_SCHEMA,
        linkedFiles: LINKED_FILES_SCHEMA,
        labels: { type: 'array', items: { type: 'string' }, description: 'Метки (заменяют список целиком)' },
        columnId: { ...COLUMN_ID_SCHEMA, description: COLUMN_ID_SCHEMA.description + '; "" — сброс на дефолтную' },
        executionExpiresAfterMinutes: EXECUTION_TTL_SCHEMA,
        projectId: { type: 'string', description: 'Перенести в другой проект (см. tasks_list_projects) или "" — сделать личной' },
      },
    },
  },
  {
    name: 'tasks_board_columns',
    description: `Колонки Kanban-доски ${PROJECT_ID ? 'текущего проекта' : '(личные задачи используют дефолтные)'}: id, name, category (todo/inProgress/done). Нужен, чтобы задать columnId в tasks_create/tasks_update.`,
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'tasks_complete',
    description: 'Пометить задачу выполненной (status → done) и сразу прикрепить итог: ' +
      'resultMarkdown (что сделано) и linkedFiles (итоговые файлы). Это ТОЛЬКО смена статуса — ' +
      'исполнителя запускает tasks_run_executor.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: {
        id: { type: 'string', description: 'ID задачи' },
        resultMarkdown: { ...RESULT_MARKDOWN_SCHEMA, description: 'Короткий итог сделанного (markdown)' },
        linkedFiles: { ...LINKED_FILES_SCHEMA, description: 'Итоговые файлы проекта (пути от корня, через /)' },
      },
    },
  },
  {
    name: 'tasks_delete',
    description: 'Удалить задачу безвозвратно.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID задачи' } },
    },
  },
  {
    name: 'tasks_add_subtask',
    description: 'Добавить подзадачу к задаче.',
    inputSchema: {
      type: 'object',
      required: ['taskId', 'title'],
      properties: {
        taskId: { type: 'string', description: 'ID родительской задачи' },
        title: { type: 'string', description: 'Название подзадачи' },
      },
    },
  },
  {
    name: 'tasks_toggle_subtask',
    description: 'Отметить подзадачу выполненной или снять отметку. Подзадачу можно указать по id или точному названию.',
    inputSchema: {
      type: 'object',
      required: ['taskId', 'isDone'],
      properties: {
        taskId: { type: 'string', description: 'ID задачи' },
        subtaskId: { type: 'string', description: 'ID подзадачи' },
        subtaskTitle: { type: 'string', description: 'Точное название подзадачи (если id неизвестен)' },
        isDone: { type: 'boolean', description: 'true — выполнена' },
      },
    },
  },
  {
    name: 'tasks_suggest_meta',
    description: 'Предложить приоритет (low/medium/high/urgent) и до 3 меток по названию и описанию задачи. ' +
      'Считает бесплатная локальная модель (если настроена), иначе Claude. Ничего не сохраняет — только предложение.',
    inputSchema: {
      type: 'object',
      required: ['title'],
      properties: {
        title: { type: 'string', description: 'Название задачи' },
        description: { type: 'string', description: 'Описание задачи (опционально)' },
      },
    },
  },
  {
    name: 'tasks_normalize_title',
    description: 'Привести заголовок задачи к аккуратному виду (повелительное наклонение, чистка голосового ввода), ' +
      'вынести упомянутый срок в dueHint. Возвращает {title, dueHint}. Бесплатная локальная модель, если настроена.',
    inputSchema: {
      type: 'object',
      required: ['title'],
      properties: { title: { type: 'string', description: 'Сырой заголовок (напр. из голосового ввода)' } },
    },
  },
  {
    name: 'tasks_find_duplicate',
    description: 'Проверить, дублирует ли новая задача одну из существующих задач владельца (предотбор по ключевым словам + ' +
      'модель). Возвращает {duplicateId, reason} или duplicateId=null. Полезно перед tasks_create, чтобы не плодить дубли.',
    inputSchema: {
      type: 'object',
      required: ['title'],
      properties: {
        title: { type: 'string', description: 'Название новой задачи' },
        description: { type: 'string', description: 'Описание (опционально)' },
      },
    },
  },
];

// tasks_run_executor — только на пользовательском ходу (env выставляет ClaudeSession):
// исполнитель порождает новую сессию Claude, на агентных ходах не даётся (анти-рекурсия)
if (EXECUTE_ENABLED) {
  TOOLS.push({
    name: 'tasks_run_executor',
    description: 'Запустить Claude-исполнителя задачи: отдельная сессия в проекте задачи ' +
      '(личная — чат вне проекта), работает в фоне и сама ведёт статус через tasks_*. ' +
      'Возвращает задачу с id сессии-исполнителя. ' +
      'НЕ отмечает задачу выполненной — для смены статуса на done используй tasks_complete.',
    inputSchema: {
      type: 'object',
      required: ['taskId'],
      properties: { taskId: { type: 'string', description: 'ID задачи' } },
    },
  });
}

// --- Реализация инструментов ---

function json(data) {
  return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
}

async function callTool(name, args) {
  switch (name) {
    case 'tasks_list_projects': {
      const entries = [];
      if (PROJECT_ID) entries.push({ id: PROJECT_ID, readOnly: false });
      for (const id of EXTRA_PROJECT_IDS) entries.push({ id, readOnly: EXTRA_PROJECT_IDS_READONLY.has(id) });
      const result = [];
      for (const e of entries) {
        try {
          const proj = await api(`/api/projects/${encodeURIComponent(e.id)}`);
          result.push({ id: e.id, name: proj.name, readOnly: e.readOnly, current: e.id === PROJECT_ID });
        } catch { /* проект удалён/недоступен — пропускаем */ }
      }
      return json(result);
    }

    case 'tasks_list': {
      const params = new URLSearchParams();
      // from/to — фильтр по диапазону срока (DueDate) на бэке, границы включительно
      for (const k of ['status', 'priority', 'assignee', 'from', 'to'])
        if (args[k]) params.set(k, String(args[k]));

      // projectId явно указан — точечный запрос по одному (доступному) проекту, вне scope
      if (args.projectId) {
        const pid = String(args.projectId);
        assertProjectReadable(pid);
        params.set('projectId', pid);
        return json((await api(`/api/tasks?${params}`)).map(brief));
      }

      if (args.scope === 'all') {
        const data = await api(`/api/tasks?${params}`);
        return json(data.filter(isTaskInScope).map(brief));
      }

      if (PROJECT_ID) params.set('projectId', PROJECT_ID);
      else params.set('personal', 'true');
      const data = await api(`/api/tasks?${params}`);
      return json(data.map(brief));
    }

    case 'tasks_search': {
      const params = new URLSearchParams({ q: String(args.query ?? '') });
      const data = await api(`/api/tasks?${params}`);
      return json(data.filter(isTaskInScope).map(brief));
    }

    case 'tasks_get': {
      const task = await api(`/api/tasks/${args.id}`);
      assertTaskAccessible(task);
      return json(task);
    }

    case 'tasks_board_columns': {
      if (!PROJECT_ID)
        return json({ note: 'Личные задачи используют дефолтные колонки.', columns: DEFAULT_COLUMNS });
      const proj = await api(`/api/projects/${PROJECT_ID}`);
      const cols = proj.boardColumns && proj.boardColumns.length ? proj.boardColumns : DEFAULT_COLUMNS;
      return json(cols.map(c => ({ id: c.id, name: c.name, category: c.category })));
    }

    case 'tasks_create': {
      const body = { title: args.title };
      for (const k of ['description', 'priority', 'dueDate', 'dueTime', 'reminderMinutes', 'recurrence', 'assignee', 'personaId', 'modelTier', 'worktreePath', 'worktreeBranch', 'labels', 'columnId', 'executionExpiresAfterMinutes'])
        if (args[k] !== undefined) body[k] = args[k];
      // Происхождение задачи из окружения хода: персона-постановщик (если ход шёл от её лица)
      // и чат-источник. Чат-источник — факт «задача рождена в этом чате», он не зависит от того,
      // кто говорил, поэтому шлём его всегда, когда известен id чата. Раньше оба поля гейтевались
      // персоной — рудимент механики докладов (требование персон с обеих сторон уже снято, см.
      // TaskExecutionService.ShouldReportToDelegator), и задача из обычного чата без персоны
      // оставалась без SourceSessionId → её чат-исполнитель всплывал в корень вместо дочерней связи.
      if (SELF_PERSONA_ID) body.createdByPersonaId = SELF_PERSONA_ID;
      if (SESSION_ID) body.sourceSessionId = SESSION_ID;
      if (Array.isArray(args.subtasks) && args.subtasks.length)
        body.subtasks = args.subtasks.map(t => ({ title: String(t) }));
      // Целевой проект: по умолчанию текущий; явный projectId, отличный от текущего,
      // требует полной (не readonly) привязки ProjectTasks
      const targetProjectId = args.projectId ? String(args.projectId) : PROJECT_ID;
      if (targetProjectId && targetProjectId !== PROJECT_ID) assertProjectWritable(targetProjectId);
      const path = targetProjectId ? `/api/projects/${targetProjectId}/tasks` : '/api/tasks';
      return json(await api(path, { method: 'POST', body: JSON.stringify(body) }));
    }

    case 'tasks_update': {
      const { id, ...rest } = args;
      // Текущая задача должна быть видна в этом контексте (закрывает дыру: без этого можно
      // было редактировать чужой проект по одному только taskId, зная его)
      if (PROJECT_ID) assertTaskAccessible(await api(`/api/tasks/${id}`));
      // Перенос в другой проект — целевой проект должен быть доступен на запись
      if (rest.projectId) assertProjectWritable(String(rest.projectId));
      return json(await api(`/api/tasks/${id}`, { method: 'PUT', body: JSON.stringify(rest) }));
    }

    case 'tasks_complete': {
      if (PROJECT_ID) assertTaskAccessible(await api(`/api/tasks/${args.id}`));
      const body = { status: 'done' };
      if (args.resultMarkdown !== undefined) body.resultMarkdown = args.resultMarkdown;
      if (args.linkedFiles !== undefined) body.linkedFiles = args.linkedFiles;
      return json(await api(`/api/tasks/${args.id}`, { method: 'PUT', body: JSON.stringify(body) }));
    }

    case 'tasks_run_executor': {
      // Аварийный рубильник прямого запуска (TASKS_EXECUTE="0"); в продукте запрет по ходу
      // приходит от бэкенда — 403 с объяснением, почему запуск сейчас невозможен
      if (!EXECUTE_ENABLED) throw new Error('tasks_run_executor выключен на этом сервере (TASKS_EXECUTE=0)');
      const t = await api(`/api/tasks/${args.taskId}/execute`, { method: 'POST' });
      return json({
        id: t.id,
        title: t.title,
        status: t.status,
        executorSessionId: t.linkedSessionId ?? null,
        note: 'Исполнитель запущен и работает в фоне — прогресс виден в связанной сессии и статусе задачи.',
      });
    }

    case 'tasks_delete': {
      if (PROJECT_ID) assertTaskAccessible(await api(`/api/tasks/${args.id}`));
      await api(`/api/tasks/${args.id}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `Задача ${args.id} удалена.` }] };
    }

    case 'tasks_add_subtask': {
      // Подзадачи обновляются списком целиком: читаем, добавляем, сохраняем
      const task = await api(`/api/tasks/${args.taskId}`);
      if (PROJECT_ID) assertTaskAccessible(task);
      const subtasks = [...task.subtasks, { id: '', title: String(args.title), isDone: false }];
      return json(await api(`/api/tasks/${args.taskId}`, {
        method: 'PUT', body: JSON.stringify({ subtasks }),
      }));
    }

    case 'tasks_toggle_subtask': {
      const task = await api(`/api/tasks/${args.taskId}`);
      if (PROJECT_ID) assertTaskAccessible(task);
      const match = s =>
        (args.subtaskId && s.id === args.subtaskId) ||
        (args.subtaskTitle && s.title === args.subtaskTitle);
      if (!task.subtasks.some(match))
        throw new Error('Подзадача не найдена — проверьте subtaskId/subtaskTitle через tasks_get');
      const subtasks = task.subtasks.map(s => match(s) ? { ...s, isDone: Boolean(args.isDone) } : s);
      return json(await api(`/api/tasks/${args.taskId}`, {
        method: 'PUT', body: JSON.stringify({ subtasks }),
      }));
    }

    case 'tasks_suggest_meta': {
      const body = { title: String(args.title), description: args.description, projectId: PROJECT_ID || null };
      return json(await api('/api/tasks/ai/classify', { method: 'POST', body: JSON.stringify(body), timeoutMs: LLM_TIMEOUT_MS }));
    }

    case 'tasks_normalize_title':
      return json(await api('/api/tasks/ai/normalize-title', {
        method: 'POST', body: JSON.stringify({ title: String(args.title) }), timeoutMs: LLM_TIMEOUT_MS,
      }));

    case 'tasks_find_duplicate': {
      const body = { title: String(args.title), description: args.description, projectId: PROJECT_ID || null };
      return json(await api('/api/tasks/ai/find-duplicate', { method: 'POST', body: JSON.stringify(body), timeoutMs: LLM_TIMEOUT_MS }));
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
          serverInfo: { name: 'tasks', version: '1.0.0' },
          instructions: INSTRUCTIONS,
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
