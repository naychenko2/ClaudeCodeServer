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
//   PERSONAS_WRITE      — "0" = скрыть write-инструменты управления персонами (create/update/
//                         delete, bindings_set, automation_create/update/delete/test,
//                         generate_avatar). ClaudeSession выключает их на ходах, не связанных
//                         с управлением командой, чтобы тяжёлые схемы (PERSONA_FIELDS/
//                         AUTOMATION_FIELDS ~основная масса контекста сервера) не грузились
//                         каждый ход. Read/ask-инструменты остаются всегда. Дефолт — включено
//                         (обратная совместимость прямых запусков); выключается только явным "0".
//
//   Правила проактивности (personas_automation_*) доступны ВСЕГДА, без флага (как
//   personas_create/update) — automation-эндпоинты бэкенда ничем не гейтятся. Персона может
//   настраивать проактивность ЛЮБОЙ персоне, включая саму себя, без обязательного участия
//   пользователя — осознанное решение (в отличие от bindings, самоограничения тут нет).
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
// Write-инструменты управления персонами. Выключаются только явным "0" (ClaudeSession
// ставит его на ходах без интента управления командой) — иначе включены (совместимость).
const WRITE = process.env.PERSONAS_WRITE !== '0';

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
  handle: { type: 'string', description: 'Ручной @handle (латинский slug, для @упоминаний); ' +
    'пусто при создании — авто-генерация из имени; занят/невалиден → ошибка' },
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
  triggerType: {
    type: 'string', enum: TRIGGER_TYPES,
    description: 'Тип триггера: timer — по расписанию; file/note/gitCommit/taskStatus — опрос ' +
      'изменений на тике; mention — по @упоминанию handle этой персоны (детектится сам, без опроса).',
  },
  triggerArgs: {
    type: 'object',
    description: 'Параметры триггера — форма зависит от triggerType:\n' +
      '  timer: { schedule: { type: "daily"|"weekdays"|"weekly"|"interval", time: "HH:mm", weekdays?: [1..7] (1=пн), intervalMinutes?: number }, tz?: string }\n' +
      '  file: { projectId, glob: "src/**/*.ts", kinds: ["created","changed"] }\n' +
      '        — ИЛИ вместо projectId ключ folder (только для ГЛОБАЛЬНОЙ персоны без проекта): folder — относительный подпуть в основной папке пользователя, "" = вся основная папка. Не задавай projectId и folder одновременно.\n' +
      '  note: { source: "personal"|projectId, tags?: ["#тег"], section?: "папка" }\n' +
      '  gitCommit: { projectId, paths?: ["src/**"] } — ИЛИ folder (см. file; папка должна быть git-репозиторием)\n' +
      '  taskStatus: { projectId?, from?: статус, to?: статус, assignee?: "me"|"claude" }\n' +
      '  mention: {} — не заполняй, срабатывает автоматически.' + PROJECT_HINT,
  },
  conditionOnlyIf: { type: 'string', description: 'Доп. условие-предикат для LLM-гейта («реагируй, только если касается деплоя»); пусто — без доп. условия (гейт всё равно спрашивает «стоит ли реагировать»)' },
  quietFrom: { type: 'string', description: 'Начало тихих часов "HH:mm" (местное время владельца) — правило не срабатывает в этом окне' },
  quietTo: { type: 'string', description: 'Конец тихих часов "HH:mm" (поддерживается переход через полночь, напр. 23:00→07:00)' },
  minIntervalMinutes: { type: 'integer', minimum: 1, description: 'Минимальный интервал между срабатываниями (троттлинг); по умолчанию 5 мин (для file — 1 мин)' },
  actionWeight: {
    type: 'string', enum: ACTION_WEIGHTS,
    description: 'gate — оценить событие и коротко ответить текстом, без тяжёлых действий; ' +
      'work — полноценный агентский ход (может править файлы, заводить задачи/заметки через инструменты)',
  },
  actionInstruction: { type: 'string', description: 'Инструкция себе на реакцию при срабатывании (что делать/о чём написать)' },
  rememberInHistory: { type: 'boolean', description: 'Записывать карточку-итог срабатывания в историю чата правила' },
  actionExpiresAfterMinutes: {
    type: ['integer', 'null'],
    description: 'TTL чата правила в минутах от последней активности (как у временных чатов); ' +
      'null — бессрочно; не указывай, чтобы оставить дефолт (1440 при создании / текущее значение при обновлении)',
  },
};

const AUTOMATION_KEYS = ['name', 'enabled', 'triggerType', 'triggerArgs', 'conditionOnlyIf',
  'quietFrom', 'quietTo', 'minIntervalMinutes', 'actionWeight', 'actionInstruction',
  'rememberInHistory', 'actionExpiresAfterMinutes'];

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
  ...(WRITE ? [{
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
        avatarPrompt: { type: 'string', description: 'Описание внешности для фото-аватара (необязательно; ' +
          'пусто — промпт строится из имени и роли). Фото генерируется автоматически при создании.' },
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
        'владельца). Возвращает кандидатов, НЕ сохраняет — сохрани нужные через personas_bindings_set.',
      inputSchema: {
        type: 'object',
        required: ['id'],
        properties: { id: { type: 'string', description: 'ID персоны' } },
      },
    },
    ...(WRITE ? [{
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
    }] : []),
    {
      name: 'knowledge_search',
      description: 'Гибридный поиск (смысловой + полнотекстовый по ключевым словам) по привязанной ' +
        'базе знаний (Dify) по её datasetId. Используй, когда выполняется условие привязки-«базы ' +
        'знаний» из твоего контекста: подставь datasetId из строки привязки и запрос по смыслу ' +
        'вопроса пользователя (можно включать точные термины/имена — их найдёт полнотекстовая часть). ' +
        'Возвращает: metadataFields (по каким полям можно фильтровать) и hits — выдержки (документ, ' +
        'score, текст, metadata: напр. дата встречи/источник) — используй их, чтобы датировать и ' +
        'атрибутировать факты. Фильтровать можно ТОЛЬКО по полям из metadataFields; если поля нет — ' +
        'вернётся ошибка со списком доступных. Диапазоны дат не поддерживаются (дата хранится строкой) — ' +
        'для периода используй contains/start with по году или году-месяцу («2025-09», «2026»).',
      inputSchema: {
        type: 'object',
        required: ['datasetId', 'query'],
        properties: {
          datasetId: { type: 'string', description: 'ID датасета из строки привязки (mcp__personas__knowledge_search datasetId "…")' },
          query: { type: 'string', description: 'Поисковый запрос на естественном языке (по смыслу вопроса)' },
          topK: { type: 'integer', minimum: 1, maximum: 20, description: 'Сколько выдержек вернуть (по умолчанию 6)' },
          filters: {
            type: 'array',
            description: 'Необязательные фильтры по метаданным документов. Фильтруй только по полям из ' +
              'metadataFields (сделай сначала поиск без фильтра, чтобы их увидеть).',
            items: {
              type: 'object',
              required: ['name', 'operator'],
              properties: {
                name: { type: 'string', description: 'Имя поля метаданных (напр. meeting_date, meeting_source, meeting_id)' },
                operator: {
                  type: 'string',
                  enum: ['contains', 'not contains', 'start with', 'end with', 'is', 'is not', 'empty', 'not empty'],
                  description: 'Строковый оператор. Для периода дат — contains/start with по «2025-09»/«2026»',
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
  // Правила проактивности: событие-триггер → персона сама пишет в закреплённый чат правила,
  // без запроса пользователя. Доступно всегда (без флага), для любой персоны, включая себя.
  {
    name: 'personas_automation_list',
    description: 'Список правил проактивности персоны — триггеры и условия, при которых она ' +
      'сама пишет в закреплённый чат правила без запроса пользователя.',
    inputSchema: {
      type: 'object',
      required: ['id'],
      properties: { id: { type: 'string', description: 'ID персоны' } },
    },
  },
  ...(WRITE ? [{
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
  {
    name: 'personas_ai_team',
    description: 'Сгенерировать команду персон (роли, характеры, аватары) под задачу/проект. ' +
      'ИИ по промпту и контексту проекта (CLAUDE.md) предлагает сбалансированный состав 3-6 персон. ' +
      'Возвращает ЧЕРНОВИКИ (поле members) — НЕ создаёт персон: покажи состав пользователю и создай ' +
      'нужных через personas_create (поля черновика совпадают с параметрами создания). ' +
      'Требуется проект: projectId обязателен (по умолчанию — проект текущей сессии; вне проекта — укажи явно).',
    inputSchema: {
      type: 'object',
      required: ['prompt'],
      properties: {
        prompt: { type: 'string', description: 'Какая команда нужна и подо что (задача/цели проекта)' },
        projectId: { type: 'string', description: 'ID проекта, под который формируется команда (по умолчанию — проект текущей сессии)' },
      },
    },
  }] : []),
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

const FIELD_KEYS = ['name', 'role', 'description', 'systemPrompt', 'model', 'effort', 'color', 'greeting', 'memoryEnabled', 'scope', 'projectId', 'handle'];

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

// Write-инструменты управления персонами — скрыты при PERSONAS_WRITE="0" (гейт по интенту хода)
const WRITE_TOOLS = new Set([
  'personas_create', 'personas_update', 'personas_delete', 'personas_bindings_set',
  'personas_automation_create', 'personas_automation_update', 'personas_automation_delete',
  'personas_automation_test', 'personas_generate_avatar', 'personas_ai_team',
]);

async function callTool(name, args) {
  if (!WRITE && WRITE_TOOLS.has(name))
    throw new Error('Инструмент управления персонами недоступен в этом ходе. Попроси пользователя явно сформулировать запрос на управление командой (создать/изменить/настроить персону) — тогда инструменты появятся.');
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
      // Контекст проекта: handle резолвится среди глобальных + проектных этого проекта
      // (две проектные «маши» из разных проектов не путаются)
      if (PROJECT_ID) body.projectId = PROJECT_ID;
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
