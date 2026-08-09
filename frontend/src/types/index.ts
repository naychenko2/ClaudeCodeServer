import type { Mode } from '../lib/modes';
export type { Mode };

export interface PermissionRule {
  pattern: string;
  action: 'allow' | 'deny';
}

// Тег из реестра общих тегов проекта (PUT /api/projects/{id}/tags перезаписывает целиком;
// order нормализуется бэком по позиции массива). color — hex из палитры-данных (GROUP_COLORS),
// отсутствует = чип красится accent'ом.
export interface ProjectTag {
  name: string;
  order: number;
  color?: string;
}

export interface Project {
  id: string;
  name: string;
  rootPath: string;
  relativePath?: string;
  createdAt: string;
  updatedAt: string;
  groupId?: string;          // группа проекта; отсутствует = без группы
  sessionCount?: number;
  difyDatasetId?: string;
  systemPrompt?: string;
  showHiddenFiles?: boolean;
  permissionRules?: PermissionRule[];
  boardColumns?: BoardColumn[];   // кастомные колонки Kanban-доски; отсутствует = дефолтные 3
  builtInSystemPrompt?: string;
  icon?: ProjectIcon;             // иконка проекта: инициалы+цвет или картинка
  tagRegistry?: ProjectTag[];     // реестр общих тегов проекта (имя, порядок, цвет)
  // Ключи серверов личного MCP-реестра, ВКЛЮЧЁННЫХ в этом проекте (allow-list):
  // сервер едет в ход только там, где явно включён. Пусто/нет — не включён никто
  mcpServersOn?: string[] | null;
  // Персона-«руководитель проекта» (фича default-personas-onboarding): дефолт для новых
  // чатов проекта; null/отсутствует — онбординг проекта ещё не пройден (гейт в WorkspacePage)
  defaultPersonaId?: string | null;
  // Живая сессия онбординга проекта — для резюма прерванного интервью
  onboardingSessionId?: string | null;
}

// Иконка проекта (по образцу PersonaAvatar): инициалы на цветном фоне или картинка
// (сгенерированная/загруженная). color — ключ палитры AGENT_COLORS; imageFile — имя файла
// в хранилище проекта; originalFile/crop — оригинал и параметры кропа (для «Перекроить»).
export interface ProjectIcon {
  kind: 'initials' | 'image';
  color?: string;
  imageFile?: string;
  originalFile?: string | null;
  crop?: AvatarCropStateDto | null;
}

// Колонка Kanban-доски проекта. category — семантическая категория статуса
// (за ней recurrence/календарь/Claude/MCP); несколько колонок могут делить категорию.
export interface BoardColumn {
  id: string;
  name: string;
  category: TaskStatus;   // 'todo' | 'inProgress' | 'done'
  color?: string;
}

// Code Graph — логическая карта типов проекта и рёбер между ними
// (GET /api/projects/{id}/code-graph). Узлы — типы, рёбра — связи типов,
// godNodes — точки перегруза (кандидаты на разбиение).
export type CodeGraphNodeKind = 'Class' | 'Interface' | 'Struct' | 'Enum';
export type CodeGraphRelation = 'Calls' | 'Implements' | 'References';
export type CodeGraphConfidence = 'Extracted' | 'Inferred';

export interface CodeGraphNode {
  id: string;
  label: string;
  fullyQualifiedName: string;
  sourceFile: string;
  sourceLocation: string;   // "line:col"
  kind: CodeGraphNodeKind;
}

export interface CodeGraphEdge {
  source: string;           // id узла-источника
  target: string;           // id узла-приёмника
  relation: CodeGraphRelation;
  confidence: CodeGraphConfidence;
}

export interface CodeGraphMetadata {
  builtAt?: string | null;  // ISO времени сборки
  nodeCount: number;
  edgeCount: number;
  fileCount: number;
  isStale: boolean;         // код изменился после сборки — граф может отставать
}

export interface CodeGraph {
  nodes: CodeGraphNode[];
  edges: CodeGraphEdge[];
  godNodes: string[];
  metadata: CodeGraphMetadata;
}

// ─── Документация проекта (панель «Документы»: README.md + docs/**) ───────────────
// Класс ссылки: doc — другой документ области (навигация внутри панели),
// repo — файл проекта вне области, обычно код (открывается в центре),
// external — http/https/mailto.
export type DocLinkKind = 'doc' | 'repo' | 'external';

export interface DocLink {
  target: string;           // путь от корня проекта (doc/repo) либо URL (external)
  anchor: string | null;    // слаг якоря после «#»
  kind: DocLinkKind;
  text: string;             // подпись ссылки
}

// Слаг считается от текста заголовка без markdown-разметки — тем же алгоритмом,
// что и на фронте (lib/docsLinks.ts): по нему панель находит раздел в DOM
export interface DocHeading {
  level: number;
  text: string;
  slug: string;
}

export interface DocEntry {
  path: string;             // путь от корня проекта с прямыми слэшами
  title: string;            // первый H1, иначе имя файла
  modified: string;         // ISO
  size: number;
  headings: DocHeading[];
  binary?: boolean;         // файл без текста — открывается только в центре
  // Папка, страницей которой служит документ («docs/decisions.md» при наличии
  // «docs/decisions/»). Пара «страница + папка» пришла из code wiki: раздел там
  // существует только так — файл несёт содержание, одноимённая папка даёт детей
  sectionFolder?: string | null;
  // Тип документа из схемы .docs (см. DocTypeSchema); null — тип не описан
  type?: string | null;
  // Свойства шапки; приходят только у типизированных документов — индекс перезапрашивается
  // на каждое изменение файлов, и возить шапки всех подряд ради метки незачем
  properties?: DocProperty[] | null;
}

export interface DocBacklink {
  path: string;
  title: string;
  anchor: string | null;
}

export interface DocDetail {
  path: string;
  title: string;
  content: string;          // исходный markdown; у бинарного пусто
  links: DocLink[];
  backlinks: DocBacklink[];
  binary?: boolean;         // pdf/visio/картинка/звук — превью не покажет, только центр
  type?: string | null;
  properties?: DocProperty[] | null;   // в отличие от индекса — у ЛЮБОГО документа
  // Отрезок content, занятый шапкой свойств: по нему превью вырезает эти строки, чтобы
  // они не задвоились с блоком «Свойства». Считает разбор — только он точно знает границы
  propsRange?: { start: number; end: number } | null;
}

export interface DocSearchHit {
  path: string;
  title: string;
  slug: string | null;      // якорь ближайшего заголовка над совпадением
  snippet: string;
}

// Папка-кандидат в область документации: сколько документов внутри (со вложенными,
// по расширениям области) и есть ли она на диске (выбранная, но удалённая остаётся
// в списке с exists=false)
export interface DocFolderCandidate {
  path: string;
  count: number;
  exists: boolean;
}

// Файл-кандидат из корня проекта (тот же приём с выбранными, которых уже нет)
export interface DocRootFileCandidate {
  name: string;
  exists: boolean;
}

// Область документации: три независимые оси. Пустой список любой из них — «ничего отсюда»
export interface DocsScope {
  folders: string[];
  rootFiles: string[];       // имена файлов в корне проекта, без путей
  types: string[];           // ключи групп типов: "markdown", "pdf", "visio"…
  home?: string | null;      // документ «Начала»; null — авто (README корня)
}

// Документ области как вариант выбора начального
export interface DocOption {
  path: string;
  title: string;
}

// Группа типов ФАЙЛОВ. text=false — файл без текста (pdf, visio, картинка, звук):
// он числится в списке, но открывается только в центральной области.
// НЕ путать с DocTypeSchema ниже — там смысловой тип документа (ADR, спека, runbook)
export interface DocTypeGroup {
  key: string;
  title: string;
  extensions: string[];
  text: boolean;
}

// ─── Типы документов и их свойства ────────────────────────────────────────

// Свойство документа как оно записано в его шапке: «**Статус:** Принято».
// link заполнен, когда значение содержит ссылку на документ — путь от корня проекта
export interface DocProperty {
  key: string;
  value: string;
  link?: string | null;
}

// Вид свойства = вид редактора в панели
export type DocPropertyKind = 'choice' | 'date' | 'text' | 'docLink';

// Цвет значения — имя РОЛИ дизайн-системы, а не цвета: зелёного как такового в токенах
// нет, есть --c-success. Роль кладёт на токен lib/docsTypes.ts
export type DocPropertyColor = 'gray' | 'accent' | 'success' | 'warning' | 'danger' | 'info' | 'plan';

export interface DocPropertyChoice {
  value: string;
  color: DocPropertyColor;
  title?: string | null;
}

export interface DocPropertyDef {
  key: string;                        // ровно то, что стоит в файле перед двоеточием
  kind: DocPropertyKind;
  title?: string | null;
  choices?: DocPropertyChoice[] | null;
  autoUpdate?: boolean;               // «дата смены»: только у kind='date'
  required?: boolean;
}

// Смысловой тип документа: папки + необязательная маска имени файла.
// НЕ путать с DocTypeGroup выше — там типы ФАЙЛОВ (markdown/pdf/visio)
export interface DocTypeSchema {
  id: string;                         // стабильный ключ; переименование заголовка его не меняет
  title: string;
  folders: string[];
  match?: string | null;              // маска имени: «ADR-*.md»
  badgeProperty?: string | null;      // что показывать плашкой в шапке и точкой в дереве
  properties: DocPropertyDef[];
}

// Настройка области: что выбрано, что можно выбрать и что было бы по умолчанию
export interface DocsScopeInfo {
  selected: DocsScope;
  folderCandidates: DocFolderCandidate[];
  rootFileCandidates: DocRootFileCandidate[];
  typeGroups: DocTypeGroup[];      // всё, что продукт умеет открыть
  defaults: DocsScope;
  documents: DocOption[];          // документы области — варианты для «Начала»
  home: string | null;             // что сейчас работает «Началом» (выбор или README)
  // Откуда взята область: 'file' — версионируемый .docs в корне репозитория (общий для
  // всех, кто его открыл), 'project' — настройка продукта, своя у каждого владельца
  scopeSource: 'file' | 'project';
  // Заполнено, когда .docs есть, но не разобран: область при этом взята из настройки
  // проекта, и без объяснения расхождение «в файле одно, в панели другое» необъяснимо
  scopeFileError?: string | null;
  // Схема типов документов из .docs. Едет вместе с областью: панель запрашивает настройку
  // при загрузке, второй запрос ради того же файла был бы лишним
  docTypes?: DocTypeSchema[] | null;
  // Секция docTypes есть, но не разобрана: область при этом продолжает работать
  docTypesError?: string | null;
  // Роли, которыми можно красить значения выбора (палитра редактора типов)
  propertyColors?: DocPropertyColor[] | null;
}

// --- История решений (change-dossiers, этап 1) ---
// Запись у файла/символа/коммита: «зачем менялось, что решили, что отвергли, какие
// грабли» — AI-выжимка из чата/задачи, привязанная к коммиту. Контракт REST —
// GET /api/projects/{id}/dossiers?file=|symbol=|commit= (ADR-004 §4/§8).
export type DossierStatus = 'active' | 'degraded' | 'archived';

export interface DossierEntry {
  id: string;
  commitSha: string;
  commitSubject: string;
  committedAt: string;        // ISO
  sessionId: string | null;   // чат-источник — может протухнуть (sessionId остаётся, см. linksStale)
  taskId: string | null;      // задача-источник — может протухнуть
  personaId: string | null;   // null — коммит человека-владельца, иначе персона
  files: string[];
  symbols: string[];
  why: string;
  decisions: string[];
  rejected: string[];
  pitfalls: string[];
  invariants: string[];
  // active — код почти не менялся с записи; degraded — файл заметно переписан
  // (нюанс, не ошибка); archived — символ исчез из графа (показывается только по запросу)
  status: DossierStatus;
  // Выжимка не собралась — сохранён только факт изменения, why не показываем
  summaryFailed: boolean;
  // Чат/задача, на которые указывают sessionId/taskId, больше не существуют —
  // соответствующие ссылки гасятся текстом вместо кнопки
  linksStale: boolean;
}

// Элемент доски агентов (диспетчерская: GET /api/board/agents)
export interface BoardItem {
  taskId: string;
  title: string;
  projectId?: string;
  sessionId?: string;
  column: 'queue' | 'working' | 'waiting' | 'done';
  sessionStatus: string;
  personaId?: string;
  currentToolName?: string;
  startedAt?: string;
  permissionPending: boolean;
}

// Часть эффективного системного промпта (в порядке отправки в claude)
export interface SystemPromptPart {
  kind: 'builtin' | 'user' | 'auto';
  content: string;
}

// Группа проектов на вкладке «Проекты»
export interface ProjectGroup {
  id: string;
  name: string;
  color: string;   // hex из палитры GROUP_COLORS
  order: number;
}

// --- Задачи ---

// Уровень модели (слот «сильная/средняя/слабая») у задачи и персоны. На проводе — имя
// слота; '' в DTO = сбросить уровень, undefined = не менять. Какая модель стоит за слотом,
// решает пара «личный слот пользователя → глобальный» (lib/modelTiers.ts).
export type ModelTierValue = 'strong' | 'medium' | 'weak';

// Значения enum-ов приходят с бэка в camelCase (JsonStringEnumConverter)
export type TaskStatus = 'todo' | 'inProgress' | 'done';
export type TaskPriority = 'urgent' | 'high' | 'medium' | 'low';
export type TaskAssignee = 'me' | 'claude';

// Повторение задачи. weekdays — ISO-дни (1=Пн … 7=Вс), только для weekly.
// type 'none' — wire-сентинел в UpdateTaskDto: убрать повторение
export type TaskRecurrenceType = 'none' | 'daily' | 'weekly' | 'monthly' | 'yearly';
export interface TaskRecurrence {
  type: TaskRecurrenceType;
  interval: number;          // каждые N периодов
  weekdays?: number[];
  until?: string;            // YYYY-MM-DD включительно
}

export interface TaskSubtask {
  id: string;
  title: string;
  isDone: boolean;
}

export interface Task {
  id: string;
  // Отсутствует у личной задачи (вне проекта)
  projectId?: string;
  ownerId?: string;
  title: string;
  description: string;      // markdown
  status: TaskStatus;
  columnId?: string;         // колонка доски проекта; отсутствует = дефолтная колонка категории
  priority: TaskPriority;
  dueDate?: string;          // YYYY-MM-DD
  dueTime?: string;          // HH:MM
  reminderMinutes?: number;  // офсет напоминания до срока в минутах (0 = в момент срока)
  reminderSentAt?: string;   // UTC-отметка отправленного напоминания
  assignee?: TaskAssignee;
  recurrence?: TaskRecurrence;
  seriesId?: string;         // общий id серии регулярной задачи
  linkedSessionId?: string;
  personaId?: string;        // исполнение от лица персоны (assignee=claude)
  // Уровень модели исполнителя (слот strong/medium/weak); отсутствует — не задан:
  // модель берётся от персоны-исполнителя и назначения места «Исполнитель задач»
  modelTier?: ModelTierValue;
  // Время жизни чата исполнения (мин от последней активности); undefined/null — бессрочно
  executionExpiresAfterMinutes?: number | null;
  claudeStartedAt?: string;  // отметка запуска Claude-исполнителя
  claudeResult?: 'success' | 'error';  // итог последнего запуска (null — выполняется/не запускалась)
  resultMarkdown?: string;            // Markdown-итог выполнения (прикрепляет исполнитель через MCP)
  linkedFiles: string[];
  subtasks: TaskSubtask[];
  labels: string[];
  // Связь с чекбоксом заметки (флаг notes-task-sync)
  sourceNoteId?: string;
  sourceNoteLine?: number;
  order: number;             // порядок карточки на Kanban-доске (ручная сортировка)
  createdAt: string;
  updatedAt: string;
  completedAt?: string;      // дата+время завершения (статус стал done); null — не завершена
  // UI-проекция повторяющейся задачи в календаре (не приходит с бэка):
  // occurrenceOf — id реального экземпляра серии, который надо открыть по клику;
  // virtual — признак вычисленного будущего повтора (реально существует только один экземпляр)
  occurrenceOf?: string;
  virtual?: boolean;
}

// Элемент единой выдачи поиска (флаг unified-search)
export interface SearchHit {
  type: 'note' | 'task';
  id: string;
  title: string;
  context: string;   // источник заметки / контекст задачи
  snippet: string;
  score?: number | null;
  url: string;       // hash-диплинк
}

// Кандидат в задачу, извлечённый из чата (флаг chat-extract-tasks). Ещё не создан.
export interface ExtractedTaskCandidate {
  title: string;
  due?: string | null;
  priority?: TaskPriority | null;
}
export interface ExtractTasksResponse {
  projectId?: string | null;
  tasks: ExtractedTaskCandidate[];
}

// Строка-чекбокс заметки + связанная задача (флаг notes-task-sync)
export interface NoteTask {
  line: number;
  text: string;
  done: boolean;
  due?: string | null;
  taskId?: string | null;
  taskStatus?: string | null;
}

export interface CreateTaskDto {
  // Клиентский id для офлайн-создания (идемпотентный replay на сервере). Обычно не задаётся.
  id?: string;
  title: string;
  description?: string;
  status?: TaskStatus;
  columnId?: string;         // колонка доски проекта при создании (быстрое добавление)
  priority?: TaskPriority;
  dueDate?: string;
  dueTime?: string;
  reminderMinutes?: number;
  assignee?: TaskAssignee;
  recurrence?: TaskRecurrence;
  linkedSessionId?: string;
  personaId?: string;        // исполнение от лица персоны
  // Уровень модели исполнителя; не указано — не задан (модель по персоне и месту)
  modelTier?: ModelTierValue;
  // Не указано — дефолт 1440 (сутки); отрицательное — бессрочно; N>=0 — TTL в минутах.
  // Имеет смысл только при исполнителе Claude/персона.
  executionExpiresAfterMinutes?: number;
  resultMarkdown?: string;
  linkedFiles?: string[];
  subtasks?: { title: string }[];
  labels?: string[];
}

// Пустая строка в dueDate/dueTime/linkedSessionId = очистить поле, undefined = не менять
export interface UpdateTaskDto {
  title?: string;
  description?: string;
  status?: TaskStatus;
  priority?: TaskPriority;
  dueDate?: string;
  dueTime?: string;
  // Отрицательное значение = убрать напоминание, undefined = не менять
  reminderMinutes?: number;
  assignee?: TaskAssignee;
  // type 'none' = убрать повторение, undefined = не менять
  recurrence?: TaskRecurrence;
  linkedSessionId?: string;
  // Персона-исполнитель: '' = убрать, undefined = не менять
  personaId?: string;
  // Уровень модели исполнителя: '' = сбросить, undefined = не менять
  modelTier?: ModelTierValue | '';
  // Время жизни чата исполнения: отрицательное = бессрочно, undefined = не менять, N>=0 = TTL
  executionExpiresAfterMinutes?: number;
  resultMarkdown?: string;    // '' = очистить, undefined = не менять
  linkedFiles?: string[];
  subtasks?: TaskSubtask[];
  labels?: string[];
  order?: number;            // порядок карточки на доске (drag); undefined = не менять
  columnId?: string;         // колонка доски проекта; undefined = не менять, '' = сброс на дефолт
  projectId?: string;        // смена проекта: guid = привязать, '' = сделать личной, undefined = не менять
}

// Тип доступа к Claude: подписка (стоимость ≈ API-эквивалент) или оплата по API-ключу (реальная цена)
export type ClaudeBilling = 'subscription' | 'api';

export interface AppSettings {
  defaultProjectsPath: string;
  claudeBilling?: ClaudeBilling;
  // Присылать ли утренний бриф по расписанию (настройка инстанса, тумблер в «Фоновых задачах»)
  dailyBriefingEnabled?: boolean;
  // Слоты «сильная/средняя/слабая» — три именованные модели инстанса (id модели любого
  // провайдера, напр. «claude-opus-5» / «glm-5.2» / «haiku»). На них ссылаются назначения
  // мест (новый чат, чат персоны, фоновые действия…) значением "tier:strong|medium|weak".
  // PATCH /api/settings: поле отсутствует = не трогать, "" = очистка слота (решает CLI).
  modelTierStrong?: string | null;
  modelTierMedium?: string | null;
  modelTierWeak?: string | null;
}

// Определение фич-флага из реестра (приходит с бэка для рендера тумблеров)
export interface FeatureFlagDefinition {
  key: string;
  title: string;
  description: string;
  default: boolean;
  stage: 'dev' | 'beta' | 'stable';
}

export interface Session {
  id: string;
  // Отсутствует у чатов вне проекта (project-less)
  projectId?: string;
  // Привязка чата к персоне, если он ведётся от её лица.
  // В групповом чате — активный спикер (входит в participants)
  personaId?: string;
  // Origin автоматизации: id правила PersonaAutomationRule, чат которого создан движком
  // проактивности (отсутствие — обычный чат). Для фильтрации авто-чатов и трассировки.
  automationRuleId?: string | null;
  // Участники группового чата (2-4 id персон; первый — ведущая). Отсутствует у обычного чата
  participants?: string[] | null;
  // Владелец чата вне проекта
  ownerId?: string;
  // Закреплён в списке чатов
  isPinned?: boolean;
  claudeSessionId?: string;
  mode: Mode;
  status: 'starting' | 'working' | 'active' | 'waiting' | 'orphaned' | 'finished' | 'error';
  lastMessage?: string;
  messageCount: number;
  createdAt: string;
  updatedAt: string;
  name?: string;
  // Тема чата — ИМЯ компонента lucide-react (PascalCase: Cat, Bug, User). Фронт рисует
  // иконку через icons[topic] (lib/lucideIcon.ts). Отсутствует/незнакомое — значка нет
  topic?: string | null;
  model?: string;
  // "claude" | "deepseek" | "glm" | ключ из подписок ClaudeSubscriptionPool
  provider?: string;
  effort?: string;
  agentName?: string;
  // Временный чат: авто-удаление через N минут после последней активности (updatedAt)
  expiresAfterMinutes?: number | null;
  // Момент установки срока: отсчёт идёт от него, если он позже последней активности
  // (иначе короткий срок на давно неактивном чате означал бы удаление сразу)
  expiryAnchor?: string | null;
  // Чат заглушён: браузерные уведомления по нему не показываются
  notificationsMuted?: boolean;
  // Цикл «до готово» (флаг work-loop); null/отсутствует — цикл выключен
  workLoop?: { promise: string; iteration: number; maxIterations: number; phase: 'working' | 'verifying' } | null;
  // Режим «Командная реализация»; null/отсутствует — режим выключен
  teamImplement?: SessionTeamImplement | null;
  // Отдельное git worktree чата: рабочая папка сессии вместо корня проекта.
  // null/отсутствует — чат в основном дереве. Только у проектных чатов.
  worktreePath?: string | null;
  worktreeBranch?: string | null;
  // Сессия-исполнитель задачи (создана TaskExecutionService)
  taskExecution?: boolean;
  // Задача-владелец чата-исполнителя (для отображения контекста «в рамках какой задачи»)
  taskId?: string | null;
  // Чат-родитель в списке — вычисляется на бэке: ручная группировка (drag-and-drop)
  // побеждает авто-связь по задаче (TaskId → Task.SourceSessionId).
  // null — корневой чат: обычный, вынесенный вручную либо с удалённой задачей
  parentSessionId?: string | null;
  // Тип происхождения чата — производный от taskId/automationRuleId на бэке
  origin: 'manual' | 'task' | 'automation';
  // Признак «Готово» для фильтра чатов: у чата есть taskId и связанная задача в статусе Done.
  // Пробрасывается бэком в Session/HomeSessionDto; в потоковые session_started/status_changed
  // НЕ кладётся — обновляй по событию task_changed (приходит полный Task со status)
  taskDone?: boolean;
  // Общие теги чата (имена из Project.tagRegistry; тег без записи в реестре возможен —
  // показывается секцией-сиротой). Меняется через PUT sessions/{sid} (поле tags)
  tags?: string[];
  // Opt-out «не сохранять решения из этого чата» (ADR-004 §6): true — записи из этого
  // чата не попадают в историю решений и не уходят в репозиторий. Только у проектных
  // сессий. Меняется через PUT sessions/{sid} (поле excludeFromDossiers)
  excludeFromDossiers?: boolean;
  // Онбординг-сессия (фича default-personas-onboarding): "user" — гейт первого входа,
  // "project" — гейт проекта; отсутствует у обычных чатов. По признаку фронт прячет
  // командные механики («Обсудить с командой») — команды в интервью ещё нет
  onboardingKind?: 'user' | 'project' | null;
}

// Строка сводки дашборда «Домой» (GET /api/home/summary): сессия + имя проекта
export interface HomeSessionInfo {
  id: string;
  projectId?: string | null;
  projectName?: string | null;
  name?: string | null;
  status: Session['status'];
  lastMessage?: string | null;
  personaId?: string | null;
  taskId?: string | null;
  taskDone?: boolean;
  messageCount: number;
  updatedAt: string;
  // Поля фильтрации: точка активности проекта считает unread только по чатам,
  // видимым в фильтре этого проекта (matchChatFilter). Без них скрытое фильтром
  // неприбытие светилось бы в рельсе зря
  origin: Session['origin'];
  isPinned?: boolean;
  tags?: string[];
  participants?: string[] | null;
  expiresAfterMinutes?: number | null;
}

export interface HomeSummaryResponse {
  // Живые сессии (starting/working/waiting) по всем проектам + чатам
  active: HomeSessionInfo[];
  // Недавние завершённые/простаивающие (кроме orphaned), новые сверху
  recent: HomeSessionInfo[];
}

export interface FileEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size?: number;
  modified: string;
  isModified: boolean;
  isNew?: boolean;
  // Состояние синхронизации для офлайна: помечен сам / по наследству от папки / нет
  synced?: 'direct' | 'inherited' | null;
}

// === Git в разделе «Файлы» ===

// Одно изменение файла: status — односимвольный код git (M/A/D/R/?/U);
// oldPath заполняется только для переименований (R)
export interface GitFileChange {
  path: string;
  status: string;
  oldPath?: string | null;
  // Статистика строк (git numstat): +added / −deleted. null — бинарный/не считалось.
  added?: number | null;
  deleted?: number | null;
}

// Статус рабочего дерева проекта (GET /api/projects/{id}/git/status)
export interface GitStatus {
  isRepo: boolean;
  branch: string | null;
  upstream: string | null;
  ahead: number;
  behind: number;
  detached: boolean;
  staged: GitFileChange[];
  unstaged: GitFileChange[];
  untracked: GitFileChange[];
  // Проект открыт как linked git worktree (UI показывает имя папки worktree вместо ветки)
  isWorktree: boolean;
}

// Ветка репозитория: current — текущая (HEAD)
export interface GitBranchInfo {
  name: string;
  current: boolean;
  upstream: string | null;
}

// Настройка промпта AI-описания коммита (GET/PUT /api/projects/{id}/git/commit-prompt)
export interface GitCommitPromptInfo {
  global: string | null;          // глобальный (per-user) промпт
  projectOverride: string | null; // проектный override
  useProject: boolean;            // активен ли проектный override
  effective: string;              // что реально применится к генерации
  default: string;                // дефолтные правила стиля (для плейсхолдера)
}

// Запись истории коммитов (GET /api/projects/{id}/git/log)
export interface GitLogEntry {
  sha: string;
  shortSha: string;
  author: string;
  email: string;
  date: string;
  subject: string;
}

// Детали коммита (GET /api/projects/{id}/git/commits/{sha})
export interface GitCommitDetail {
  sha: string;
  shortSha: string;
  author: string;
  email: string;
  date: string;
  subject: string;
  body: string;
  files: GitFileChange[];
}

// Запись стэша (GET /api/projects/{id}/git/stash)
export interface GitStashEntry {
  index: number;
  message: string;
  date: string;
}

// Строка blame файла (GET /api/projects/{id}/git/blame?path=)
export interface GitBlameLine {
  line: number;
  sha: string;
  shortSha: string;
  author: string;
  date: string;
  content: string;
}

// Удалённый репозиторий и авто-коммит (GET /api/projects/{id}/git/remote)
export interface GitRemoteInfo {
  serverEnabled: boolean;
  remoteUrl: string | null;
  htmlUrl: string | null;
  autoCommit: boolean;
  autoPush: boolean;
}

export interface SyncMark {
  path: string;
  isDirectory: boolean;
}

export interface WorkflowAgentInfo {
  id: string;
  prompt: string;
  summary?: string;
  tools?: { name: string; count: number }[];
  files?: string[];
  isDone?: boolean;
  // Тип сабагента из agent-*.meta.json (agentType вызова agent()) — совпал с handle
  // персоны → рисуем карточку персоны-консультанта вместо безликой строки
  agentType?: string;
}

// Блок таймлайна workflow-агента (полный поток из транскрипта, лениво по REST):
// text | thinking | tool_use | structured (итог StructuredOutput, text = pretty-json,
// рендерится свёрнутым). tool_use несёт полный input и результат —
// рендерится тем же ToolUseView, что и обычный чат.
export interface WorkflowAgentBlock {
  kind: 'text' | 'thinking' | 'tool_use' | 'structured';
  text?: string;
  toolName?: string;
  toolId?: string;
  toolInput?: unknown;
  toolResult?: string;
  isError?: boolean;
}

// Запись истории kind='workflow_progress' — последний снапшот прогресса workflow
// (переживает F5/рестарт сервера). Отдельным элементом ленты НЕ рендерится:
// normalizeHistory вмерживает её в tool_use Workflow по toolUseId.
// aborted=true — восстановлено после рестарта: ватчеров нет, агенты не завершатся.
export interface StoredWorkflowProgress {
  kind: 'workflow_progress';
  toolUseId: string;
  isDone: boolean;
  aborted?: boolean;
  agents?: WorkflowAgentInfo[];
}

// WebSocket сообщения от сервера — sessionId присутствует во всех типах
export type ServerMessage = { sessionId: string } & (
  // turnWorktree — фактическая рабочая папка ЭТОГО хода, если агент внутри хода ушёл
  // в свой git worktree (инструмент EnterWorktree), минуя Session.worktreePath.
  // null/отсутствует — ход идёт там, куда его отправил сервер
  | { type: 'session_started'; claudeSessionId: string; isResume: boolean; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[]; turnWorktree?: { path: string; name: string } | null }
  | { type: 'text_delta'; text: string }
  | { type: 'user_message'; text: string; attachedPaths?: string[]; senderPersonaId?: string; auto?: boolean; senderOrigin?: string; senderChatName?: string; staffNote?: string; timestamp?: number }
  // Гостевая реплика персоны без агентского хода (0 токенов) — доклад о завершении
  // делегированной задачи (модель Z); маркер доклада распознаётся на рендере (см.
  // lib/delegationReport.ts). Живой аналог StoredTextMessage.PersonaId из истории.
  | { type: 'guest_text'; text: string; personaId: string; timestamp?: number }
  | { type: 'thinking_delta'; text: string }
  // Текст/thinking сабагента (Task/Agent) — целыми блоками, с привязкой к родительскому tool_use
  | { type: 'agent_text'; parentToolUseId: string; text: string }
  | { type: 'agent_thinking'; parentToolUseId: string; text: string }
  | { type: 'tool_use'; id: string; name: string; input: unknown; parentToolUseId?: string }
  | { type: 'tool_input_delta'; toolUseId: string; partialJson: string }
  | { type: 'tool_result'; toolUseId: string; content: string; isError: boolean }
  // Фоновые агенты (Agent run_in_background / Workflow) реально завершились — единственный
  // достоверный сигнал «ответ готов» (по task-notification CLI). aborted=true — агенты
  // умерли вместе с процессом (Стоп/несовместимый ход), не доработав
  | { type: 'bg_agent_done'; toolUseIds: string[]; aborted?: boolean }
  | { type: 'permission_request'; requestId: string; toolName: string; toolInput: unknown }
  | { type: 'ask_question'; toolUseId: string; input: unknown }
  | { type: 'plan_review'; requestId: string; plan: string }
  // external — правку сделали не Edit/Write этого чата (человек в IDE, форматтер);
  // фронт снимает кнопку «Откатить» и помечает карточку «Изменение вне чата»
  | { type: 'file_changed'; path: string; added: number; removed: number; external?: boolean }
  // contextTokens — размер контекста последнего запроса хода (оценка заполнения окна).
  // usage для этого не годится: он суммирует все запросы хода, включая сабагентов.
  // usageModel — фактическая модель хода (её же бэк проставляет постам в истории):
  // на этом событии лента клеит модель постам текущего хода
  | { type: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[]; contextTokens?: number; usageModel?: string }
  | { type: 'fal_cost'; requestId: string; endpointId?: string; costUsd: number; outputUnits?: number; unitPrice?: number }
  // Завершённая генерация glif: счётчик + кредиты (если billing доехал в payload). Дедуп по jobId.
  | { type: 'glif_cost'; jobId: string; outputType?: string; mediaCount: number; credits?: number; model?: string }
  | { type: 'error'; text: string }
  | { type: 'rate_limit'; limitType: string; resetsAt?: string; status?: string; utilization?: number; isUsingOverage?: boolean; overageStatus?: string; overageResetsAt?: string }
  | { type: 'compact_boundary'; trigger: string; preTokens?: number; postTokens?: number }
  | { type: 'compact_status'; status?: string; compactResult?: string; compactError?: string }
  | { type: 'truncated' }
  | { type: 'redacted_thinking' }
  | { type: 'exited' }
  | { type: 'status_changed'; status: string; lastMessage?: string; messageCount?: number }
  | { type: 'chat_deleted' }
  | { type: 'chat_renamed'; name: string; topic?: string | null }
  | { type: 'workflow_progress'; toolUseId: string; agents: WorkflowAgentInfo[]; isDone: boolean }
  | { type: 'task_changed'; action: 'created' | 'updated' | 'deleted'; task: Task }
  | { type: 'notes_changed'; action: 'created' | 'updated' | 'deleted'; noteId?: string }
  | { type: 'knowledge_changed'; action: string; datasetId?: string }
  | { type: 'personas_changed'; action: 'created' | 'updated' | 'deleted' | 'memory' | 'default'; personaId?: string }
  // Онбординг завершён (фича default-personas-onboarding): дефолт-персона назначена из
  // онбординг-сессии. Гейт снимается НЕ по этому событию mid-turn, а по концу хода
  // (result) или кнопке «Перейти в систему» — событие лишь помечает завершение
  | { type: 'onboarding_completed'; kind: 'user' | 'project'; personaId: string; projectId?: string | null }
  | { type: 'team_memory_changed'; action: 'added' | 'updated' | 'removed'; projectId: string; entryId?: string }
  // Изменение git-статуса проекта (commit/stage/checkout/…) — клиент перезапрашивает статус
  | { type: 'git_status_changed'; projectId: string }
  | { type: 'git_turn_commit'; sessionId: string; projectId: string; sha: string; subject: string }
  | { type: 'speaker_changed'; personaId: string; label: string }
  // Чат переключён на другой аккаунт/провайдер: auto — тихий фейловер пула подписок
  // (в ленту не попадает); label — разделитель «Продолжено на …» явной миграции.
  // reason — классификация причины фолбэка с бэкенда (TurnErrorClassifier.WireName:
  // rate_limit|usage_limit|provider_error|unreachable|context_overflow) для подсказки «Ответила … — … была
  // недоступна» (см. providerSwitchReasonLabel в lib/providerLimit.ts)
  | { type: 'provider_switched'; provider: string; model?: string; label?: string; auto?: boolean; reason?: string }
  // Лимит подписки исчерпан, в пуле переключиться некуда — предложение продолжить
  // чат на стороннем провайдере (карточка с кнопками)
  | { type: 'provider_limit'; resetsAt?: string; providers: ProviderFallbackOption[] }
  | { type: 'work_loop'; active: boolean; iteration: number; maxIterations: number; phase: string | null }
  // Явная остановка цикла «до готово» в ленту — человекочитаемый текст (лимит/ошибка/ручной
  // стоп), готовый с сервера. reason ∈ limit|error|manual (см. WorkLoopStoppedMessage)
  | { type: 'work_loop_stopped'; reason: string; text: string }
  // Режим «Командная реализация»: приходит при каждом изменении (вкл/стадия/волна/авто/стоп)
  // modeLocked/planVersion — Э8: план-режим навязан чату (интервью/планирование),
  // версия текущего плана итерации
  | { type: 'team_implement'; active: boolean; stage: TeamImplementStage | null; waveNumber: number; autoWaves: boolean; coordinatorPersonaId: string | null; plannerPersonaId: string | null; executorPersonaIds: string[] | null; budget: TeamImplementBudget | null; planCardId: string | null; plannedWaves?: number; coordinatorNoCode?: boolean; stopped?: boolean; modeLocked?: boolean; planVersion?: number }
  // Карточка плана командной реализации. Переиздаётся при каждой правке (смена исполнителя,
  // решение человека) с тем же planId — клиент обновляет карточку, а не плодит дубли
  | { type: 'team_plan'; planId: string; plan: TeamPlan; resolved: boolean; approved: boolean | null; supersededBy?: number | null }
  // Карточка остановки командной реализации: причина + кнопки решения. Переиздаётся при
  // ответе человека (resolved=true) с тем же escalationId — клиент обновляет карточку.
  // Поля плоские (в истории та же карточка лежит вложенным объектом escalation)
  // personaId — автор карточки (Э8, координатор на момент публикации)
  | { type: 'team_escalation'; escalationId: string; kind: TeamEscalationKind; title: string; details: string; actions: TeamEscalationAction[]; taskId: string | null; wave: number; resolved: boolean; chosenActionId: string | null; personaId?: string | null }
  // Жизненный цикл вызова планировщика (не путать с team_implement — тот про стадию режима).
  // Транзитное: в историю не пишется, после рестарта не восстанавливается — карточка плана
  // (team_plan) или отказа (team_escalation) уже несут итог. start=true — планировщик запущен;
  // start=false — закончил: success=true → subtaskCount/waveCount/elapsedMs, success=false →
  // failure (готовый текст причины, тот же, что уйдёт в title карточки отказа следом)
  | { type: 'team_planning'; start: boolean; success: boolean; subtaskCount: number; waveCount: number; elapsedMs: number; route: string | null; failure: string | null; promptChars: number; responseChars: number }
  | { type: 'preview_status'; status: string; port?: number; error?: string; serviceId?: string }
  // Вывод дев-сервера — приходит только подписчикам группы конкретного сервиса
  // (JoinPreviewLog), а не всем вкладкам пользователя. data — накопленное за тик
  // (~100 мс) сразу куском: построчная рассылка захлёбывалась на сборках
  | { type: 'preview_log'; serviceId: string; data: string }
  | { type: 'notification'; title: string; body: string; url?: string; kind: 'reminder' | 'claude' | 'info' | 'success' | 'meeting'; notificationId?: string; notifType?: string; projectId?: string; sessionId?: string; taskId?: string; source?: string; tag?: string; personaId?: string; personaName?: string; personaRole?: string; personaColor?: string; personaHasAvatar?: boolean; projectName?: string }
  | { type: 'recall_manifest'; items: RecallItem[] }
  // Полный снимок очереди сообщений занятой сессии (постановка/отмена/доставка).
  // kind: 'user' — сообщение человека из «честной очереди» (рисуется карточкой «Вы» с
  // вложениями и режимом, который вернётся в композер по «Стоп»); 'agent' — chats_send.
  | { type: 'pending_messages'; items: { id: string; text: string; senderPersonaId?: string; senderOrigin?: string; enqueuedAt: string; senderChatName?: string; kind?: 'user' | 'agent'; attachedPaths?: string[]; mode?: string | null }[] }
  // «Стоп» вернул прерванное пользовательское сообщение в композер (фича «честная
  // очередь»). text=null — восстанавливать нечего (прерван авто/агентский ход, очередь
  // пуста): композер не трогаем. Иначе — текст, вложения и режим, которые подставятся
  // в пустое поле (черновик пользователя важнее — непустой композер игнорируется).
  | { type: 'composer_restore'; text?: string | null; attachedPaths?: string[] | null; mode?: string | null }
  // Подсказка следующего сообщения — чип в композере
  | { type: 'prompt_suggestion'; text: string }
  // Снимок промпта хода записан: id для кнопки «какой промпт ушёл» под постом.
  // Текст сюда не кладём — шторка забирает его отдельным REST-запросом.
  // applied=false — ход доигрывался в живом процессе, и этот промпт модели не уходил
  | { type: 'prompt_snapshot'; snapshotId: string; applied: boolean; inheritedFromId?: string }
);

// Снимок промпта хода (GET /api/sessions/{id}/prompt/{snapshotId})
export interface PromptSection {
  key: string;
  title: string;
  text: string;
  // system — часть --append-system-prompt; turn — текст сообщения хода с обвязками;
  // cli-file — файл слоя CLI (CLAUDE.md с раскрытыми импортами)
  kind: 'system' | 'turn' | 'cli-file';
  // Длина оригинального текста, когда сам текст в выдаче опущен (файлы слоя CLI
  // грузятся по требованию — они весят десятки КБ)
  size?: number | null;
  // Кусок одинаков от хода к ходу → живёт в кэшируемом префиксе запроса.
  // false — пересчитывается под текст хода (recall, привязки, граф, само сообщение)
  stable?: boolean;
  // Чья это часть промпта: persona | mcp | project | recall | turn | cli | misc.
  // Слой персоны размазан по пяти секциям — по группе считается его суммарная цена
  group?: string;
}

export interface CliSkill {
  name: string;
  description?: string | null;
  // profile — каталог скиллов профиля CLI, project — .claude/skills рабочей папки
  source: string;
}

// Доступная часть слоя claude CLI. Недоступны и потому отсутствуют: текст встроенного
// системного промпта Anthropic и текстовые описания инструментов
// Незаполненные поля сервер отдаёт как null (сериализатор контроллеров их не прячет),
// поэтому все они nullable: проверять надо typeof, а не !== undefined
export interface CliLayer {
  tools?: string[] | null;
  mcpServers?: { name: string; status: string }[] | null;
  files?: PromptSection[] | null;
  skills?: CliSkill[] | null;
  transcriptBytes?: number | null;
  transcriptMessages?: number | null;
}

export interface PromptSnapshot {
  id: string;
  createdAt: number;
  applied: boolean;
  inheritedFromId?: string | null;
  sections: PromptSection[];
  cliArgs: string[];
  mcpServers: string[];
  model?: string | null;
  mode?: string | null;
  cliLayer?: CliLayer | null;
}

export interface UsageInfo {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

// Preview: запускаемый сервис проекта (инференс из манифеста или из .claude/launch.json)
export interface ProjectService {
  id: string;
  name: string;
  source: string;               // launch.json | npm | dotnet | docker-compose | procfile | makefile | custom
  command: string;
  args: string[];
  cwd: string | null;
  suggestedPort: number | null;
  autoPort: boolean;
  saved: boolean;               // из .claude/launch.json — можно редактировать/удалять
  // idle | starting | started | stopped | error | external (порт слушает процесс,
  // запущенный вне продукта — остановить его нельзя и логов у него нет)
  status: string;
  runningPort: number | null;
  error: string | null;
  // Составной запуск (Rider multilaunch): id входящих сервисов. Своей команды у
  // группы нет — она поднимает участников, статус и порт производные от них
  members?: string[] | null;
}

// Одна конфигурация из .claude/launch.json (формат Claude Desktop)
export interface LaunchConfigEntry {
  name?: string;
  runtimeExecutable?: string;
  runtimeArgs?: string[];
  program?: string;
  args?: string[];
  port?: number;
  autoPort?: boolean;
  cwd?: string;
  env?: Record<string, string>;
}

// Вариант продолжения чата при исчерпании лимита (из события provider_limit):
// kind='subscription' — другой аккаунт того же пула подписок Claude (та же модель,
// tierLabel — тариф «Max 5×», utilization 0..1); kind='provider' (дефолт) — сторонний
// провайдер (tierLabel/utilization не заполняются)
export interface ProviderFallbackOption {
  key: string;
  displayName: string;
  model: string;
  kind?: 'subscription' | 'provider';
  tierLabel?: string | null;
  utilization?: number | null;
}

// Состояние одного окна лимита подписки (из rate_limit_event). utilization: 0..1.
export interface RateLimitInfo {
  limitType: string;
  utilization?: number;
  resetsAt?: string;
  status?: string;
  isUsingOverage?: boolean;
  overageStatus?: string;
  overageResetsAt?: string;
}

// Одно окно квоты CLI-провайдера подписки (GET /api/providers/{key}/balance|usage):
// unit 'percent' — value остаток в процентах ("97.3"); 'count' — "120/300", занято сейчас
// из лимита (моментальный снимок); 'consumed' — "101/4000", израсходовано из лимита (растёт
// монотонно, сбрасывается по resetsAt). count/consumed — уже отформатированная строка «N/M»
export type ProviderQuotaWindowUnit = 'percent' | 'count' | 'consumed';
export interface ProviderQuotaWindow {
  label: string;
  value: string;
  resetsAt: string | null;
  unit: ProviderQuotaWindowUnit;
}

// Баланс CLI-провайдера. windows — ВСЕ окна квоты (включая основное), только у квотных
// провайдеров (GLM/Kimi/MiniMax); у денежных (deepseek/moonshot/openrouter) отсутствует/пусто —
// totalBalance/resetsAt остаются единственным источником (обратная совместимость + график)
export interface ProviderBalanceInfo {
  available: boolean;
  currency: string;
  totalBalance: string;
  asOf?: string;
  resetsAt?: string | null;
  windows?: ProviderQuotaWindow[] | null;
  // Доп. сведения в раскрытую карточку (напр. «В том числе подарочных: $5», расход по периодам,
  // уровень подписки, сводка за 24ч у FreeLLM) — null/отсутствует: показать нечего
  note?: string | null;
  // === Типизированные поля «Модели и расход» (models-spend-data-spec) ===
  // Все опциональны: «не разобралось — поля нет», вёрстка обязана жить без них.
  // Пишет ли бэк историю баланса (для sparkline в раскрытии)
  trackHistory?: boolean;
  // Подарочный (бонусный) остаток в валюте баланса — показывается «из них $N подарочных»
  grantedBalance?: number | null;
  // Тариф подписки провайдера без приставки «Подписка:» (напр. "Standard") — пилюля в шапке
  planLabel?: string | null;
  // Предел расхода ключа (OpenRouter): шкала «осталось из $N лимита ключа», только в «Деньгах»
  keyLimit?: { remaining: number; total: number } | null;
  // Расход по данным самого провайдера (НЕ наш SpendStore) — подпись «по данным OpenRouter»
  spend?: { daily: number; weekly: number; monthly: number } | null;
  // Здоровье агрегатора (FreeLLM): запросы за 24ч, успешность, живые платформы
  health?: {
    requests24h?: number | null;
    successRate?: number | null;
    platformsAlive?: number | null;
    platformsTotal?: number | null;
  } | null;
}

// Снимок использования окна во времени (история с бэка, data/usage.json) — для экрана usage и тренда
export interface UsageSnapshot {
  timestamp: string;
  limitType: string;
  // Чей снимок: ключ подписки пула ("claude", …) или CLI-провайдера ("glm", "deepseek")
  subscriptionKey?: string;
  utilization?: number;
  status?: string;
  isUsingOverage?: boolean;
  resetsAt?: string;
  overageStatus?: string;
  overageResetsAt?: string;
  // Кто записал снимок: живой ход чата, идл-пинг простаивающего аккаунта или OAuth-опрос
  // лимитов; null/отсутствует — записи до фичи идл-пинга (обратная совместимость)
  source?: 'turn' | 'probe' | 'oauth' | null;
}

// Тариф подписки (с бэка, из credentials)
export interface PlanInfo {
  subscriptionType?: string;
  rateLimitTier?: string;
  label: string;
}

// Ответ /api/usage: история снимков + тариф
export interface UsageResponse {
  snapshots: UsageSnapshot[];
  plan?: PlanInfo;
  subscriptions?: Record<string, SubscriptionUsage>;
  // Порог утилизации 5h-окна, выше которого аккаунт выведен из ротации новых чатов
  rotationThreshold?: number;
  // Ключ аккаунта, куда фактически ушёл бы новый чат сейчас (детерминированный выбор):
  // при отсутствии свободных пул спиллит на перегруженный — бейдж показывает это честно
  routingTarget?: string;
  // Снимки окон лимитов сторонних CLI-провайдеров (glm/deepseek) — их Anthropic-совместимые
  // эндпоинты тоже шлют rate_limit_event, снимки пишутся под ключ провайдера
  providers?: Record<string, UsageSnapshot[]>;
  // Статус опроса api/oauth/usage по ключам аккаунтов: "ok" | "unauthorized" (токен
  // не подходит — setup-токен вместо полноценного входа) | "error"
  pollStatuses?: Record<string, string>;
  // Локальная модель (Ollama): какая модель и на какие фоновые действия она заведена
  ollama?: OllamaUsageInfo;
}

// Блок «Локальная модель» на экране использования. У Ollama нет лимитов/баланса (бесплатно),
// показываем модель и маршрут каждого фонового действия (локаль/claude).
export interface OllamaUsageInfo {
  enabled: boolean;
  model?: string | null;
  baseUrl?: string | null;
  actions: OllamaActionInfo[];
}

// Раскрытие пресета в ответе места каталога: заполнено, когда назначение места —
// общий пресет (route при этом развёрнут в первый шаг — для «Сейчас пойдёт»);
// name null — битая ссылка (пресет удалён)
export interface PlacePresetRef {
  id: string;
  name: string | null;
  steps: string[];
}

export interface OllamaActionInfo {
  key: string;
  title: string;
  group: string;
  // Начинается ли действие с локальной модели прямо сейчас (с учётом доступности Ollama)
  routedToOllama: boolean;
  // Откуда взято действующее значение: дефолт каталога, конфиг Ollama:Actions или выбор админа
  source?: 'default' | 'config' | 'admin';
  // Исполнитель первого шага: 'local' | 'tier:strong|medium|weak' (слот) | id модели
  // провайдера; легаси 'claude'/'default' ≙ tier:medium. Если назначение — пресет,
  // здесь развёрнутый первый шаг цепочки, а сам пресет — в поле preset
  route?: string;
  // Пресет назначения (разворачивается в route на бэке); null — назначение не пресет
  preset?: PlacePresetRef | null;
  // Действию нужна сильная модель (лицо продукта, генерация артефактов) — локаль ему не
  // годится; пресеты подбирают Claude/облачную модель. В UI помечается бейджем.
  requiresStrong?: boolean;
  // Агентное место (группа «Чаты и персоны»): запускает сессию claude CLI, поэтому
  // локальная модель и direct:-модели ему недоступны
  agentic?: boolean;
}

export interface SubscriptionUsage {
  snapshots: UsageSnapshot[];
  name?: string;
  // Берёт ли пул этот аккаунт для новых чатов + эффективная утилизация 5h-окна (0..1)
  inRotation?: boolean;
  utilization?: number;
  // Жёсткое исчерпание (rejected/100%) — выведен независимо от числа utilization
  exhausted?: boolean;
  // Ярлык тарифа ("Max 20×", "Pro", …) — по нему пул приоритизирует аккаунты
  tier?: string;
  // Готовая PowerShell-команда входа в профиль аккаунта (для плашки «нужен claude login»);
  // null — команда неприменима (токен приходит из env, логин в файл не поможет)
  loginCommand?: string | null;
}

// Статистика аккаунта fal.ai (баланс + расход за период)
export interface FalModelSpend { endpointId: string; cost: number; }
export interface FalDaySpend { date: string; cost: number; }
export interface FalUsageSummary {
  days: number;
  total: number;
  byModel: FalModelSpend[];
  series: FalDaySpend[];
}
export interface FalAccountResponse {
  enabled: boolean;
  balance?: number | null;
  currency?: string | null;
  usage?: FalUsageSummary | null;
}

// Статистика аккаунта glif (план + баланс кредитов + расход за окна).
// Кредиты, не USD: currency="credits". Агрегаты расхода фиксированные (24ч/7д/30д).
export interface GlifSpend { last24h?: number; last7d?: number; last30d?: number; }
export interface GlifAccountResponse {
  enabled: boolean;
  plan?: string | null;
  planStatus?: string | null;
  balance?: number | null;
  currency?: string | null;
  spend?: GlifSpend | null;
}

// Live-состояние цикла «до готово» (из события work_loop; флаг work-loop)
export interface WorkLoopState {
  active: boolean;
  iteration: number;
  maxIterations: number;
  phase: string | null;
}

// === Режим «Командная реализация» ===
// Стадии непрерывного контура — совпадают с wire-токенами TeamImplementStage на бэке
export type TeamImplementStage =
  | 'interview'         // координатор спрашивает человека, прежде чем планировать (Э8)
  | 'planning'          // координатор готовит карточку плана
  | 'confirming'        // план ждёт единственного согласования
  | 'wave'              // волна исполнителей в работе
  | 'awaitingDecision'  // эскалация: ждёт решения человека
  | 'checking'          // финальная проверка (сборка/тесты)
  | 'idle';             // итерация закрыта, режим ждёт новой вводной

// Бюджет итерации: счётчики «израсходовано» + потолки (сбрасывается по новой вводной).
// wakeups — срочные вызовы координатора докладом-блокером снизу (свой потолок)
export interface TeamImplementBudget {
  tasksUsed: number;
  wavesUsed: number;
  runsUsed: number;
  retriesUsed: number;
  wakeupsUsed: number;
  maxTasks: number;
  maxWaves: number;
  maxRuns: number;
  maxRetries: number;
  maxWakeups: number;
}

// Состояние режима на сессии (Session.teamImplement); null — режим выключен.
// Состав исполнителей: пустой список = вся команда проекта.
export interface SessionTeamImplement {
  stage: TeamImplementStage;
  waveNumber: number;
  // Плановое число волн текущей итерации (из карточки плана); 0 — план ещё не запускался.
  // Бейдж «волна N из M» берёт M отсюда, а не из потолка бюджета — тот про расход
  plannedWaves: number;
  autoWaves: boolean;
  // Человек нажал «Остановить»: текущие исполнители дорабатывают, новые волны не стартуют.
  // Снимается решением «Продолжить» по карточке остановки либо новой вводной
  stopped: boolean;
  coordinatorPersonaId?: string | null;
  plannerPersonaId?: string | null;
  executorPersonaIds: string[];
  budget: TeamImplementBudget;
  // «Координатор не пишет код сам»: у чата-штаба отключены инструменты правки файлов
  coordinatorNoCode: boolean;
  planCardId?: string | null;
  // Э8: план-режим навязан чату (стадии интервью/планирования) — селектор режима в
  // композере показывает «план» и заблокирован. Присутствует у live-состояния (событие
  // team_implement присылает готовое значение); при REST-гидратации до первого события
  // считаем его сами из savedMode — см. teamImplementModeLocked()
  modeLocked?: boolean;
  // Э8: режим прав человека, сохранённый на входе в план-режим (raw-поле REST-гидратации,
  // SessionTeamImplement.SavedMode с бэка) — вернётся после согласования плана. У live-события
  // его нет, там сразу приходит посчитанный modeLocked
  savedMode?: Mode | null;
  // Э8: версия текущего плана итерации (0 — планов ещё не было). Имя совпадает и на REST,
  // и в live-событии — маппится напрямую
  planVersion: number;
}

// Live-состояние режима (из события team_implement)
export interface TeamImplementState extends SessionTeamImplement {
  active: boolean;
}

// Под-задача плана командной реализации: единица раздачи (в Э3 из неё создаётся задача).
// executorRationale — одна строка «почему именно он» от планировщика; в ней же приходят
// пометки бэка «проверьте выбор» / «не обосновал», когда подбор ненадёжен.
export interface TeamPlanSubtask {
  id: string;
  title: string;
  goal: string;
  executorPersonaId?: string | null;
  executorRationale: string;
  files: string[];
  wave: number;
  doneCriteria: string;
}

// Структурный план командной реализации (карточка в ленте штаба).
// waveCount/executorCount считает бэкенд — подзаголовок карточки собирается из них.
export interface TeamPlan {
  id: string;
  request: string;
  summary: string;
  plannerPersonaId?: string | null;
  approved?: boolean | null;
  createdAt: string;
  waveCount: number;
  executorCount: number;
  subtasks: TeamPlanSubtask[];
  // Э8: версия плана в итерации — 1 у первого, растёт с каждым перепланированием после
  // интервью. Карточка показывает «План v2 · обновлён после уточнений»
  version: number;
  // Э8: допущения планировщика — путь «вопросов нет» (утверждаются вместе с планом)
  assumptions: string[];
  // Э8: «что изменилось» у плана vN относительно предыдущей версии; пусто у v1
  changes: string[];
  // Замысел планировщика на 3–5 строк (решение 2026-08-02) — пусто/undefined у истории
  // до этой доработки и когда планировщик не заполнил поле: блок в карточке не рисуем
  intent?: string;
  // Путь к файлу полного плана относительно корня проекта — null у глобального чата
  // без проекта либо когда запись не удалась; undefined у истории до этой доработки
  planFilePath?: string | null;
}

// Решение человека по карточке плана (метод хаба RespondTeamPlan). edit — правка плана
// текстом (feedback): сервер сам гасит текущую карточку и пересобирает план версией vN+1
export type TeamPlanDecision = 'run' | 'reassign' | 'cancel' | 'edit';

// Триггер остановки практики — wire-токены TeamEscalationKind с бэка.
// waveGate — не проблема, а гейт «волна закрыта, запускать следующую?» при снятом авто;
// stopped — пауза по команде человека; waveAdded — вовсе не остановка (см. ниже)
export type TeamEscalationKind =
  | 'blocker'
  | 'taskFailed'
  | 'planDeviation'
  | 'checkFailed'
  | 'productDecision'
  | 'budgetExhausted'
  | 'waveStalled'
  | 'waveGate'
  | 'stopped'
  // Информация (Э5): по новой вводной развёрнута добавочная волна. Работа уже идёт,
  // клика карточка не ждёт — только показывает состав и даёт «Остановить»
  | 'waveAdded'
  // Тупик в волне (Э8): координатор не знает, как действовать дальше — требования неясны.
  // Волны на паузе, дальше — интервью (ASK-карточки); без кнопок, тон не тревожный
  | 'needsClarification';

// Кнопка карточки остановки: id — wire-токен решения, label — подпись для человека
export interface TeamEscalationAction {
  id: string;
  label: string;
}

// Карточка остановки режима «Командная реализация» (Э4). Форма — как в истории чата
// (StoredTeamEscalationMessage.escalation); live-событие приходит плоским и собирается
// в этот же объект редьюсером
export interface TeamEscalation {
  id: string;
  kind: TeamEscalationKind;
  title: string;
  details: string;
  actions: TeamEscalationAction[];
  taskId?: string | null;
  wave: number;
  // Только в истории — у live-события времени нет
  createdAt?: string;
  resolved: boolean;
  chosenActionId?: string | null;
  // Автор карточки (Э8): координатор на момент публикации — карточка идёт от его лица.
  // null — персоны у штаба нет, шапка деградирует до обезличенного варианта
  personaId?: string | null;
}

// Элементы чата
export type ChatItem =
  // viaAgent — сообщение прислано не человеком, а агентом из другой сессии (chats_send);
  // senderPersonaId — персона-автор (рендерим сообщение её лицом);
  // systemDirective — служебная директива цикла «до готово» (компактная плашка вместо пузыря);
  // auto — опубликовано автоматически (не человеком): командная механика/задача — показываем источник
  // senderOrigin — источник входящего сообщения, если он в ДРУГОМ месте (имя чужого проекта
  // либо «Вне проектов»): рисуем чипом, чтобы было видно, откуда прилетело. Считает сервер.
  // senderChatName — имя чата-отправителя: заголовок карточки, когда персоны у него нет
  // staffNote — служебный ход механики штаба (ответ на карточку, возврат в интервью, сводка
  // волны): рисуется компактной плашкой-разделителем с этой подписью, а не пузырём
  // ts — время отправки (Unix-мс UTC): подпись в панели действий поста. Отсутствует
  // у истории, записанной до появления поля
  // promptSnapshotId — снимок промпта, собранного для хода, который начался этим
  // сообщением: по нему шторка «какой промпт ушёл». Нет у истории до появления поля,
  // у ходов без нового сообщения и при сбое записи снимка
  | { kind: 'user_message'; text: string; attachedPaths?: string[]; viaAgent?: boolean; senderPersonaId?: string; systemDirective?: boolean; auto?: boolean; senderOrigin?: string; senderChatName?: string; staffNote?: string; ts?: number; promptSnapshotId?: string }
  | { kind: 'session_started'; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[]; turnWorktree?: { path: string; name: string } | null }
  // personaId — авторство реплики (персона на момент хода); после смены собеседника
  // старые реплики сохраняют прежний аватар. Отсутствует у обычного ассистента.
  // parentToolUseId — текст/thinking сабагента: рендерится внутри карточки родительского
  // tool_use (секция «Активность»), а не в основной ленте.
  // model — чем написан ИМЕННО этот пост: приходит из истории либо клеится на result
  // (модель хода известна только к его концу). ts — когда написан (Unix-мс UTC).
  // Оба необязательны: у старой истории их нет, у сабагентского текста модели нет
  | { kind: 'text'; text: string; personaId?: string; parentToolUseId?: string; model?: string; ts?: number }
  | { kind: 'thinking'; text: string; expanded: boolean; parentToolUseId?: string }
  // bgDone/bgAborted — завершение фонового агента (bg_agent_done / bgDone из истории);
  // workflowAborted — workflow восстановлен из истории прерванным (агенты уже не завершатся)
  | { kind: 'tool_use'; id: string; name: string; input: unknown; result?: string; isError?: boolean; parentToolUseId?: string; streamingArg?: string; workflowAgents?: WorkflowAgentInfo[]; workflowDone?: boolean; workflowAborted?: boolean; bgDone?: boolean; bgAborted?: boolean }
  // decision — вердикт пользователя (только живая лента: permission_request в history не персистится)
  | { kind: 'permission_request'; requestId: string; toolName: string; toolInput: unknown; resolved: boolean; decision?: 'allowed' | 'denied' | 'always' }
  | { kind: 'ask_question'; toolUseId: string; input: unknown; resolved: boolean; answers?: Record<string, string | string[]> }
  | { kind: 'plan_review'; requestId: string; plan: string; resolved: boolean; approved?: boolean; feedback?: string }
  // Карточка плана командной реализации: структурный план (под-задачи, исполнители,
  // волны) с кнопками «Запустить» / «Изменить план» / «Отменить». Сверяется по planId
  | { kind: 'team_plan'; planId: string; plan: TeamPlan; resolved: boolean; approved?: boolean | null; supersededBy?: number | null }
  // Карточка остановки командной реализации: причина, суть и кнопки решения.
  // Сверяется по escalationId — ответ человека переиздаёт ту же карточку решённой
  | { kind: 'team_escalation'; escalationId: string; escalation: TeamEscalation }
  // Итог успешного вызова планировщика — короткая строка в потоке (не персистится, живёт
  // только в ленте вкладки; после планировщика следом приходит карточка team_plan)
  | { kind: 'team_planning_done'; subtaskCount: number; waveCount: number; elapsedMs: number }
  | { kind: 'file_changed'; path: string; added: number; removed: number; external?: boolean }
  | { kind: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[]; contextTokens?: number }
  | { kind: 'fal_cost'; requestId: string; endpointId?: string; costUsd: number; outputUnits?: number; unitPrice?: number }
  | { kind: 'glif_cost'; jobId: string; outputType?: string; mediaCount: number; credits?: number; model?: string }
  | { kind: 'rate_limit'; limitType: string; resetsAt?: string; status?: string }
  | { kind: 'compact_boundary'; trigger: string; preTokens?: number; postTokens?: number }
  | { kind: 'truncated' }
  | { kind: 'redacted_thinking' }
  | { kind: 'interrupted' }
  | { kind: 'resumed' }
  | { kind: 'session_ended' }
  // Разделитель «сменился собеседник»: label задан явно (смена вручную / speaker_changed
  // с сервера) либо резолвится по personaId (derived из истории группового чата)
  | { kind: 'companion_switched'; label: string; personaId?: string }
  // Разделитель «Продолжено на …» — явная миграция чата на другого провайдера
  | { kind: 'provider_switched'; label: string }
  // Разделитель «Ответила …» — рантайм-фолбэк сменил именно МОДЕЛЬ (уровень 2 цепочки —
  // другой провайдер), не просто подписку того же провайдера: та ротация модель не трогает
  // и своей пометки не получает (см. блок G model-providers-rework.md). model/previousModel —
  // id из каталога моделей, подпись собирается на рендере (useModelLabel). reason —
  // канонический класс причины с бэкенда (rate_limit|usage_limit|provider_error|unreachable|context_overflow,
  // персистится в StoredModelSwitchedMessage.Reason). rawLabel — сырой label маркера
  // provider_switched, фолбэк подсказки, когда reason не пришёл или не распознан
  // (см. providerSwitchReasonLabel в lib/providerLimit.ts)
  | { kind: 'model_switched'; model: string; previousModel: string; reason?: string; rawLabel?: string }
  // Карточка-предложение: лимит подписки исчерпан — продолжить на стороннем провайдере.
  // resolved — миграция состоялась (карточка гаснет)
  | { kind: 'provider_limit'; resetsAt?: string; providers: ProviderFallbackOption[]; resolved?: boolean }
  | { kind: 'git_turn_commit'; projectId: string; sha: string; subject: string }
  // Остановка цикла «до готово»: текст готов на сервере (лимит/ошибка/ручной стоп),
  // фронт его не собирает — иначе разъедется с сервером при смене лимита
  | { kind: 'work_loop_stopped'; reason: string; text: string }
  | { kind: 'error'; text: string; canRetry?: boolean };

// Скиллы и агенты
export interface SkillInfo {
  name: string;
  description: string;
  argumentHint?: string;
  filePath: string;
}

export interface AgentInfo {
  name: string;
  description: string;
  color?: string;
  tools: string[];
  permissionMode?: string;
  fileName: string; // без .md
}

export interface SkillsData {
  skills: SkillInfo[];
  projectSkills: SkillInfo[];
  agents: AgentInfo[];
  // Workflow-скрипты (~/.claude/workflows/*.js) — многоагентные оркестрации (/panel-of-experts и т.п.)
  workflows?: SkillInfo[];
  // Скиллы установленных плагинов Claude Code (~/.claude/plugins) — имена с namespace
  // «плагин:скилл» (/oh-my-claudecode:autopilot и т.п.)
  plugins?: SkillInfo[];
}

// Навык из реестра skills.sh (результат поиска/подбора). source — «owner/repo».
export interface RegistrySkill {
  source: string;
  skill: string;
  description: string | null;
  installs: number | null;
  url: string;
}

// Кандидат LLM-подбора: навык реестра + обоснование.
export interface SkillSuggestion {
  skill: RegistrySkill;
  reason: string;
}

// Сгенерированный по промпту навык (превью до сохранения): слаг-имя, описание, тело SKILL.md.
export interface GeneratedSkill {
  name: string;
  description: string;
  body: string;
}

// Ответ GET /api/auth/me — профиль текущего пользователя + эффективные настройки.
// Поля онбординга (фича default-personas-onboarding): defaultPersonaId — личная
// дефолт-персона (null — онбординг не пройден), needsOnboarding — обязательный гейт
// онбординга при входе, onboardingSessionId — живая сессия интервью для резюма.
export interface Me {
  userId: string;
  username: string;
  displayName?: string | null;
  role: string;
  featureFlags?: Record<string, boolean>;
  contextThresholds?: { warnPct: number; dangerPct: number } | null;
  defaultPersonaId?: string | null;
  needsOnboarding?: boolean;
  onboardingSessionId?: string | null;
}

export interface AuthState {
  serverUrl: string;
  token: string;
  username: string;
  // Отображаемое имя из профиля («Григорий»); пусто — показываем username.
  // Для показа пользователю всегда через displayNameOf(), а не напрямую
  displayName?: string;
  role?: string;
  id?: string;
}

/** Как обращаться к пользователю: имя из профиля, иначе логин. */
export function displayNameOf(auth: { displayName?: string; username: string }): string {
  return auth.displayName?.trim() || auth.username;
}

export interface UserProfile {
  id: string;
  username: string;
  // Отображаемое имя из профиля; может отсутствовать — тогда показываем username
  displayName?: string;
  role: 'admin' | 'user';
  createdAt: string;
  // Среда исполнения процессов пользователя: сервер (полный доступ) или Docker-песочница
  executionEnvironment?: 'local' | 'container';
}

// ===== Продуктовая история (AI-сводка по всем проектам) =====

// Пункт продуктовой сводки за день: что нового и чем полезно
export interface ChangelogItem {
  type: 'feature' | 'improvement' | 'fix' | 'other';
  area: string;      // раздел продукта — для группировки внутри дня
  emoji: string;
  title: string;
  benefit: string;
  score: number;        // значимость 1-5 (5 — хит, 1-2 — по мелочи)
  scoreReason: string;  // обоснование оценки (в тултипе бейджа)
  authors: string[];
  projects: string[];
}

// Во сколько обошлась сборка сводки дня — показывается внизу дня
export interface ChangelogGeneration {
  durationMs: number;           // сколько ждали ответа, вместе со стартом CLI
  inputTokens: number;          // вход без кеша
  cacheCreationTokens: number;  // вход, записанный в кеш промптов
  cacheReadTokens: number;      // вход из кеша (дешевле обычного)
  outputTokens: number;
  costUsd: number | null;       // для подписки — оценка по тарифам API, деньги не списываются
  model: string | null;
  generatedAt: string;          // ISO-дата сборки
}

// Сводка изменений за один день (по всем проектам)
export interface ChangelogDay {
  date: string; // yyyy-MM-dd
  items: ChangelogItem[];
  degraded?: boolean;       // сводку собрать не удалось — пункты сырые (subject'ы коммитов)
  degradedReason?: string;  // что сломалось и как это починить
  generation?: ChangelogGeneration | null;  // расход на сборку (нет у старых записей кеша)
}

// Заглушка дня для мгновенного списка (без LLM)
export interface DaySummaryStub {
  date: string;
  commitCount: number;
  cached: boolean;
}

// Статус настройки источника changelog — «настроено» vs «донастрой инстанс»
export interface ChangelogStatus {
  configured: boolean;
  mode: string;         // 'repo' | 'projects'
  detail: string | null;
}

// ===== Заметки (Obsidian-совместимая база знаний) =====

// Привязка заметки-комментария к MD-документу (флаг doc-annotations)
export interface NoteAnnotationInfo {
  docScope: string;        // 'personal' | projectId (путь от корня проекта)
  docPath: string;
  status: 'open' | 'resolved';
  blockId?: string | null;
  anchorQuote?: string | null;
  anchorHeading?: string | null;
  isReply?: boolean;       // ответ в треде (annotates указывает на заметку-комментарий)
  docMissing?: boolean;    // документ-цель удалён (ghost-узел в дереве)
}

// Ответ в треде комментария
export interface NoteReply {
  noteId: string;
  title: string;
  excerpt: string;
  createdAt: string;
  tags: string[];
}

// Комментарий документа с резолвом привязки — для подсветки при чтении
export interface DocAnnotation {
  noteId: string;
  title: string;
  status: 'open' | 'resolved';
  state: 'exact' | 'changed' | 'orphan';
  start: number;           // офсеты якорного блока в контенте документа; -1 у сироты
  end: number;
  blockId?: string | null;
  anchorHeading?: string | null;
  quote: string;
  excerpt: string;
  tags: string[];
  updatedAt: string;
  replies: number;         // число ответов в треде
}

// Источник заметки: "personal" (личный vault) или id проекта
export interface NoteSummary {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  path: string;
  tags: string[];
  createdAt: string;
  updatedAt: string;
  expiresAt?: string;
  sourceSessionId?: string;
  annotation?: NoteAnnotationInfo | null;
}

// Разрешённая исходящая ссылка [[...]]; resolved=false — «призрачная» (цели ещё нет)
export interface NoteLink {
  targetId: string;
  targetTitle: string;
  resolved: boolean;
}

// Обратная ссылка: заметка, ссылающаяся на текущую, + контекст-сниппет
export interface NoteBacklink {
  sourceId: string;
  sourceTitle: string;
  source: string;
  sourceLabel: string;
  snippet: string;
}

export interface NoteDetail {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  path: string;
  content: string;      // сырой markdown (для правки)
  tags: string[];
  links: NoteLink[];
  backlinks: NoteBacklink[];
  unlinkedMentions: NoteBacklink[];   // упоминания заголовков без [[…]]
  createdAt: string;
  updatedAt: string;
  expiresAt?: string;
  sourceSessionId?: string;
  annotation?: NoteAnnotationInfo | null;
}

// Узел графа; ghost=true — «призрачная» заметка (на неё ссылаются, но её нет)
export interface NoteGraphNode {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  degree: number;
  ghost: boolean;
  tags?: string[];
  // Комментарии в графе (?annotations=true): 'comment'/'reply' — узлы комментариев,
  // 'doc' — призрачный узел документа-файла; status — open|resolved у корневых
  kind?: 'comment' | 'reply' | 'doc' | null;
  status?: 'open' | 'resolved' | null;
}

// Шаблон заметки (templates/ личного vault)
export interface NoteTemplate {
  id: string;
  title: string;
}

// Результат семантического поиска (Dify RAG)
export interface NoteSemanticHit {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  score: number;
  snippet: string;
}

export interface NoteGraphEdge {
  source: string;
  target: string;
}

export interface NoteGraph {
  nodes: NoteGraphNode[];
  edges: NoteGraphEdge[];
}

// Источник для выбора «куда создать» (личный vault + проекты владельца)
export interface NoteSource {
  key: string;
  label: string;
}

// Физическая папка источника (в т.ч. пустая) — для дерева и выбора «куда создать»
export interface NoteFolder {
  source: string;
  path: string;
}

export interface CreateNoteDto {
  title: string;
  content?: string;
  source?: string;
  templateId?: string;
  folder?: string;   // папка внутри источника ("Идеи/Черновики"); пусто = корень
  expiresAfterMinutes?: number | null;
  sourceSessionId?: string;
}

export interface UpdateNoteDto {
  title?: string;
  content?: string;
}

// ===== Знания (базы знаний Dify — раздел «Знания») =====
// База знаний = Dify-датасет. Список классифицируется бэкендом по имени/permission:
// личные («{user}:…» — заметок/проектов/памяти персон/самостоятельные) и публичные
// (all_team_members). deletable — можно ли удалить из раздела (самостоятельные/публичные).
export type KnowledgeVisibility = 'personal' | 'public';

export interface KnowledgeBaseSummary {
  id: string;
  title: string;
  type: string;                 // Заметки | Проект | Память персоны | Самостоятельная | Публичная
  visibility: KnowledgeVisibility;
  documentCount: number;
  createdAt: string | null;
  deletable: boolean;
  description?: string | null;
}

export interface KnowledgeDocument {
  id: string;
  name: string;
  indexingStatus: string;       // completed | indexing | error и т.п. (строка Dify)
}

export interface KnowledgeBaseDetail extends KnowledgeBaseSummary {
  documents: KnowledgeDocument[];
}

export interface KnowledgeSearchHit {
  score: number;
  content: string;
  documentName: string;
}

export interface CreateKnowledgeBaseDto {
  title: string;
  description?: string;
  visibility: KnowledgeVisibility;
}

// Ответ GET /api/knowledge: configured=false — Dify не настроен (фронт показывает empty-state)
export interface KnowledgeListResponse {
  configured: boolean;
  items: KnowledgeBaseSummary[];
}

// Сегмент (чанк) документа базы знаний — порция текста для просмотра/поиска
export interface KnowledgeSegment {
  position: number;
  content: string;
  wordCount: number;
}

// Содержимое документа (GET /api/knowledge/{id}/documents/{docId}) — сегменты по порядку
export interface KnowledgeDocumentContent {
  id: string;
  segments: KnowledgeSegment[];
}

// ===== Персоны (олицетворённые ИИ-собеседники) =====

// Зона контекста персоны: глобально (личное пространство) или в рамках проекта
export type PersonaScope = 'global' | 'project';

// Профиль доступа персоны (P6): full — без ограничений; readOnly — смотрит и
// советует, но ничего не меняет; custom — свой список запрещённых инструментов
export type PersonaAccess = 'full' | 'readOnly' | 'custom';

// Специальность персоны — функциональная роль для оркестрации (НЕ отображаемое имя роли):
// конвейер (analyst→planner→reviewer→executor), голос брифинга (secretary),
// группировка/статус команды, роутинг памяти команды. none — не задана.
// backendExecutor/frontendExecutor/devopsExecutor — профильные исполнители; wire-значения
// совпадают с ключами бэкендного SpecialtyCatalog (camelCase от enum).
export type PersonaSpecialty =
  | 'none' | 'analyst' | 'planner' | 'reviewer' | 'executor' | 'secretary'
  | 'coordinator' | 'mentor' | 'designer' | 'consultant' | 'librarian' | 'tester'
  | 'backendExecutor' | 'frontendExecutor' | 'devopsExecutor';

// Шаблон прав и инструментов специальности: подставляется в поля персоны
// при выборе специальности, дальше правится вручную.
export interface SpecialtyTemplate {
  access: PersonaAccess;
  // null — все возможности (tasks+notes+web)
  tools: string[] | null;
  // Имеет смысл только при access === 'custom'
  disallowedTools: string[] | null;
}

// Запись каталога специальностей из GET /api/specialties: подписи и эффективный
// шаблон прав вызывающего (настройки поверх дефолтов кода) приходят с бэкенда.
export interface SpecialtyCatalogEntry {
  key: PersonaSpecialty;
  label: string;
  executorFamily: boolean;
  template: SpecialtyTemplate | null;
}

// Именованный пресет — упорядоченная цепочка шагов (ADR-007 §1). Шаг — маршрут
// в лексике назначений мест: tier:strong|medium|weak, id модели, local, claude, default.
// Ссылаться на другой пресет шаг не может (вложенность запрещена бэкенд-валидацией).
export interface ModelRoutePreset {
  id: string;
  name: string;
  description?: string | null;
  steps: string[];
}

// Пресет в объединённом списке GET /api/specialties/settings: личные впереди,
// затем общие; scope — признак слоя (общий чужой читается, но не правится)
export interface ScopedPreset extends ModelRoutePreset {
  scope: 'owner' | 'global';
}

// Настройка специальности в слое (глобальном или личном): шаблон прав + матрица
// моделей по уровням (ADR-007 §2). Значение ячейки — id модели ИЛИ "preset:{id}";
// пустая ячейка — «спроси матрицу шире». defaultTier — уровень по умолчанию для
// персон специальности без своего. Запись в личном слое заменяет глобальную ЦЕЛИКОМ.
export interface SpecialtyTemplateSettings {
  access: PersonaAccess;
  tools?: string[] | null;
  disallowedTools?: string[] | null;
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
  defaultTier?: ModelTierValue | null;
}

// Слой настроек специальностей и пресетов: шаблоны + «любая специальность» + пресеты-цепочки
export interface SpecialtySettingsLayer {
  specialties: Record<string, SpecialtyTemplateSettings>;
  // «Любая специальность»: применяется, когда у конкретной специальности записи нет
  defaultSpecialty?: SpecialtyTemplateSettings | null;
  presets: ModelRoutePreset[];
}

// Ответ GET /api/specialties/settings: глобальный слой, личный слой вызывающего
// и объединённый список пресетов с признаком слоя
export interface SpecialtySettingsResponse {
  version: number;
  // Эффективный бюджет подмен цепочки хода (per-owner → global → дефолт, кламп 1..5):
  // шаги пресета за пределом бюджета+1 приглушаются как «обычно не используется»
  maxSubstitutions?: number;
  global: SpecialtySettingsLayer;
  owner: SpecialtySettingsLayer;
  presets: ScopedPreset[];
}

// Ответ серверного сброса исключений (GET .../reset/{scope}/preview, POST .../reset/{scope}):
// specialties/personas — число ФАКТИЧЕСКИ изменённых записей (у preview — сколько изменится);
// shadowed — состояние слоя ПОСЛЕ операции (ключи специальностей, оставшихся ради своих прав,
// а не дельта вызова); personaNames — имена изменённых персон (только scope=owner)
export interface ResetResult {
  specialties: number;
  shadowed: string[];
  personas: number;
  personaNames: string[];
}

// Ответ GET /api/models/preview — эффективный резолв для строки «Сейчас пойдёт»
// (спека, блок 4). model — первая развёрнутая модель хода (null — пустой резолв или
// битый пресет); source — где выбрано значение; tier/tierOrigin — эффективный уровень
// и кто его задал; preset — раскрытие ссылки preset:{id}; chain — план фолбэка.
export interface ModelPreviewResponse {
  model: string | null;
  source: 'persona-model' | 'persona-cell' | 'specialty-cell'
    | 'owner-slot' | 'instance-slot' | 'place-assignment' | 'explicit' | null;
  tier: ModelTierValue | null;
  tierOrigin: 'task' | 'persona' | 'specialty' | 'place' | null;
  preset: { id: string; name: string | null; steps: string[]; broken: boolean } | null;
  chain: string[];
}

// Ответ GET /api/models/presets/{id}/usage — места, где выбран пресет
// (диалог удаления, спека блок 6). ownerId — null у общих мест (инстанс/место каталога)
export interface PresetUsageEntry {
  kind: 'instance-slot' | 'owner-slot' | 'specialty-cell' | 'persona-model' | 'persona-cell' | 'place';
  label: string;
  ownerId?: string | null;
}
export interface PresetUsageResponse {
  presetId: string;
  count: number;
  usages: PresetUsageEntry[];
}

// Параметры кропа загруженного аватара: масштаб + смещение центра окна
// от центра картинки (в пикселях исходника)
export interface AvatarCropStateDto {
  scale: number;
  offsetX: number;
  offsetY: number;
}

// Аватар персоны: инициалы на цветном фоне или загруженная картинка (этап 4).
// color — ключ палитры AGENT_COLORS; imageFile — имя файла в хранилище персоны;
// originalFile/crop — оригинал загруженного файла и параметры кропа (для «Перекроить»).
export interface PersonaAvatar {
  kind: 'initials' | 'image';
  color?: string;
  imageFile?: string;
  originalFile?: string | null;
  crop?: AvatarCropStateDto | null;
}

// Структурированный контракт персоны (P1): характер разложен по слотам,
// каждый слот попадает в свою секцию системного промпта. Отсутствие контракта —
// legacy-режим: весь характер живёт единым текстом в systemPrompt.
export interface PersonaContract {
  character?: string;         // характер и манера общения (свободный текст)
  tone?: string;              // тон одной фразой («тепло и на равных»)
  mustDo?: string[];          // правила «всегда …»
  mustNot?: string[];         // правила «никогда …»
  outputFormat?: string;      // требования к формату ответов
  speechExamples?: string[];  // примеры реплик (образцы стиля)
  instructions?: string;      // полный регламент роли (длинный markdown)
}

export interface Persona {
  id: string;
  ownerId: string;
  name: string;
  role?: string;              // роль персоны (главная подпись: «Роль (Имя)»)
  handle: string;             // машинное имя (@handle) — уникально в контексте (проект/глобально)
  handleCustom?: boolean;     // handle задан вручную (миграция/авто-переименования не трогают)
  description?: string;
  systemPrompt?: string;      // «характер» — legacy-текст (у персон без contract)
  contract?: PersonaContract | null; // структурированный контракт (P1)
  model?: string;
  // Уровень модели персоны (слот); отсутствует — не задан. Слабее явной model и слабее
  // уровня самой задачи, когда персона выступает исполнителем.
  // Семантика (ADR-007 §2): уровень разворачивается по самой узкой заполненной матрице —
  // своя (tierStrong/… ниже) → специальности → слоты владельца.
  modelTier?: ModelTierValue;
  // Свои модели по уровням: id модели ИЛИ "preset:{id}"; пусто — наследуется
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
  effort?: string;
  scope: PersonaScope;
  projectId?: string;         // задан только для scope === 'project'
  avatar: PersonaAvatar;
  greeting?: string;          // приветствие персоны в начале чата
  memoryEnabled: boolean;     // долгая память (этап 2)
  // Специальность (функциональная роль) для оркестрации; отсутствие/none — не задана
  specialty?: PersonaSpecialty;
  // Возможности персоны (ключи tasks/notes/web); null/отсутствие — без ограничений
  tools?: string[] | null;
  // Профиль доступа (P6); отсутствие — full
  access?: PersonaAccess;
  // Свой список запрещённых инструментов (только при access === 'custom')
  disallowedTools?: string[] | null;
  // Ключ шаблона пантеона OmO, из которого подключена персона (null — создана вручную)
  templateKey?: string | null;
  // Привязки к источникам знаний и правилам (фича persona-bindings); null — нет
  bindings?: PersonaBinding[] | null;
  // Доступ ко всем проектам владельца — текущим и будущим (только scope === 'global').
  // Привязки типа project/projectPath остаются подсказкой «когда каким пользоваться»
  // и зону не сужают, пока этот флаг включён.
  allProjectsAccess?: boolean;
  // Правила автоматизации (событийно-управляемая проактивность); null — нет
  automationRules?: PersonaAutomationRule[] | null;
  createdAt: string;
  updatedAt: string;
}

// Шаблон роли пантеона OmO (GET /api/personas/pantheon): каталог живёт на бэкенде,
// connectedPersonaId — id уже подключённой персоны владельца (null — не подключена)
export interface PantheonTemplate {
  key: string;
  role: string;
  name: string;
  description: string;
  contract: PersonaContract;
  greeting: string;
  color: string;
  tools?: string[] | null;
  access?: PersonaAccess;
  model?: string;
  effort?: string;
  // Специальность роли (функциональный тег) — ставится в specialty подключённой персоны
  specialty?: PersonaSpecialty;
  connectedPersonaId?: string | null;
}

// === Привязки персоны: «знания и правила» (фича persona-bindings) ===
// Тип источника: project — проект целиком; projectPath — папка/файл проекта;
// knowledge — база знаний (Dify-датасет); notes — источник заметок; tool —
// инструмент workspace; skill — глобальный навык.
// ─── Проактивность/автоматизации персон: правила «событие → действие» ───
export type AutomationTriggerType = 'timer' | 'file' | 'note' | 'gitCommit' | 'taskStatus' | 'mention';
export type AutomationActionWeight = 'gate' | 'work';

// Триггер: args — гибкий JSON-объект, ключи зависят от type
// (см. AutomationTrigger на бэке: timer/file/note/gitCommit/taskStatus/mention).
export interface AutomationTrigger {
  type: AutomationTriggerType;
  args?: Record<string, unknown> | null;
}
export interface AutomationCondition {
  onlyIf?: string | null;       // текст-предикат для LLM-гейта
  quietFrom?: string | null;    // "23:00"
  quietTo?: string | null;      // "07:00" (переход через полночь допустим)
  minIntervalMinutes?: number | null;
}
export interface AutomationAction {
  weight: AutomationActionWeight; // gate — one-shot гейт+сообщение; work — полный агентский ход
  instruction: string;
  rememberInHistory: boolean;
  // Время жизни чата правила (мин); null — бессрочно. Применяется один раз при создании.
  expiresAfterMinutes?: number | null;
}
export interface PersonaAutomationRule {
  id: string;
  enabled: boolean;
  name: string;
  trigger: AutomationTrigger;
  condition?: AutomationCondition | null;
  action: AutomationAction;
  createdAt: string;
  updatedAt: string;
}
// DTO для POST/PUT /api/personas/{id}/automation (partial-merge на бэке: null-поля наследуются).
export interface AutomationRuleDto {
  enabled?: boolean;
  name?: string;
  triggerType?: AutomationTriggerType;
  triggerArgs?: Record<string, unknown> | null;
  conditionOnlyIf?: string | null;
  quietFrom?: string | null;
  quietTo?: string | null;
  minIntervalMinutes?: number | null;
  actionWeight?: AutomationActionWeight;
  actionInstruction?: string;
  rememberInHistory?: boolean;
  // Не указано (undefined, поле опущено в теле запроса) — сохранить текущее/дефолт 1440
  // при создании; null — бессрочно; N>0 — TTL в минутах.
  actionExpiresAfterMinutes?: number | null;
}

// projectPersonas/projectTasks — кросс-проектные привязки: доступ к персонам/задачам
// ДРУГОГО проекта того же владельца (target — projectId; path — сужение: personaId для
// projectPersonas, "readonly" для projectTasks; пусто — вся команда / полный доступ)
export type PersonaBindingType = 'project' | 'projectPath' | 'knowledge' | 'notes' | 'tool' | 'skill' | 'projectPersonas' | 'projectTasks';

// Режим привязки: auto — персона обращается по условию; always — выжимка в каждый ход; off — выключена
export type PersonaBindingMode = 'auto' | 'always' | 'off';

export interface PersonaBinding {
  id: string;
  type: PersonaBindingType;
  // Цель по типам: project/projectPath → projectId; knowledge → datasetId;
  // notes → ключ источника; tool → ключ инструмента; skill — имя навыка
  target: string;
  // Путь внутри цели: папка/файл проекта или папка источника заметок
  path?: string | null;
  // Условие «когда пользоваться» — попадает в системный промпт
  condition: string;
  mode: PersonaBindingMode;
  createdAt: string;
  updatedAt: string;
}

// Тело создания/обновления привязки (POST/PUT bindings)
export interface PersonaBindingDto {
  type: PersonaBindingType;
  target: string;
  path?: string | null;
  condition?: string;
  mode?: PersonaBindingMode;
}

// Элемент каталога целей привязки (GET /api/personas/binding-targets)
export interface BindingTarget {
  id: string;
  label: string;
  hint?: string | null;
  // Происхождение цели; для типа tool с ключом «mcp:<…>» (сервер личного реестра) — 'mcp'
  meta?: string | null;
  // Дефолт инструмента у конкретной персоны (только type=tool с personaId):
  // включён ли без привязки и чем задан дефолт (настройки / пресет роли / системный)
  defaultEnabled?: boolean | null;
  defaultOrigin?: 'settings' | 'role' | null;
  // Последний известный статус сервера личного реестра (только type=tool, ключ «mcp:<…>»,
  // см. McpServerStatuses на бэке: connected/failed/needs-auth/unknown). Строка, а не литерал —
  // набор значений живёт на бэке. Нет данных (старый бэк / не mcp-ключ) — точка статуса
  // в пикере не показывается.
  status?: string | null;
}

// Тело создания персоны (POST /api/personas). Большинство полей опциональны.
export interface CreatePersonaDto {
  name: string;
  role?: string;
  description?: string;
  systemPrompt?: string;
  // Ручной @handle (latin-slug). Создание: пусто — авто из имени. Обновление: undefined —
  // не менять, "" — сбросить к авто-генерации. Занят/невалиден → 400
  handle?: string;
  // Контракт характера; при обновлении: undefined — не менять, пустые слоты — сбросить
  contract?: PersonaContract;
  model?: string;
  // Уровень модели персоны: '' = сбросить, undefined — не менять/не задавать
  modelTier?: ModelTierValue | '';
  effort?: string;
  scope?: PersonaScope;
  projectId?: string;
  color?: string;             // ключ палитры AGENT_COLORS для аватара-инициалов
  greeting?: string;
  memoryEnabled?: boolean;
  // Возможности (tasks/notes/web); полный набор бэкенд нормализует в «без ограничений»
  tools?: string[];
  // Профиль доступа (P6): full | readOnly | custom
  access?: PersonaAccess;
  // Свой список запрещённых инструментов (для custom)
  disallowedTools?: string[];
  // Специальность (функциональная роль) для оркестрации; отсутствие/none — не задана
  specialty?: PersonaSpecialty;
  // Доступ ко всем проектам владельца (текущим и будущим); только для scope === 'global'
  allProjectsAccess?: boolean;
  // true — сгенерировать фото-аватар сразу после создания (опт-ин для авто/LLM-путей,
  // где человек не выбирает аватар сам — напр. пакетное создание команды)
  autoAvatar?: boolean;
  // Описание внешности для фотопортрета (англ.); пусто — берётся из роли/описания персоны
  avatarPrompt?: string;
}

// Тело обновления персоны (PUT /api/personas/{id}) — все поля опциональны
export type UpdatePersonaDto = Partial<CreatePersonaDto>;

// === Долгая память персоны (этап 3) ===
// Тип записи памяти:
//  - semantic   — факты («Факты»): устойчивые сведения о мире/пользователе
//  - episodic   — эпизоды («Эпизоды»): что произошло в конкретном разговоре
//  - procedural — приёмы («Приёмы»): как персона привыкла что-то делать
export type PersonaMemoryType = 'semantic' | 'episodic' | 'procedural';

// Запись долгой памяти персоны
export interface PersonaMemoryEntry {
  id: string;
  personaId: string;
  type: PersonaMemoryType;
  text: string;
  tags?: string[];
  salience: number;           // значимость (для приоритизации/забывания)
  sourceSessionId?: string;   // чат, из которого запомнилось
  pending?: boolean;          // предложено autolearn, ждёт подтверждения (③-3.2)
  createdAt: string;
  lastAccessedAt: string;
}

// Рабочий фокус персоны — «что я сейчас делаю» (одна ячейка рабочей памяти,
// живёт отдельно от записей; в recall подмешивается первым блоком)
export interface PersonaWorkingFocus {
  what: string;
  status: string;
  nextStep?: string;
  sourceSessionId?: string;
  updatedAt: string;
}

// Результат семантического поиска по памяти
export interface PersonaMemoryHit {
  id: string;
  type: PersonaMemoryType;
  text: string;
  tags: string[];
  score: number;
  createdAt: string;
}

// Тип записи командной памяти: решение / договорённость(конвенция) / факт / термин
export type TeamMemoryType = 'fact' | 'decision' | 'convention' | 'glossary';

// Источник записи командной памяти: вручную / автоизвлечение из хода / из совещания
export type TeamMemorySource = 'manual' | 'autoTurn' | 'autoMeeting';

// Запись общей памяти команды проекта (③-3.4) — recall'ят все персоны команды
export interface TeamMemoryEntry {
  id: string;
  ownerId: string;
  projectId: string;
  text: string;
  type: TeamMemoryType;
  tags?: string[];
  salience: number;
  source: TeamMemorySource;
  sourceSessionId?: string;
  createdAt: string;
}

// Элемент манифеста recall (F3): что персона подтянула в ход. Kind ∈ memory|note|knowledge|team
// (team — память команды проекта, ③-3.4).
export interface RecallItem {
  kind: string;
  ref?: string | null;
  title: string;
  snippet?: string | null;
}

// Черновик персоны из AI-формирования команды (POST /api/personas/ai/team)
export interface TeamMemberDraft {
  name?: string;
  role?: string;
  description?: string;
  character?: string;
  tone?: string;
  specialty?: string;
  color?: string;
  greeting?: string;
  // Описание внешности для фото-аватара (англ.) — используется при автогенерации после создания
  avatarPrompt?: string;
}

// ===== Уведомления (центр уведомлений) =====

// alert — тревоги телеметрии (SigNoz). Отдельный вид, а не 'info': иначе срочное
// тонет среди саммари и дайджестов, и отфильтровать его нечем.
export type NotificationKind = 'reminder' | 'claude' | 'info' | 'success' | 'meeting' | 'alert';

export interface NotificationItem {
  id: string;
  kind: NotificationKind;
  type: string;
  title: string;
  body: string;
  url?: string;
  projectId?: string;
  sessionId?: string;
  taskId?: string;
  source?: string;
  tag?: string;
  // Атрибуция персоны (аватар/лицо на уведомлении) + имя проекта
  personaId?: string;
  personaName?: string;
  personaRole?: string;
  personaColor?: string;
  personaHasAvatar?: boolean;
  projectName?: string;
  isRead: boolean;
  createdAt: string;
  readAt?: string;
}

export interface NotificationListResponse {
  items: NotificationItem[];
  totalCount: number;
  unreadCount: number;
}

export interface CreateNotificationRequest {
  kind: NotificationKind;
  type: string;
  title: string;
  body: string;
  url?: string;
  projectId?: string;
  sessionId?: string;
  taskId?: string;
  source?: string;
  tag?: string;
}

// ===== Аналитика расхода токенов (Spend Analytics v2, /api/spend/*) =====

// Токены везде объектом — форма ответа бэкенда (docs/architecture/spend-analytics-api.md)
export interface SpendTokens {
  input: number;
  output: number;
  cacheRead: number;
  cacheCreation: number;
  total: number;
}

// Точка дневного ряда обзора/виджета; aggregated — день свёрнут (за детальным окном)
export interface SpendDayPoint {
  date: string;
  aggregated: boolean;
  total: number;
  bySource: Record<string, number>;
  falGenerations: number;
}

// Строка карточки-топа обзора и узел pivot-дерева (одна форма у бэкенда)
export interface SpendCardRow {
  key: string;               // id значения; "" — узел «без значения»
  name: string | null;       // null — сущность удалена
  meta: string | null;       // у чатов: "chat" | "task"
  tokens: SpendTokens;
  turns: number;
  falGenerations: number;
}

export interface SpendOverviewResponse {
  from: string;
  to: string;
  detailDays: number;
  windowStart: string;
  allUsers: boolean;
  totals: SpendTokens;
  turns: number;
  falGenerations: number;
  // Оценка объёма по тарифам API за период (сумма SpendRecord.CostUsd), $.
  // «Модели и расход» → плитка «По тарифам API»: бэк отдаёт не всегда —
  // без поля плитка просто не рисуется.
  costUsd?: number | null;
  byDay: SpendDayPoint[];
  cards: {
    users: SpendCardRow[];
    projects: SpendCardRow[];
    models: SpendCardRow[];
    chats: SpendCardRow[];
    personas: SpendCardRow[];
    sources: SpendCardRow[];
    providers: SpendCardRow[];
  };
  topTurns: SpendTurnDto[];
}

export interface SpendPivotNode extends SpendCardRow {
  hasDetail: boolean;        // false — узел целиком из дневных агрегатов (🔒 ходы недоступны)
}

export interface SpendPivotResponse {
  nodes: SpendPivotNode[];
}

// Замер размера постановки задачи по секциям (один запуск исполнителя).
// Только размеры в символах — содержимого постановки и заметок здесь нет by design.
export interface SpendTaskPromptRun {
  at: string;
  taskId: string;
  ownerId: string;
  projectId: string | null;
  sessionId: string | null;
  personaId: string | null;
  totalChars: number;
  totalTokensEst: number;
  task: number;
  expected: number;
  tools: number;
  rules: number;
  restrictions: number;
  delegation: number;
  omo: number;
  context: number;
  notes: number;
}

export interface SpendTaskPromptResponse {
  runs: SpendTaskPromptRun[];
}

export interface SpendTurnDto {
  id: string;
  timestamp: string;
  ownerId: string;
  userName: string | null;
  sessionId: string | null;
  chatName: string | null;
  projectId: string | null;
  projectName: string | null;
  taskId: string | null;
  taskTitle: string | null;
  personaId: string | null;
  personaName: string | null;
  provider: string | null;
  model: string | null;
  source: string;            // chat-turn | one-shot | fal | free
  label: string | null;      // подпись one-shot действия или endpoint fal
  tokens: SpendTokens;
  generations: number;
  durationMs: number | null;
  own: boolean;              // ход текущего пользователя → можно «Открыть чат»
}

export interface SpendTurnsResponse {
  total: number;
  windowClamped: boolean;    // в периоде были свёрнутые дни — их ходы недоступны
  items: SpendTurnDto[];
}

export interface SpendTurnDetailResponse {
  turn: SpendTurnDto;
  neighbors: { id: string; timestamp: string; total: number }[];
}

export interface SpendWidgetResponse {
  today: SpendTokens;
  week: SpendTokens;
  todayTurns: number;
  weekTurns: number;
  weekFalGenerations: number;
  byDay: SpendDayPoint[];
}

export interface SpendBadgeResponse {
  sessionId: string;
  total: SpendTokens;
  turns: number;
  lastTurn: SpendTurnDto | null;
}

// --- Бэкапы (админский раздел) ---

export interface BackupSummary {
  chats: number;
  personas: number;
  tasks: number;
  notes: number;
  projects: number;
  users: number;
  totalBytes: number;
}

export interface BackupEntry {
  fileName: string;
  createdAt: string;
  size: number;
  summary: BackupSummary;
}

export interface BackupStatus {
  // Всё из секции Backup конфига — UI их не меняет, только показывает
  enabled: boolean;
  // Куда архивы ложатся фактически (при пустом Backup:Path — папка по умолчанию)
  effectivePath: string;
  secretsPath: string;
  intervalHours: number;
  lastSuccessAt: string | null;
  lastError: string | null;
  lastAttemptAt: string | null;
  recent: BackupEntry[];
}

// --- Ридер ссылок (docs/adr/ADR-005-link-reader-server.md) ---

// Причины отказа — таблица §6 ADR-005; тексты для человека рисует фронт (readerErrors.ts)
export type ReaderErrorCode =
  | 'invalid-url' | 'local-address' | 'dns-failed' | 'unreachable' | 'tls-invalid'
  | 'timeout' | 'auth-required' | 'blocked-by-site' | 'not-found' | 'server-error'
  | 'too-many-redirects' | 'not-a-page' | 'pdf' | 'too-large' | 'not-readable';

export interface ReaderPage {
  title: string;
  siteName?: string | null;
  byline?: string | null;
  markdown: string;
}

export interface ReaderError {
  code: ReaderErrorCode;
  // Для диагностики — человеку не показывается (ADR §6)
  httpStatus?: number | null;
}

// --- MCP-серверы: личный реестр владельца (фича mcp-registry) ---

// Пара «имя — значение» из env/headers. Секретное значение наружу не выходит:
// value = null при hasValue = true (в форме вместо него отметка «задано»).
export interface McpValue {
  name: string;
  value?: string | null;
  hasValue: boolean;
  secret: boolean;
}

export interface McpAuth {
  kind: string;                 // none | apikey | bearer | oauth2
  headerName?: string | null;
  hasSecret: boolean;
  authorizationServer?: string | null;
  clientId?: string | null;
  expiresAt?: string | null;
  hasTokens: boolean;
}

// Последнее известное состояние сервера: из system/init хода или из пробы по кнопке.
// status — строка, набор значений живёт на бэке (connected/failed/needs-auth/unknown).
export interface McpServerStatus {
  status: string;
  observedAt: string;
  source: string;               // init | probe
  sessionId?: string | null;
  error?: string | null;
}

export interface McpServer {
  id: string;
  key: string;
  toolKey: string;              // «mcp:<ключ>» — ключ привязки инструмента у персоны
  label: string;
  description?: string | null;
  transport: string;            // stdio | http | sse
  command?: string | null;
  args: string[];
  env: McpValue[];
  url?: string | null;
  headers: McpValue[];
  auth: McpAuth;
  enabled: boolean;
  alwaysLoad: boolean;
  allowReadOnlyPersonas: boolean;
  // Доезжать ли в чаты вне проектов при allow-модели (флаг mcp-allowlist); deny-ветка не читает
  allowOutsideProjects: boolean;
  source: string;               // manual | legacymcpconfig | legacyuserscope
  authVersion: number;
  createdAt: string;
  updatedAt: string;
  status?: McpServerStatus | null;
}

// Наблюдаемый сервер вне личного реестра: записи в реестре нет, есть только наблюдение
// статуса. group — кто подключил сервер и кто им управляет: product — сервисы AI Home
// (tasks, notes, wsp…), integration — интеграции с внешними системами (dify/fal-ai/glif),
// persona-memory — выделенная память персоны-консультанта (pmem_<handle>),
// external — наследство CLI из глобального .mcp.json/~/.claude.json и плагинов.
// Незнакомое значение фронт показывает как external — сервер подключён, скрывать его нельзя
export type McpBuiltinGroup = 'product' | 'integration' | 'persona-memory' | 'external';

export interface McpBuiltinServer {
  key: string;
  group: McpBuiltinGroup;
  status: McpServerStatus | null;
}

// Пара формы: пустое value у секрета = «оставить как было»
export interface McpValueInput {
  name: string;
  value?: string | null;
  secret?: boolean;
}

export interface McpServerUpsert {
  key?: string;
  label?: string;
  description?: string | null;
  transport?: string;
  command?: string | null;
  args?: string[];
  env?: McpValueInput[];
  url?: string | null;
  headers?: McpValueInput[];
  auth?: { kind?: string; headerName?: string | null; secret?: string | null; clientId?: string | null };
  enabled?: boolean;
  alwaysLoad?: boolean;
  allowReadOnlyPersonas?: boolean;
  allowOutsideProjects?: boolean;
}

export interface McpProbeResult {
  ok: boolean;
  status: string;
  serverName?: string | null;
  toolCount?: number | null;
  toolNames?: string[] | null;
  error?: string | null;
}

// Вход по OAuth (волна 7) — ответы POST .../oauth/start и .../oauth/complete
export interface McpOAuthStartResult {
  authorizeUrl: string;
  state: string;
  redirectUri: string;
}
export interface McpOAuthCompleteResult {
  ok: boolean;
  key: string;
}

// Диагностика вызовов MCP-инструментов (GET /api/mcp/calls, только админ)
export interface McpToolStat {
  tool: string;
  calls: number;
  failures: number;
  avgMs: number;
}

export interface McpCallFailure {
  at: string;
  tool: string;
  sessionId?: string | null;
  path: string;
  statusCode: number;
  elapsedMs: number;
}

export interface McpCallsResponse {
  tools: McpToolStat[];
  recentFailures: McpCallFailure[];
}
