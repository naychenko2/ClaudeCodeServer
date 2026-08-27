// MCP-сервер персон ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// ЗАМОРОЖЕН (ветка отката, ADR-012 фаза 2 волна 2): источник контракта —
// backend/ClaudeHomeServer/Services/Mcp/Http/PersonasToolset.Schemas.cs; правки схем/поведения
// обязаны ехать парой с http-веткой (сторож парности — PersonasToolsetParityTests).
// Файл не удалять: Mcp:HttpTransport=false возвращает personas на stdio с этим env.
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
//   PERSONAS_WRITE      — "0" = скрыть ВСЕ write-инструменты сразу (модули manage и automation).
//                         Аварийный рубильник прямых запусков: ClaudeSession шлёт "1" ВСЕГДА —
//                         состав инструментов не имеет права зависеть от хода (иначе процесс CLI
//                         перезапускается со всеми MCP, а personas_create «пропадает»).
//   PERSONAS_MANAGE     — "0" = скрыть модуль manage: personas_create/update/delete/
//                         bindings_set/generate_avatar/ai_team (плюс справочник слотов характера).
//   PERSONAS_AUTOMATION — "0" = скрыть модуль automation: personas_automation_list/create/
//                         update/delete/test (плюс справочник параметров триггера — самые
//                         тяжёлые схемы сервера).
//                         Оба модуля решаются ПО ПЕРСОНЕ (tool-ключи personas-manage /
//                         personas-automation, дефолт — пресет по Persona.Specialty), значит
//                         постоянны в рамках сессии. Ядро сервера (personas_list/get, привязки,
//                         persona_ask) от них не зависит. Права персоны дополнительно режут
//                         Persona.Tools/ExtraDisallowedTools.
//   PERSONAS_EXTRA_PROJECT_IDS  — CSV id проектов из кросс-проектных привязок ProjectPersonas
//                         текущей персоны: вся их команда видна в personas_list(scope=context)
//                         и резолвится по handle в persona_ask (в дополнение к своему контексту)
//   PERSONAS_EXTRA_PERSONA_IDS  — CSV id точечных персон из тех же привязок (сужение до одной
//                         персоны вместо всей команды её проекта)
//
//   Правила проактивности (personas_automation_*) — свой модуль (PERSONAS_AUTOMATION);
//   automation-эндпоинты бэкенда ничем не гейтятся, и персона с модулем может настраивать
//   проактивность ЛЮБОЙ персоне, включая саму себя, без обязательного участия пользователя —
//   осознанное решение (в отличие от bindings, самоограничения тут нет).
//
// Персона — AI-собеседник с именем, ролью, характером и аватаром; бывает глобальной
// или привязанной к проекту. Изоляция per-owner — на стороне backend (токен определяет
// владельца, чужие персоны и проекты недоступны).

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


const API_URL = (process.env.PERSONAS_API_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const API_TOKEN = process.env.PERSONAS_API_TOKEN ?? '';
const PROJECT_ID = process.env.PERSONAS_PROJECT_ID || null;
const SELF_ID = process.env.PERSONAS_SELF_ID || null;
// Сессия, в которой работает модель — заголовок X-Caller-Session-Id: по нему бэкенд
// определяет глубину делегирования идущего хода и гейтит persona_ask (анти-рекурсия)
const SESSION_ID = process.env.PERSONAS_SESSION_ID || null;
const MENTIONS = process.env.PERSONAS_MENTIONS === '1';
const BINDINGS = process.env.PERSONAS_BINDINGS === '1';
// Общий аварийный рубильник write-инструментов (прямые запуски сервера вне ClaudeSession)
const WRITE = process.env.PERSONAS_WRITE !== '0';
// Модули сервера: manage — CRUD персон, automation — правила проактивности. Выключаются
// явным "0"; без переменной включены (совместимость с прямыми запусками и старым конфигом).
const MANAGE = WRITE && process.env.PERSONAS_MANAGE !== '0';
const AUTOMATION = WRITE && process.env.PERSONAS_AUTOMATION !== '0';
// Кросс-проектные привязки ProjectPersonas: доступ к команде/точечным персонам ДРУГОГО проекта
const EXTRA_PROJECT_IDS = (process.env.PERSONAS_EXTRA_PROJECT_IDS || '').split(',').map(s => s.trim()).filter(Boolean);
const EXTRA_PERSONA_IDS = (process.env.PERSONAS_EXTRA_PERSONA_IDS || '').split(',').map(s => s.trim()).filter(Boolean);

// --- HTTP к бэкенду ---

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
        // Сессия, в которой работает модель: по ней бэкенд гейтит persona_ask на
        // делегированном ходу (состав инструментов при этом неизменен)
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

// Себя ли спрашивают по handle (без запроса — если SELF_ID не задан, вопрос не может быть о себе)
async function isSelfHandle(handle) {
  const self = await api(`/api/personas/${encodeURIComponent(SELF_ID)}`).catch(() => null);
  return !!(self && String(self.handle).toLowerCase() === handle.toLowerCase());
}

// Тело JSON-ошибки бэкенда из сообщения api() — "HTTP <код>: <json>"
function parseErrorBody(message) {
  const idx = message.indexOf(': ');
  if (idx < 0) return null;
  try { return JSON.parse(message.slice(idx + 2)); } catch { return null; }
}

// --- Инструменты ---

const CONTEXT_NOTE = PROJECT_ID
  ? 'Контекст — текущий проект: для проектной персоны projectId можно не указывать.'
  : 'Контекст — чат вне проекта: по умолчанию создаются глобальные персоны.';

const COLORS = ['yellow', 'orange', 'blue', 'green', 'purple', 'red', 'brown', 'cyan', 'pink'];

// Ссылка на справочник сервера (поле instructions ответа initialize): развёрнутые описания
// слотов характера, привязок и triggerArgs лежат ТАМ и попадают в контекст один раз на сервер,
// а не в каждую схему. Дублировать их в description — значит платить за них ×2…×3.
const SEE_INSTRUCTIONS = 'см. инструкции сервера personas';

// Общие поля персоны для create/update (кроме name — у create он обязателен)
const PERSONA_FIELDS = {
  role: { type: 'string', description: 'Роль — главное в отображении («Роль (Имя)»), например «Дизайнер»' },
  specialty: {
    type: 'string',
    enum: ['none', 'analyst', 'planner', 'reviewer', 'executor', 'secretary',
      'coordinator', 'mentor', 'designer', 'consultant', 'librarian', 'tester',
      'backendExecutor', 'frontendExecutor', 'devopsExecutor'],
    description: 'Специализация для оркестрации команды (роутинг типовых сабагентов на персону); '
      + 'executor/tester при access "full" даёт право править файлы — выставляй осознанно',
  },
  description: { type: 'string', description: 'Короткое описание, кто это (для карточки)' },
  // Контракт характера — по слотам (собирается в contract на бэкенде). Что писать в каждый
  // слот — в INSTRUCTIONS: здесь description только там, где имя поля само за себя не говорит.
  character: { type: 'string', description: `Характер на «ты» («Ты — …»), 2-5 предложений; слоты — ${SEE_INSTRUCTIONS}` },
  tone: { type: 'string' },
  mustDo: { type: 'array', items: { type: 'string' } },
  mustNot: { type: 'array', items: { type: 'string' } },
  outputFormat: { type: 'string' },
  speechExamples: { type: 'array', items: { type: 'string' } },
  systemPrompt: { type: 'string', description: 'УСТАРЕЛО: единый текст характера — используй character' },
  // Уровень модели — единственный способ задать модель персоны (конкретную модель через
  // MCP передать нельзя: поле «Модель» убрано из карточки, настраивается только ячейками
  // уровней). Уровень резолвится в модель на бэке (слоты владельца); уровень самой задачи
  // сильнее уровня персоны.
  modelTier: {
    type: 'string',
    enum: ['strong', 'medium', 'weak'],
    description: 'strong — архитектура, ревью, запутанный баг; medium — работа по плану; '
      + 'weak — рутина. Сомневаешься — не указывай',
  },
  effort: { type: 'string', description: 'Усилие рассуждения модели' },
  color: { type: 'string', enum: COLORS },
  greeting: { type: 'string' },
  memoryEnabled: { type: 'boolean', description: 'Долгая память персоны (по умолчанию включена)' },
  handle: { type: 'string', description: '@handle (латинский slug); пусто при создании — авто из имени' },
};

// Привязка персоны (флаг persona-bindings): источник знаний или правило с условием применения
const BINDING_ITEM_SCHEMA = {
  type: 'object',
  required: ['type', 'target'],
  properties: {
    // Что означают target/path при каждом type и как писать condition/mode — в INSTRUCTIONS:
    // эта схема встречается трижды (create, update, bindings_set), любой текст здесь тройной
    type: {
      type: 'string',
      enum: ['project', 'projectPath', 'knowledge', 'notes', 'tool', 'skill', 'projectPersonas', 'projectTasks'],
      description: `формат привязки — ${SEE_INSTRUCTIONS}`,
    },
    target: { type: 'string' },
    path: { type: 'string' },
    condition: { type: 'string' },
    mode: { type: 'string', enum: ['auto', 'always', 'off'] },
  },
};

// Параметры привязок в create/update — только при включённом флаге persona-bindings
const BINDING_CREATE_FIELDS = BINDINGS ? {
  bindings: { type: 'array', items: BINDING_ITEM_SCHEMA, description: 'Явные привязки источников знаний и правил' },
  autoBindings: { type: 'boolean', description: 'true — после создания AI сам подберёт привязки под роль персоны' },
} : {};

// Правило проактивности (событие-триггер → персона сама пишет в закреплённый чат правила,
// без запроса пользователя). Args триггера — гибкий объект, форма зависит от triggerType
// (см. комментарий к AutomationTrigger в PersonaAutomation.cs).
// Значения enum — camelCase (глобальная JsonStringEnumConverter(CamelCase) на бэкенде,
// см. Program.cs): "gitCommit"/"taskStatus"/"gate"/"work", НЕ PascalCase.
const TRIGGER_TYPES = ['timer', 'file', 'note', 'gitCommit', 'taskStatus', 'mention'];
const ACTION_WEIGHTS = ['gate', 'work'];
const PROJECT_HINT = PROJECT_ID ? ` Текущий проект этого чата: projectId="${PROJECT_ID}".` : '';

const AUTOMATION_FIELDS = {
  name: { type: 'string', description: 'Человекочитаемое имя правила («Следить за релизами») — видно в списке и в заголовке чата правила' },
  enabled: { type: 'boolean', description: 'Включено ли правило (по умолчанию true)' },
  // Смысл триггеров, форм triggerArgs и веса действия — в INSTRUCTIONS (набор идёт ×2:
  // automation_create + automation_update)
  triggerType: {
    type: 'string', enum: TRIGGER_TYPES,
    description: 'timer — по расписанию; file/note/gitCommit/taskStatus — опрос изменений; '
      + 'mention — по @упоминанию handle персоны',
  },
  triggerArgs: { type: 'object', description: 'Параметры триггера, форма зависит от triggerType' },
  conditionOnlyIf: { type: 'string', description: 'Доп. условие для LLM-гейта («только если касается деплоя»)' },
  quietFrom: { type: 'string', description: 'Начало тихих часов "HH:mm" (местное время владельца)' },
  quietTo: { type: 'string', description: 'Конец тихих часов "HH:mm" (можно через полночь)' },
  minIntervalMinutes: { type: 'integer', minimum: 1, description: 'Минимальный интервал срабатываний; дефолт 5 мин (file — 1 мин)' },
  actionWeight: { type: 'string', enum: ACTION_WEIGHTS, description: 'gate — оценить и коротко ответить; work — полноценный агентский ход' },
  actionInstruction: { type: 'string', description: 'Инструкция себе на реакцию при срабатывании' },
  rememberInHistory: { type: 'boolean', description: 'Писать карточку-итог в историю чата правила' },
  actionExpiresAfterMinutes: { type: ['integer', 'null'], description: 'TTL чата правила, мин; null — бессрочно; не указывай — дефолт 1440' },
};

const AUTOMATION_KEYS = ['name', 'enabled', 'triggerType', 'triggerArgs', 'conditionOnlyIf',
  'quietFrom', 'quietTo', 'minIntervalMinutes', 'actionWeight', 'actionInstruction',
  'rememberInHistory', 'actionExpiresAfterMinutes'];

// Справочник сервера (поле instructions ответа initialize). Клиент кладёт его в контекст ОДИН
// раз на сервер, тогда как текст в description оплачивается в каждой схеме, где повторён:
// слоты характера шли ×2 (create+update), triggerArgs ×2, привязка ×3.
//
// ВАЖНО: claude CLI усекает instructions примерно на 2 КБ, дописывая «… [truncated]» (проверено
// живой пробой: из 6 КБ маркеров доехали первые ~2050 символов). Поэтому сюда идёт только то,
// что дублировалось по нескольку раз, в максимально плотной форме — всё остальное осталось
// в схемах. При правке следи за длиной: хвост за лимитом до модели не доедет.
// Секции справочника следуют флагам ровно как состав инструментов: без модуля manage
// слоты характера некому применять, без AUTOMATION не нужны параметры триггера, без
// BINDINGS нет привязок. Иначе в урезанном режиме (3 инструмента) справочник весил бы
// больше самих схем.
const INSTRUCTIONS = [
  'Справочники сервера «Персоны» (на них ссылаются описания инструментов).',
  ...(MANAGE ? [
    '',
    'СЛОТЫ ХАРАКТЕРА (create/update), заполняй все: character — характер на «ты» («Ты — …»),',
    '  2-5 предложений; tone — тон одной фразой; mustDo/mustNot — по 2-4 пункта «всегда»/',
    '  «никогда»; outputFormat — формат ответов; speechExamples — 1-2 реплики от лица персоны;',
    '  greeting — приветствие. systemPrompt — устаревший единый текст характера.',
  ] : []),
  // Формат привязки нужен только тем, кто их СОСТАВЛЯЕТ (create/update/bindings_set —
  // все под MANAGE): без модуля bindings_list отдаёт готовые записи, конструировать нечего,
  // а справочник съедал бы ~700 символов из жёсткого лимита instructions
  ...(BINDINGS && MANAGE ? [
  '',
  'ПРИВЯЗКА (bindings в create/update, bindings_set): { type, target, path?, condition?, mode? }.',
  '  type → target/path: project — проект целиком (projectId); projectPath — папка/файл проекта',
  '  (projectId; path обязателен); knowledge — база знаний (datasetId); notes — заметки',
  '  ("personal"|projectId; path: папка); tool — рубильник (tasks/…; mcp:<ключ> — сервер',
  '  реестра, по умолчанию НЕ выдан — personas_mcp_grant); skill; projectPersonas —',
  '  ЧУЖОГО проекта для persona_ask (projectId; path: id персоны = сужение, пусто = вся команда);',
  '  projectTasks — задачи ЧУЖОГО проекта (projectId; path "readonly" = чтение, иначе полный доступ).',
  '  condition — когда применять (пусто = всегда). mode — auto (дефолт) | always | off.',
  ] : []),
  ...(AUTOMATION ? [
  '',
  'ПАРАМЕТРЫ ТРИГГЕРА (triggerArgs в automation_create/update) по triggerType:',
  '  timer: { schedule: { type: "daily"|"weekdays"|"weekly"|"interval", time: "HH:mm",',
  '    weekdays?: [1..7] (1=пн), intervalMinutes?: number }, tz?: string }',
  '  file: { projectId, glob: "src/**/*.ts", kinds: ["created","changed"] }',
  '  note: { source: "personal"|projectId, tags?: ["#тег"], section?: "папка" }',
  '  gitCommit: { projectId, paths?: ["src/**"] } — папка должна быть git-репозиторием',
  '  taskStatus: { projectId?, from?, to?, assignee?: "me"|"claude" }',
  '  mention: {} — не заполняй, срабатывает само',
  '  file/gitCommit: вместо projectId — folder (только у ГЛОБАЛЬНОЙ персоны, подпуть в домашней',
  '  папке, "" = вся папка); с projectId вместе не задавать.' + PROJECT_HINT,
  ] : []),
].join('\n');

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
    // ВСЕГДА в tools/list — до модульных спредов (инвариант стабильности состава: инструмент
    // не смеет мерцать между ходами). Ограничение — на бэкенде: вызов из чата разрешён только
    // онбординг-сессии (по X-Caller-Session-Id), в остальных чатах сервер откажет.
    name: 'personas_set_default',
    description: 'Назначить персону дефолтной: глобальную — личной дефолт-персоной пользователя, ' +
      'проектную — руководителем её проекта. Для чата-онбординга: задаёт назначенную персону ' +
      '(как созданную через personas_create, так и выбранную из существующих) ПОСЛЕ явного ' +
      'подтверждения пользователем. Права выбранной существующей персоны НЕ меняй — вызывай ' +
      'только этот инструмент. В обычных чатах бэкенд откажет — тогда предложи пользователю ' +
      'сменить дефолт в настройках.',
    inputSchema: {
      type: 'object',
      required: ['personaId'],
      properties: { personaId: { type: 'string', description: 'ID персоны, назначаемой дефолтной' } },
    },
  },
  ...(MANAGE ? [{
    name: 'personas_create',
    description: `Создать персону — AI-собеседника с именем, ролью и характером. ${CONTEXT_NOTE} ` +
      `Заполняй ВСЕ слоты характера — ${SEE_INSTRUCTIONS}.`,
    inputSchema: {
      type: 'object',
      required: ['name'],
      properties: {
        name: { type: 'string', description: 'Имя персоны' },
        ...PERSONA_FIELDS,
        scope: { type: 'string', enum: ['global', 'project'], description: 'Зона персоны: global (дефолт) — во всех чатах; project — в своём проекте' },
        projectId: { type: 'string', description: 'ID проекта для scope=project (дефолт — проект сессии)' },
        avatarPrompt: { type: 'string', description: 'Внешность для фото-аватара; пусто — из имени и роли (фото создаётся само)' },
        ...BINDING_CREATE_FIELDS,
      },
    },
  },
  {
    name: 'personas_update',
    description: 'Изменить персону: поля как при создании, передавай только изменяемые. ' +
      'Пустая строка очищает role/effort/color/greeting (specialty — "none"). ' +
      'Смена scope на "project" требует projectId.' +
      (BINDINGS ? ' bindings — ПОЛНАЯ замена набора привязок (свои собственные менять нельзя).' : ''),
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
  }] : []),
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
        'владельца). Возвращает кандидатов, НЕ сохраняет — ' + (MANAGE
          ? 'сохрани нужные через personas_bindings_set.'
          : 'сохранить их может только персона с модулем manage, покажи список пользователю.'),
      inputSchema: {
        type: 'object',
        required: ['id'],
        properties: { id: { type: 'string', description: 'ID персоны' } },
      },
    },
    {
      name: 'personas_mcp_list',
      description: 'MCP-серверы личного реестра владельца: key, label, транспорт, статус, ' +
        'выдан ли персоне (поле enabledForPersona при id). ' +
        'Сервер по умолчанию НЕ выдан ни одной персоне — его нужно выдать явно. ' +
        'Выдать/отозвать один сервер точечно (не трогая остальные привязки) — personas_mcp_grant; ' +
        'полная замена набора привязок — personas_bindings_set/bindings с target "mcp:<ключ>".',
      inputSchema: {
        type: 'object',
        properties: { id: { type: 'string', description: 'ID персоны — проверить, выдан ли ей каждый сервер (без id — только список серверов)' } },
      },
    },
    // bindings_set и mcp_grant — правка чужой персоны, поэтому едут с модулем manage, а не с
    // read-инструментами привязок (иначе персона без manage меняла бы права коллегам)
    ...(MANAGE ? [{
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
      // Точечная выдача/отзыв одного MCP-сервера — в отличие от bindings_set НЕ трогает
      // остальные привязки персоны. Слабой модели проще кликнуть один сервер, чем собрать
      // весь набор (риск снести привязки к проектам/базам). mode: auto/always — выдать
      // (allow: выдан, deny: включён как дефолт), off — отозвать (allow: не выдан, deny: выключен).
      name: 'personas_mcp_grant',
      description: 'Выдать или отозвать персоне один MCP-сервер личного реестра — точечно, ' +
        'не затрагивая остальные её привязки (в отличие от personas_bindings_set, который ' +
        'заменяет весь набор). ' +
        'Сервер по умолчанию НЕ выдан: выдай его (revoke=false), чтобы персона получила ' +
        'инструменты сервера в своих чатах. ' +
        'Свои собственные доступы персона менять не может.',
      inputSchema: {
        type: 'object',
        required: ['id', 'key'],
        properties: {
          id: { type: 'string', description: 'ID персоны (не себя)' },
          key: { type: 'string', description: 'Ключ MCP-сервера (см. personas_mcp_list)' },
          revoke: { type: 'boolean', description: 'true — отозвать сервер у персоны, false (дефолт) — выдать' },
        },
      },
    }] : []),
    {
      name: 'knowledge_search',
      description: 'Гибридный поиск (смысловой + полнотекстовый) по привязанной базе знаний (Dify) ' +
        'по её datasetId. Используй, когда выполняется условие привязки-«базы знаний» из твоего ' +
        'контекста. Возвращает metadataFields (по каким полям можно фильтровать) и hits — выдержки ' +
        '(документ, score, текст, metadata: напр. дата встречи/источник), по ним датируй и ' +
        'атрибутируй факты. Диапазоны дат не поддерживаются — для периода бери contains/start with ' +
        'по «2025-09»/«2026».',
      inputSchema: {
        type: 'object',
        required: ['datasetId', 'query'],
        properties: {
          datasetId: { type: 'string', description: 'ID датасета из строки привязки' },
          query: { type: 'string', description: 'Запрос на естественном языке (по смыслу вопроса)' },
          topK: { type: 'integer', minimum: 1, maximum: 20, description: 'Сколько выдержек (дефолт 6)' },
          filters: {
            type: 'array',
            description: 'Фильтры по метаданным документов — только по полям из metadataFields ' +
              '(сделай сначала поиск без фильтра, чтобы их увидеть)',
            items: {
              type: 'object',
              required: ['name', 'operator'],
              properties: {
                name: { type: 'string', description: 'Имя поля метаданных (напр. meeting_date, meeting_source)' },
                operator: {
                  type: 'string',
                  enum: ['contains', 'not contains', 'start with', 'end with', 'is', 'is not', 'empty', 'not empty'],
                  description: 'Строковый оператор',
                },
                value: { type: 'string', description: 'Значение (не нужно для empty/not empty)' },
              },
            },
          },
          logic: { type: 'string', enum: ['and', 'or'], description: 'Как объединять несколько фильтров (по умолчанию and)' },
        },
      },
    },
  ] : []),
  // Модуль automation: событие-триггер → персона сама пишет в закреплённый чат правила,
  // без запроса пользователя. Для любой персоны, включая себя. Схемы triggerArgs — самые
  // тяжёлые на сервере, поэтому весь модуль за своим ключом (personas-automation).
  ...(AUTOMATION ? [{
    name: 'personas_automation_list',
    description: 'Список правил проактивности персоны — триггеры и условия, при которых она ' +
      'сама пишет в закреплённый чат правила без запроса пользователя.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID персоны' } },
    },
  },
  {
    name: 'personas_automation_create',
    description: 'Создать правило проактивности персоны. Можно для ЛЮБОЙ персоны, включая ' +
      'саму себя — самоограничений нет. Троттлинг (тихие часы, минимальный интервал, потолок ' +
      'срабатываний в час) применяется сервером автоматически поверх твоих настроек.',
    inputSchema: {
      type: 'object',
      required: ['id', 'name', 'triggerType'],
      properties: {
        id: { type: 'string', description: 'ID персоны, для которой создаётся правило' },
        ...AUTOMATION_FIELDS,
      },
    },
  },
  {
    name: 'personas_automation_update',
    description: 'Изменить правило проактивности: передавай только изменяемые поля — ' +
      'остальные (включая triggerArgs целиком) сохранятся как есть.',
    inputSchema: {
      type: 'object',
      required: ['id', 'ruleId'],
      properties: {
        id: { type: 'string', description: 'ID персоны' },
        ruleId: { type: 'string', description: 'ID правила' },
        ...AUTOMATION_FIELDS,
      },
    },
  },
  {
    name: 'personas_automation_delete',
    description: 'Удалить правило проактивности персоны по id.',
    inputSchema: {
      type: 'object',
      required: ['id', 'ruleId'],
      properties: {
        id: { type: 'string', description: 'ID персоны' },
        ruleId: { type: 'string', description: 'ID правила' },
      },
    },
  },
  {
    name: 'personas_automation_test',
    description: 'Ручной прогон правила проактивности: синтетическое событие, троттлинг ' +
      'игнорируется. Запускает реакцию в фоне, результата не ждёт.',
    inputSchema: {
      type: 'object',
      required: ['id', 'ruleId'],
      properties: {
        id: { type: 'string', description: 'ID персоны' },
        ruleId: { type: 'string', description: 'ID правила' },
      },
    },
  }] : []),
  // Хвост модуля manage: удаление персоны, аватар и генерация состава команды
  ...(MANAGE ? [{
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
  {
    name: 'personas_ai_team',
    description: 'Сгенерировать команду персон под задачу/проект: ИИ по промпту и CLAUDE.md проекта ' +
      'предлагает состав 3-6 персон. Возвращает ЧЕРНОВИКИ (members), персон НЕ создаёт — покажи ' +
      'состав пользователю и заведи нужных через personas_create (поля черновика те же). ' +
      'Требуется проект: вне проекта укажи projectId явно.',
    inputSchema: {
      type: 'object',
      required: ['prompt'],
      properties: {
        prompt: { type: 'string', description: 'Какая команда нужна и подо что (задача/цели проекта)' },
        projectId: { type: 'string', description: 'ID проекта команды (дефолт — проект сессии)' },
      },
    },
  }] : []),
  // @упоминания (флаг persona-mentions): спросить другую персону — она ответит one-shot'ом
  // от своего лица, со своим характером, памятью и моделью. Глубина делегирования строго 1:
  // one-shot, запущенный persona_ask, этот сервер уже не получает.
  ...(MENTIONS ? [{
    name: 'persona_ask',
    description: 'Спросить другую персону: она ответит от своего лица, в своём характере и со своей ' +
      'долгой памятью. Зови, когда пользователь упоминает @handle персоны или нужна её экспертиза. ' +
      'Вопрос формулируй самодостаточно — персона не видит этот разговор; контекст передай в context. ' +
      'Тёзки из разных проектов: вызов по handle вернёт кандидатов — повтори с personaId.',
    inputSchema: {
      type: 'object',
      required: ['question'],
      properties: {
        handle: { type: 'string', description: 'handle персоны (без @), см. personas_list; не нужен при personaId' },
        personaId: { type: 'string', description: 'ID персоны — однозначная альтернатива handle' },
        question: { type: 'string', description: 'Самодостаточный вопрос к персоне' },
        context: { type: 'string', description: 'Контекст разговора (кратко, только нужное для ответа)' },
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

// model сознательно отсутствует: конкретная модель персоне через MCP не задаётся (поле
// «Модель» убрано из карточки — настраивается только ячейками уровней), и даже явная
// передача model в аргументах сюда не попадёт — поле не попадёт и в body запроса
const FIELD_KEYS = ['name', 'role', 'specialty', 'description', 'systemPrompt', 'modelTier', 'effort', 'color', 'greeting', 'memoryEnabled', 'scope', 'projectId', 'handle'];

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

// Инструменты модулей — список TOOLS их уже фильтрует, но исполнение перепроверяем
// отдельно (defense-in-depth: выключенный модуль не должен отработать при ошибке экспозиции)
const MANAGE_TOOLS = new Set([
  'personas_create', 'personas_update', 'personas_delete', 'personas_bindings_set',
  'personas_mcp_grant', 'personas_generate_avatar', 'personas_ai_team',
]);
const AUTOMATION_TOOLS = new Set([
  'personas_automation_list', 'personas_automation_create', 'personas_automation_update',
  'personas_automation_delete', 'personas_automation_test',
]);

async function callTool(name, args) {
  if (!MANAGE && MANAGE_TOOLS.has(name))
    throw new Error('Инструмент управления персонами недоступен этой персоне (модуль manage выключен). Попроси пользователя включить его привязкой tool:personas-manage.');
  if (!AUTOMATION && AUTOMATION_TOOLS.has(name))
    throw new Error('Инструменты правил проактивности недоступны этой персоне (модуль automation выключен). Попроси пользователя включить его привязкой tool:personas-automation.');
  switch (name) {
    case 'personas_list': {
      const scope = args.scope ?? 'context';
      if (scope === 'all') return json(await api('/api/personas'));
      if (scope === 'project' && !PROJECT_ID)
        throw new Error('Текущая сессия вне проекта — проектных персон здесь нет (используй scope "global" или "all").');
      const params = new URLSearchParams({ scope });
      if (scope !== 'global') params.set('projectId', PROJECT_ID ?? '');
      // Кросс-проектные привязки ProjectPersonas — команда/точечные персоны другого проекта
      if (scope === 'context') {
        if (EXTRA_PROJECT_IDS.length) params.set('extraProjectIds', EXTRA_PROJECT_IDS.join(','));
        if (EXTRA_PERSONA_IDS.length) params.set('extraPersonaIds', EXTRA_PERSONA_IDS.join(','));
      }
      return json(await api(`/api/personas?${params}`));
    }

    case 'personas_get':
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}`));

    case 'personas_set_default':
      // Гейт онбординг-сессии — на бэкенде по X-Caller-Session-Id (api() шлёт его всегда)
      return json(await api(`/api/personas/${encodeURIComponent(args.personaId)}/make-default`, { method: 'POST' }));

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
      // Персона из чата не выбирает аватар руками — просим бэкенд сгенерить фото
      // автоматически (best-effort; без Fal:ApiKey тихо остаются инициалы)
      body.autoAvatar = true;
      if (typeof args.avatarPrompt === 'string' && args.avatarPrompt.trim())
        body.avatarPrompt = args.avatarPrompt;
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

    case 'personas_mcp_list': {
      const servers = await api('/api/mcp/servers');
      let enabledByKey = null;
      if (args.id) {
        const targets = await api(`/api/personas/binding-targets?type=tool&personaId=${encodeURIComponent(args.id)}`);
        enabledByKey = new Map(
          targets.filter(t => typeof t.id === 'string' && t.id.startsWith('mcp:'))
            .map(t => [t.id.slice(4), t.defaultEnabled]));
      }
      return json(servers.map(s => ({
        key: s.key,
        label: s.label,
        transport: s.transport,
        status: s.status?.status ?? null,
        // defaultEnabled приходит с бэка уже с учётом allow-семантики; фолбэк — только если
        // сервера вдруг нет в каталоге привязок (рассинхрон). Allow-модель: «нет записи = не выдан».
        // Каталог и реестр синхронны (оба из McpRegistry.GetByOwner), так что фолбэк фактически
        // не достигается — но семантику держим верной.
        ...(enabledByKey ? { enabledForPersona: enabledByKey.get(s.key) ?? false } : {}),
      })));
    }

    case 'personas_bindings_set': {
      assertNotSelfBindings(args.id);
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}/bindings`, {
        method: 'PUT',
        body: JSON.stringify({ bindings: args.bindings ?? [] }),
      }));
    }

    case 'personas_mcp_grant': {
      // Точечная выдача/отзыв одного MCP-сервера: берём текущие привязки, заменяем только
      // цель «mcp:<ключ>», остальное не трогаем. GET /bindings возвращает массив напрямую.
      assertNotSelfBindings(args.id);
      const key = String(args.key ?? '').trim();
      if (!key) throw new Error('Укажи key MCP-сервера (см. personas_mcp_list).');
      const mcpTarget = 'mcp:' + key;
      const revoke = args.revoke === true;
      const current = await api(`/api/personas/${encodeURIComponent(args.id)}/bindings`);
      // Канонические поля привязки (Id и служебное отбрасываем — PUT их не требует и
      // пересоздаст), mcp-цель снимаем, чтобы поставить свежую.
      const bindings = (Array.isArray(current) ? current : [])
        .map(b => ({
          type: b.type, target: b.target,
          ...(b.path != null ? { path: b.path } : {}),
          ...(b.condition != null ? { condition: b.condition } : {}),
          ...(b.mode != null ? { mode: b.mode } : {}),
        }))
        .filter(b => !(b.type === 'tool' && b.target === mcpTarget));
      // mode работает в обеих моделях доступа: auto/always — выдан (allow) и включён (deny),
      // off — не выдан (allow) и выключен (deny). condition не ставим: состав инструментов хода
      // не смеет зависеть от текста (инвариант стабильности состава).
      bindings.push({ type: 'tool', target: mcpTarget, mode: revoke ? 'off' : 'auto' });
      await api(`/api/personas/${encodeURIComponent(args.id)}/bindings`, {
        method: 'PUT',
        body: JSON.stringify({ bindings }),
      });
      return { content: [{ type: 'text', text: revoke
        ? `Сервер «${key}» отозван у персоны ${args.id}.`
        : `Сервер «${key}» выдан персоне ${args.id}.` }] };
    }

    case 'knowledge_search':
      return json(await api('/api/personas/knowledge-search', {
        method: 'POST',
        body: JSON.stringify({
          datasetId: args.datasetId,
          query: args.query,
          topK: args.topK ?? null,
          filters: Array.isArray(args.filters) ? args.filters : null,
          logic: args.logic ?? null,
        }),
      }));

    case 'personas_automation_list':
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}/automation`));

    case 'personas_automation_create': {
      const body = personaBody(args, AUTOMATION_KEYS);
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}/automation`, {
        method: 'POST',
        body: JSON.stringify(body),
      }));
    }

    case 'personas_automation_update': {
      const body = personaBody(args, AUTOMATION_KEYS);
      return json(await api(`/api/personas/${encodeURIComponent(args.id)}/automation/${encodeURIComponent(args.ruleId)}`, {
        method: 'PUT',
        body: JSON.stringify(body),
      }));
    }

    case 'personas_automation_delete':
      await api(`/api/personas/${encodeURIComponent(args.id)}/automation/${encodeURIComponent(args.ruleId)}`, { method: 'DELETE' });
      return { content: [{ type: 'text', text: `Правило ${args.ruleId} удалено.` }] };

    case 'personas_automation_test':
      await api(`/api/personas/${encodeURIComponent(args.id)}/automation/${encodeURIComponent(args.ruleId)}/test`, { method: 'POST' });
      return { content: [{ type: 'text', text: 'Правило запущено вручную (в фоне).' }] };

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

    case 'personas_ai_team': {
      // ИИ формирует состав команды по промпту + контексту проекта; нужен projectId
      // (по умолчанию — проект сессии). Ответ — черновики (members), персоны НЕ создаются.
      const projectId = args.projectId || PROJECT_ID;
      if (!projectId)
        throw new Error('Нужен projectId: текущая сессия вне проекта — укажи projectId проекта, под который формируется команда.');
      return json(await api('/api/personas/ai/team', {
        method: 'POST',
        body: JSON.stringify({ projectId, prompt: String(args.prompt ?? '') }),
      }));
    }

    case 'persona_ask': {
      if (!MENTIONS) throw new Error('Инструмент persona_ask выключен (флаг persona-mentions).');
      const personaId = args.personaId ? String(args.personaId) : '';
      const handle = personaId ? '' : String(args.handle ?? '').replace(/^@/, '');
      if (!personaId && !handle) throw new Error('Укажи handle или personaId персоны.');
      if (SELF_ID && (personaId === SELF_ID
        || (handle && await isSelfHandle(handle))))
        throw new Error('Это твой собственный id/handle — отвечай сам, спрашивать себя не нужно.');

      const body = { question: String(args.question ?? '') };
      if (personaId) body.personaId = personaId;
      else body.handle = handle;
      if (args.context) body.context = String(args.context);
      // Контекст проекта: handle резолвится среди глобальных + проектных этого проекта
      // (две проектные «маши» из разных проектов не путаются) + кросс-проектные привязки
      // ProjectPersonas — расширяют резолв на команду/точечных персон другого проекта
      if (PROJECT_ID) body.projectId = PROJECT_ID;
      if (EXTRA_PROJECT_IDS.length) body.extraProjectIds = EXTRA_PROJECT_IDS;
      if (EXTRA_PERSONA_IDS.length) body.extraPersonaIds = EXTRA_PERSONA_IDS;
      try {
        // Ответ персоны — это целый ход Claude в её контексте: 60-секундного дефолта мало,
        // бэкенд сам ограничивает ожидание и отвечает 502 «Claude не ответил за отведённое время»
        const res = await api('/api/personas/ask',
          { method: 'POST', body: JSON.stringify(body), timeoutMs: 300_000 });
        return { content: [{ type: 'text', text: res?.answer ?? '' }] };
      } catch (err) {
        if (err?.status === 409) {
          const parsed = parseErrorBody(err.message);
          const candidates = Array.isArray(parsed?.candidates) ? parsed.candidates : [];
          const list = candidates.map(c =>
            `- personaId=${c.id} — ${c.role ? `${c.role} (${c.name})` : c.name}${c.projectName ? `, проект «${c.projectName}»` : ''}`
          ).join('\n');
          throw new Error(`${parsed?.error ?? 'Неоднозначный handle'}. Повтори вызов с personaId одного из кандидатов:\n${list}`);
        }
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

// Обработка ОДНОГО JSON-RPC сообщения. Вынесена из обработчика строки, чтобы поддержать
// batch (массив сообщений): раньше массив уходил в деструктуризацию, id оказывался undefined,
// и запрос молча терялся — ответа клиент не получал вовсе и ждал до своего таймаута.
async function handleMessage(msg) {
  // null — валидный JSON: деструктуризация его роняла процесс (unhandled rejection),
  // а вместе с процессом исчезали ВСЕ инструменты сервера («Stream closed»)
  if (msg === null || typeof msg !== 'object') return;

  const { id, method, params } = msg;
  if (id === undefined || id === null) return;

  try {
    switch (method) {
      case 'initialize':
        reply(id, {
          protocolVersion: params?.protocolVersion ?? '2024-11-05',
          capabilities: { tools: {} },
          serverInfo: { name: 'personas', version: '1.0.0' },
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
