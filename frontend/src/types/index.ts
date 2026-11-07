import type { Mode } from '../lib/modes';
import type { PromptCacheWire } from '../lib/promptCache';
import type { RecallItem } from '../features/personas/types';
import type { SessionTeamImplement, TeamEscalation, TeamEscalationAction, TeamEscalationKind, TeamImplementBudget, TeamImplementStage, TeamPlan, TeamWaveLiveness } from '../features/team/types';
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

// Тема чатов проекта (docs/features/chat-subjects.md). В коде корень
// Subject: слово Topic занято значком чата (Session.topic). color в v1 в UI не выставляется.
export interface ProjectSubject {
  id: string;
  name: string;
  order: number;
  color?: string | null;
  createdBy: 'ai' | 'human';
  createdAt: string;
}

// Строка PUT /api/projects/{id}/subjects: id известной темы — правка, без id — новая тема
// человека. order не передан — порядок по позиции в запросе (бэк сравнивает его со старым,
// и у темы модели любое изменение переводит её в human)
export interface ProjectSubjectEdit {
  id?: string;
  name: string;
  order?: number;
  color?: string | null;
}

// Состояние разбора чатов по темам (GET/POST /api/projects/{id}/subjects/run).
// started есть только у ответа POST: false — проход уже шёл, второй не запущен
export interface SubjectRunState {
  // busy — только в ответе запуска: идёт разбор другого проекта, этот не запущен
  state: 'idle' | 'running' | 'busy';
  done: number;
  total: number;
  lastError?: string | null;
  finishedAt?: string | null;
  started?: boolean | null;
}

// Фон рабочего пространства проекта (ADR-008). Wire-формат совпадает
// с ProjectBackgroundView бэка: kind — lowercase-строка, tileVersion — имя файла
// тайла (оно же cache-buster у GET tile.svg, только при generated), failReason — класс
// отказа. У проекта без фона поле отсутствует/null (генерацию не пробовали).
export type ProjectBackgroundKind = 'pending' | 'generated' | 'standard' | 'failed';

export interface ProjectBackground {
  kind: ProjectBackgroundKind;
  tileVersion?: string | null;
  failReason?: string | null;   // no-model | bad-json | rejected | io
}

// Ответ POST .../background/generate|reset (ADR-008 §7): итог операции с судьбой
// предложенного моделью цвета. colorApplied=false + suggestedColorKey — сервер цвет не
// трогал (он выбран руками), фронт показывает диалог подтверждения смены цвета.
export interface BackgroundResult {
  kind: ProjectBackgroundKind;
  tileVersion?: string | null;
  suggestedColorKey?: string | null;
  colorApplied: boolean;
  failReason?: string | null;
}

// Группа возможностей проекта (ADR-016 §4). ЕДИНСТВЕННАЯ точка правды о доступности
// подсистемы у конкретного проекта — компоненты спрашивают «есть ли мой ключ в features
// и доступна ли группа», а НЕ deviceId. Сторож G10 (ProjectCapabilitiesGuardTests) ловит
// инлайновые проверки локальности вне lib/projectCapabilities.ts.
export type ProjectCapabilityHost = 'server' | 'device' | 'off';

export interface ProjectCapabilityGroup {
  host: ProjectCapabilityHost;
  available: boolean;
  // Готовый текст для человека, почему группа недоступна (например «Устройство офлайн»);
  // null если доступна
  reason: string | null;
  // Состав группы — ключи возможностей (см. ниже)
  features: string[];
}

// Матрица возможностей проекта (ADR-016 §4) — фронтовая проекция ProjectCapabilities.cs
// бэка. host: где работает ход (сервер или устройство); deviceId: id устройства для
// локального, null для серверного. exec: можно ли запустить ход прямо сейчас (reason —
// готовый текст причины).
export interface ProjectCapabilitiesView {
  host: 'server' | 'device';
  deviceId: string | null;
  files: ProjectCapabilityGroup;
  platform: ProjectCapabilityGroup;
  serverContent: ProjectCapabilityGroup;
  // Механики, читающие транскрипт CLI с диска сервера; у старого бэка поля нет
  transcript?: ProjectCapabilityGroup;
  exec: { available: boolean; reason: string | null };
  // Плагины владельца (ADR-031): не группа, а отдельная возможность — доступность
  // считает ProjectCapabilities.Plugins (у локального проекта в MVP выключена:
  // каталог пакетов и секреты на устройство не едут). У старого бэка поля нет
  plugins?: { available: boolean; reason: string | null };
}

// Ключи возможностей (ADR-016 §4, ключи ProjectFeatures на бэке). Фронт держит свой
// enum-литерал рядом с DTO: менять его синхронно с ProjectFeatures.cs при расширении.
// Состав групп зафиксирован контрактом DTO из 3.1.
export const ProjectFeature = {
  Files: 'files',
  Diff: 'diff',
  Git: 'git',
  FileWatcher: 'fileWatcher',
  Terminal: 'terminal',
  DevServers: 'devServers',
  Skills: 'skills',
  Attachments: 'attachments',
  Chat: 'chat',
  History: 'history',
  Tasks: 'tasks',
  Memory: 'memory',
  Personas: 'personas',
  Notes: 'notes',
  Costs: 'costs',
  Tts: 'tts',
  Knowledge: 'knowledge',
  CodeGraph: 'codeGraph',
  Dossiers: 'dossiers',
  Docs: 'docs',
  MapHygiene: 'mapHygiene',
  LiveSubagents: 'liveSubagents',
  WorkflowView: 'workflowView',
  ChatBranch: 'chatBranch',
  // Руки на устройстве (ADR-016 §7): ни в одну группу не входят — доступность решает
  // handsRefusal проекта, а не состояние группы (зеркало ProjectFeatures.Hands)
  Hands: 'hands',
  // Плагины владельца (ADR-031): ни в одну группу не входят — доступность считает
  // capabilities.plugins (у локального проекта в MVP выключена, причина — в reason)
  Plugins: 'plugins',
} as const;
export type ProjectFeatureKey = (typeof ProjectFeature)[keyof typeof ProjectFeature];

// Устройство проекта глазами веб-морды (ADR-008 + ADR-016 §4): null у серверного проекта
// и у локального с отозванным устройством. Доп. поля нужны для отображения статуса в
// диалогах и хедере — «устройство офлайн» видно и в проекте, и в чате
export interface ProjectDeviceView {
  id: string;
  name: string;
  online: boolean;
  platform: string | null;
  agentVersion: string | null;
  harnessReady: boolean;
  harnessProblem: string | null;
}

export interface Project {
  id: string;
  name: string;
  rootPath: string;
  relativePath?: string;
  createdAt: string;
  updatedAt: string;
  ownerId?: string;          // владелец проекта; бэк отдаёт, фронту нужно для гейта фона
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
  // Реестр тем чатов и нижняя граница фонового разбора: чаты старше
  // неё фон не трогает, их раскладывает кнопка. null — фон у проекта ещё не проходил
  subjectRegistry?: ProjectSubject[];
  subjectsSweepSince?: string | null;
  // Ключи серверов личного MCP-реестра, ВКЛЮЧЁННЫХ в этом проекте (allow-list):
  // сервер едет в ход только там, где явно включён. Пусто/нет — не включён никто
  mcpServersOn?: string[] | null;
  // Персона-«руководитель проекта»: дефолт для новых чатов проекта;
  // null/отсутствует — онбординг проекта ещё не пройден (гейт в WorkspacePage)
  defaultPersonaId?: string | null;
  // Живая сессия онбординга проекта — для резюма прерванного интервью
  onboardingSessionId?: string | null;
  // Каркас знакомства v2: null/отсутствует — проект создан до фичи (не предлагаем),
  // "pending" — новый (можно предложить), "none" — человек отказался, иначе — ключ пресета
  presetKey?: string | null;
  // Фон рабочего пространства: null/отсутствует — генерацию не пробовали
  // (стандартный фон). Меняет только владелец проекта
  background?: ProjectBackground | null;
  // Автоимпорт «Историй решений»: фоновый наблюдатель ветки ccs/dossiers/v1 в Git-репозитории
  // проекта (по новому tip подтягивает записи коллег). Гейт — отдельный тумблер в настройках;
  // бэкенд-сервис включается только когда поле true.
  autoImportDossiers?: boolean;
  // Порог автоправила архива ЭТОГО проекта (дней без активности, флаг
  // chat-auto-archive). null/отсутствует — своего порога нет, наследуется личный
  // порог владельца; настройка проекта личный порог не трогает
  archiveAfterDays?: number | null;
  // Локальный проект (ADR-016 §4): null — серверный. У локального — id устройства из
  // реестра ADR-008. САМО поле читать нельзя — только через projectCapabilities.ts
  deviceId?: string | null;
  // Карточка устройства для UI; null у серверного проекта и у локального с отозванным
  // устройством. Через неё показываем имя/онлайн/харнес — она же идёт в «устройство
  // офлайн» в шапке чата
  device?: ProjectDeviceView | null;
  // Матрица возможностей проекта (ADR-016 §4). Обязательна у новых ответов; для старого
  // бэка может быть null — тогда capabilities вырождаются в «серверный проект, всё доступно»
  capabilities?: ProjectCapabilitiesView | null;
  // Руки локального проекта (ADR-016 §7): тумблер проекта
  handsEnabled?: boolean;
  // Почему тумблер рук включить нельзя; null — можно. Готовый текст с сервера
  // (ProjectCapabilities.HandsRefusal). Читать только через projectCapabilities.ts
  handsRefusal?: string | null;
  // Только в ответе создания и перепривязки локального проекта: агент устройства создал или
  // разрешил папку («Папка «…» создана и разрешена агенту на «…»»). Показывается уведомлением
  folderNotice?: string | null;
}

// Иконка проекта (ADR-009): initials — две буквы на цветной плитке; glyph — значок из
// белого списка lucide (только name). Проверка показа значка — СТРОГО положительная
// (`kind === 'glyph'`, не `kind !== 'initials'`): старая запись с числовым Kind = 1
// (бывший Image) должна тихо деградировать в инициалы, а не притворяться глифом.
// color — ключ палитры AGENT_COLORS, красит плитку и глиф через currentColor.
// image — картинка, загруженная владельцем (ревизия ADR-009 от 01.10.2026), в родных цветах.
export interface ProjectIcon {
  kind: 'initials' | 'glyph' | 'image';
  color?: string;
  // Имя lucide-значка. Проверка на клиенте: glyph присутствует и name непустой.
  // Иначе — рисуем инициалы (§7 ADR-009).
  glyph?: { name?: string | null } | null;
  // Имя файла загруженной картинки — оно же cache-buster у api.projects.iconImageUrl
  imageFile?: string | null;
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
export type CodeGraphNodeKind =
  | 'Class' | 'Interface' | 'Struct' | 'Enum'
  | 'Component' | 'Hook' | 'UiPrimitive' | 'Util';
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
  excludeFolders?: string[] | null;  // подпапки, вычитаемые из выбранных папок; null — исключений нет
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

export type * from '../features/dossiers/types';

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
  // Поля сферы (флаг spheres): имя значка из белого списка lucide и устав («Чем занимается сфера»)
  icon?: string | null;
  charter?: string | null;
}

// Сфера — группа проектов с командой персон и памятью; на проводе это тот же /api/project-groups
export type Sphere = ProjectGroup;

// GET /api/spheres/{id}/overview
export interface SphereOverview {
  sphere: { id: string; name: string; color: string; icon: string | null; charter: string | null };
  projects: { id: string; name: string; capabilities: ProjectCapabilitiesView }[];
  team: { id: string; name: string; handle: string; role: string | null }[];
  memory: { count: number };
  openTasks: {
    id: string; title: string; projectId: string | null;
    status: 'todo' | 'inProgress' | 'done'; priority: 'low' | 'medium' | 'high' | 'urgent'; dueDate: string | null;
  }[];
}

// Память сферы: две полки — сама сфера и проекты сферы (GET /api/spheres/{id}/memory)
export type SphereMemoryType = 'decision' | 'convention' | 'fact' | 'glossary';
export interface SphereMemoryPromotion { projectId: string; entryId: string; at: string }
export interface SphereMemoryEntry {
  id: string; scopeId: string; text: string; type: SphereMemoryType; salience: number; source: string;
  createdAt: string; promotedFrom: SphereMemoryPromotion | null;
}
export interface ProjectMemoryEntry {
  id: string; projectId: string; text: string; type: string; salience: number; source: string; createdAt: string;
}
export interface SphereMemoryProjectShelf { projectId: string; projectName: string; entries: ProjectMemoryEntry[] }
export interface SphereMemoryResponse { sphere: SphereMemoryEntry[]; projects: SphereMemoryProjectShelf[]; maxEntries: number }

// Ответ DELETE /api/project-groups/{id}: сколько записей памяти ушло вместе со сферой
export interface DeleteSphereResponse { deletedMemory: number }

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
  description: string;      // markdown; у облегчённого элемента списка (listItem) всегда ''
  // Облегчённый элемент СПИСКА (GET /api/tasks, /projects/{id}/tasks): description='' и
  // resultMarkdown отсутствуют, вместо них — признаки и превью. Полный текст — через
  // ensureFullTask(id) (GET /api/tasks/{id}); у полной задачи (ответ create/update/get,
  // realtime task_changed) поля listItem нет.
  listItem?: boolean;
  hasDescription?: boolean;
  hasResult?: boolean;
  descriptionPreview?: string;   // начало описания (≤200 символов + «…» при усечении)
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
  // Режим «Исполнение фичи»: связь задачи с фичей (эпик) и кодом шага.
  // Аддитивно и nullable — старые задачи без разметки не несут этих полей.
  featureId?: string | null;   // id задачи-эпика
  stepRef?: string | null;    // код шага из плана («Ш1», «D1c»)
  lane?: string | null;       // линия шага («бэк», «фронт»); подпись берётся как есть
  // Роль задачи в фиче (бэк TaskItem.Role): feature · cut · plan · step · rework · review · qa · final · merge · consult.
  role?: string | null;
  // Чат, породивший задачу (бэк TaskItem.SourceSessionId). Для фичи — связь эпика с чатом:
  // GET /api/tasks/features?sessionId= выбирает эпики по этому полю (и ExecutorLaunchedBySessionId).
  sourceSessionId?: string | null;
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
  // Тип карточки: обычная задача или дефект (правила DefectRules). У обычных задач — 'task'
  kind?: TaskKind;
  // Шаги воспроизведения дефекта (null у обычных задач; для дефекта — всегда присылается)
  repro?: DefectRepro | null;
  // Подтверждение проверки (null — не подтверждено). Сбрасывается при уходе из Done
  verification?: TaskVerification | null;
  // Критерии приёмки и свидетельства по ним (пусто/нет — задача без критериев)
  acceptance?: AcceptanceCriterion[];
  evidence?: AcceptanceEvidence[];
  // Последний результат предгейта (бэк TaskItem.Pregate): бывает только у шага/доработки;
  // null/нет — не прогонялся. Аддитивно: старые задачи читаются без поля.
  pregate?: PregateRecord | null;
  // Исход дефекта: 'closedWithoutCheck' — внутренний путь закрытия без проверки
  outcome?: DefectOutcome | null;
  // Фикс-волна 4 team-blocker-honest: метки от терминального отказа хода исполнителя
  // (см. ExecutorStopClassifier). executorStoppedAt != null — карточка ждёт человека.
  executorStoppedAt?: string;
  executorStopReason?: string;
  // Явное ожидание человека (Update AwaitingHumanSince): с какого момента и почему.
  // «Ход идёт» (спиннер «AI работает») — только без остановки и без ожидания,
  // тот же предикат, что бэк FeatureProjector.IsTurnRunning.
  awaitingHumanSince?: string;
  awaitingHumanReason?: string;
  /** Исполнитель ждёт устройство локального проекта (ADR-016): с какого момента и почему */
  deviceWaitSince?: string | null;
  deviceWaitReason?: string | null;
  // Метка снятия человеком по карточке блокера (DropSubtaskAsync): если стоит —
  // карточка закрыта человеком, правка статуса от агента отвергается. Человек может
  // снять метку, перетащив задачу обратно в Todo/InProgress.
  droppedByHumanAt?: string;
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
  // Тип карточки: обычная задача или дефект (правила DefectRules на бэке).
  // Не указано — сервер сохраняет прежний Kind. Repro/Verification имеют смысл
  // только у дефектов, но бэк их не трогает если они при присылке type=task —
  // переключение прячет блоки в UI, данные остаются в JSON
  kind?: TaskKind;
  // Шаги воспроизведения. Целиком заменяет объект на бэке — форма шлёт все
  // три поля, иначе пользователь потерял бы expected/actual при правке только steps
  repro?: DefectRepro | null;
  // Подтверждение проверки: на бэке PersonaId подставляется из сессии (X-Caller-Session-Id),
  // клиентский PersonaId не учитывается. null — очистить (снять вердикт)
  verification?: TaskVerification | null;
}

// Тип карточки. Defect подчиняется правилам DefectRules на бэке: нельзя закрыть
// без Verification (или Outcome=closedWithoutCheck), нельзя попасть в колонку Role=review
// без Repro.steps
export type TaskKind = 'task' | 'defect';

// Шаги воспроизведения дефекта: steps — обязательное описание, expected/actual —
// необязательные комментарии автора и наблюдателя
export interface DefectRepro {
  steps: string;
  expected?: string;
  actual?: string;
}

// Критерий приёмки задачи и свидетельство его выполнения (связь по criterionId)
export interface AcceptanceCriterion { id: string; text: string }
export interface AcceptanceEvidence { criterionId: string; how: string; result: string }

// Результат предгейта шага (бэк record PregateRecord): sha — коммит, по которому гнали проверки;
// green — вердикт; summary — вывод красных проверок; at — момент (UTC);
// redStreak — сколько красных подряд по одному redKey (зелёный обнуляет);
// nothingApplicable — «нечего проверять» (green при этом false, «зелёным» не считается).
export interface PregateRecord {
  sha: string;
  green: boolean;
  summary: string;
  at: string;
  redStreak: number;
  redKey?: string | null;
  nothingApplicable?: boolean;
}

// Подтверждение проверки: personaId == null означает проверку человеком (UI-форма
// владельца задачи), не-null — конкретной персоной (чаще всего — исполнившей дефект).
// Поле бэк подставляет из сессии, клиент его НЕ шлёт
export interface TaskVerification {
  verifiedAt: string;
  personaId?: string | null;
  notes?: string | null;
}

// Исход дефекта — сейчас только внутренний путь «снято без проверки»
// (NoteTaskSyncService / TeamWaveService). Прочие исходы придут следующими волнами
export type DefectOutcome = 'closedWithoutCheck';

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
  // Тип карточки: undefined = не менять, 'defect'/'task' = сменить (см. CreateTaskDto)
  kind?: TaskKind;
  // Шаги воспроизведения дефекта. Целиком заменяет объект (как Labels) — форма шлёт
  // все три поля разом. null — очистить
  repro?: DefectRepro | null;
  // Подтверждение проверки: null — очистить (например, при возврате из Done)
  verification?: TaskVerification | null;
  // Исход дефекта: только 'closedWithoutCheck' в этой волне. undefined = не менять
  outcome?: DefectOutcome | null;
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
  stage: 'dev' | 'beta' | 'stable' | 'experimental';
}

// Привязка чата картинки к файлу проекта (ADR-018 §1). Пути — от корня проекта через «/»
export interface SessionImageChat {
  // null — чат-черновик «Нарисовать картинку»: файла ещё нет, есть папка назначения
  currentPath: string | null;
  draftFolder?: string | null;
  // Прежние пути, старые первыми: по ним поиск отдаёт «разговор продолжился на новой версии»
  lineage: string[];
}

// Ручной запуск генерации из редактора в ленте чата картинки (StoredImageLaunchMessage):
// by — "human" | "agent", estimate — котировка на момент запуска
export interface ImageLaunchFields {
  by: string;
  prompt: string;
  provider: string;
  model: string;
  count: number;
  estimate?: { amount: number | null; unit: string; approx: boolean; source: string } | null;
  jobId: string;
  timestamp?: number;
}

// Запись модуля в ленте чата (StoredModuleRecord / ModuleRecordMessage, ADR-019 §2): data —
// JSON модуля, ядро его не разбирает; fallback — текст строки, когда модуля нет
export interface ModuleRecordFields {
  module: string;
  recordType: string;
  data?: unknown;
  fallback: string;
  timestamp?: number;
}

// Пометка снимка холста у сообщения чата картинки (ADR-018 §3, StoredUserMessage.ImageSnapshot):
// revision — ревизия холста на момент отправки; attached=false — холст не менялся, снимок не приложен
export interface ImageSnapshotMark {
  revision: string;
  attached: boolean;
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
  // Чат картинки (ADR-018 §1): тип задаётся при создании, внутри меняется только путь
  imageChat?: SessionImageChat | null;
  mode: Mode;
  // Инструменты, разрешённые в этом чате без вопроса («Всегда разрешать …»). Скоуп —
  // инструмент целиком: «Bash» = любые команды этого чата. Разрешение постоянное
  // (переживает рестарт), снимается через api.sessions.revokeAutoAllow.
  // Необязательное: старые ответы и бэкенд без этой правки поля не присылают
  autoAllowTools?: string[];
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
  // Серверная отметка прочтения чата (синк непрочитанности между устройствами);
  // null/отсутствует — через API не отмечали. Непрочитанность считает chatReadState.ts:
  // updatedAt > max(локальная отметка, эта, baseline)
  lastReadAt?: string | null;
  // Последнее сообщение, написанное владельцем руками (не автоматика и не исполнитель);
  // по нему сортируется список проектов. null — с появления поля сюда не писали
  lastUserMessageAt?: string | null;
  // Чат заглушён: браузерные уведомления по нему не показываются
  notificationsMuted?: boolean;
  // Голосовой режим чата: ответы озвучиваются (POST /api/tts)
  voiceMode?: boolean;
  // Стиль озвучки: 'talk' — ответ короткий целиком (разговор), 'digest' — ответ обычный,
  // вслух читается выжимка из блока <voice> в его конце. Пусто = talk.
  // Выбор принадлежит УСТРОЙСТВУ (localStorage), здесь — последнее выставленное значение
  voiceStyle?: string | null;
  // Цикл «до готово» (флаг work-loop); null/отсутствует — цикл выключен
  workLoop?: { promise: string; iteration: number; maxIterations: number; phase: 'working' | 'waiting' | 'verifying' } | null;
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
  // «Продолжить в новом чате»: из какого чата продолжен этот и в какой продолжен он сам.
  // Поля могут отсутствовать (старый бэкенд) — читать как null
  continuedFromSessionId?: string | null;
  continuedInSessionId?: string | null;
  // Тема чата: id из Project.subjectRegistry. Висячий id (тему
  // удалили) читается как «без темы». subjectSource: 'ai' — разложила модель, 'human' —
  // закрепил человек (в т.ч. «убрал из темы» при subjectId = null), null — никто
  subjectId?: string | null;
  subjectSource?: 'ai' | 'human' | null;
  subjectAssignedAt?: string | null;
  subjectTriedAt?: string | null;
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
  // Онбординг-сессия: "user" — гейт первого входа,
  // "project" — гейт проекта; отсутствует у обычных чатов. По признаку фронт прячет
  // командные механики («Обсудить с командой») — команды в интервью ещё нет
  onboardingKind?: 'user' | 'project' | null;
  // Архив чатов (план «Архив чатов» v4, шаги 4-5). isArchived — готовый bool с бэка,
  // производный от ArchivedAt и UpdatedAt (см. Session.IsArchived в C#). На фронте
  // НЕ пересчитываем — вторая копия правила ломалась бы на равных таймстемпах.
  // Читать поле напрямую не надо: единая точка — isArchivedChat (lib/chatFilters).
  // ArchiveSummary/ArchiveSummaryAt — кэш сводки карточки архива (ChatDigestService);
  // SummaryNoteId проставляется SessionSummaryService при «Сохранить в заметки».
  // ArchivedAt — для совместимости с серверной сериализацией (не используется на
  // чтении фронтом: достаточно bool isArchived).
  isArchived?: boolean;
  archivedAt?: string | null;
  archiveSummary?: string | null;
  archiveSummaryAt?: string | null;
  summaryNoteId?: string | null;
}

// Тип материала контекста чата — те же значения, что SessionContextTypes на бэке
// (file/url/task); union, чтобы магические строки не расползались по UI
export type SessionContextType = 'file' | 'url' | 'task';

// Запись контекста чата (фича chat-context): материал, приложенный к чату явной
// кнопкой. id — путь от корня проекта (file) | URL (url) | id задачи (task).
// title — подпись со стороны добавившего; missing приходит только из GET
// {sessionId}/context (единственная точка расчёта — SessionContextResolver
// на бэке), в событии context_updated и ответе PUT его нет
export interface SessionContextEntry {
  type: SessionContextType;
  id: string;
  title?: string | null;
  missing?: boolean;
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
  // Серверная отметка прочтения (синк между устройствами) — для точки активности проекта
  lastReadAt?: string | null;
  // Родительский чат по той же связи, что у дерева; null — корневой
  parentSessionId?: string | null;
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

export type * from '../features/git/types';

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
  // delegationTaskId — доклад о завершении делегированной задачи: id задачи структурным
  // полем (см. ChatItem user_message), а не вытащенный из текста маркера
  | { type: 'user_message'; text: string; attachedPaths?: string[]; senderPersonaId?: string; auto?: boolean; senderOrigin?: string; senderChatName?: string; staffNote?: string; timestamp?: number; delegationTaskId?: string; imageSnapshot?: ImageSnapshotMark | null }
  // Гостевая реплика персоны без агентского хода (0 токенов) — доклад о завершении
  // делегированной задачи (модель Z); маркер доклада распознаётся на рендере (см.
  // lib/delegationReport.ts). Живой аналог StoredTextMessage.PersonaId из истории.
  // delegationTaskId — задача этого доклада (StoredTextMessage.DelegationTaskId)
  | { type: 'guest_text'; text: string; personaId: string; timestamp?: number; delegationTaskId?: string }
  // Черновик плагина build-it-yourself изменился (ADR-031, шаг 4.2): наблюдатель
  // data/plugins/{owner}/_drafts/{slug} шлёт владельцу сигнал обновить предпросмотр.
  // slug + монотонная ревизия; без содержимого файлов и хостовых путей.
  | { type: 'plugin_draft_changed'; slug: string; revision: number }
  // Установка плагина изменилась (S6): install/обновление/grant/enable/uninstall/секрет.
  // Без полезной нагрузки (владельцу): клиент сам перечитывает GET /api/plugins.
  | { type: 'plugins_changed' }
  | { type: 'thinking_delta'; text: string }
  // Текст/thinking сабагента (Task/Agent) — целыми блоками, с привязкой к родительскому tool_use
  | { type: 'agent_text'; parentToolUseId: string; text: string }
  | { type: 'agent_thinking'; parentToolUseId: string; text: string }
  // startedAt/finishedAt — Unix-мс по часам сервера (таймер карточки инструмента)
  | { type: 'tool_use'; id: string; name: string; input: unknown; parentToolUseId?: string; startedAt?: number }
  | { type: 'tool_input_delta'; toolUseId: string; partialJson: string }
  // Фактический старт инструмента (task_started CLI: Bash, агенты) — сдвигает отсчёт таймера
  | { type: 'tool_started'; toolUseId: string; startedAt?: number }
  // Живой прогресс идущего инструмента поверх таймера карточки (ToolProgress)
  | ({ type: 'tool_progress'; toolUseId: string } & ToolProgress)
  | { type: 'tool_result'; toolUseId: string; content: string; isError: boolean; finishedAt?: number }
  // Фоновые агенты (Agent run_in_background / Workflow) реально завершились — единственный
  // достоверный сигнал «ответ готов» (по task-notification CLI). aborted=true — агенты
  // умерли вместе с процессом (Стоп/несовместимый ход), не доработав
  | { type: 'bg_agent_done'; toolUseIds: string[]; aborted?: boolean }
  // У чата появилась/закончилась живая фоновая работа. Событие для СПИСКА чатов, а не для
  // ленты; приходит только на СМЕНЕ состояния. active — агенты (карточка светится, робот),
  // command — фоновая команда Bash (тихий значок: она живёт часами и завершения не шлёт)
  | { type: 'bg_agents_presence'; active: boolean; command: boolean }
  | { type: 'permission_request'; requestId: string; toolName: string; toolInput: unknown }
  | { type: 'ask_question'; toolUseId: string; input: unknown }
  | { type: 'plan_review'; requestId: string; plan: string }
  // Человек ответил на карточку (вопрос / разрешение / план) с ДРУГОГО устройства — гасим
  // свою копию формы. У отвечающего клиента карточка уже погашена оптимистично, событие
  // для него холостое. kind: question → id это toolUseId и решение в answers;
  // permission → id это requestId и решение в decision; plan → requestId + approved/feedback
  | { type: 'interaction_resolved'; kind: 'question' | 'permission' | 'plan'; id: string;
      answers?: Record<string, string | string[]>; decision?: 'allowed' | 'denied' | 'always';
      approved?: boolean; feedback?: string }
  // external — правку сделали не Edit/Write этого чата (человек в IDE, форматтер);
  // фронт снимает кнопку «Откатить» и помечает карточку «Изменение вне чата»
  | { type: 'file_changed'; path: string; added: number; removed: number; external?: boolean }
  // contextTokens — размер контекста последнего запроса хода (оценка заполнения окна).
  // usage для этого не годится: он суммирует все запросы хода, включая сабагентов.
  // usageModel — фактическая модель хода (её же бэк проставляет постам в истории):
  // на этом событии лента клеит модель постам текущего хода.
  // promptCache — состояние prompt cache хода (фич-флаг prompt-cache); null/отсутствует —
  // ход без сигнала кэша и история до появления поля
  | { type: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[]; contextTokens?: number; usageModel?: string; durationApiMs?: number; promptCache?: PromptCacheWire }
  | { type: 'fal_cost'; requestId: string; endpointId?: string; costUsd: number; outputUnits?: number; unitPrice?: number }
  // Завершённая генерация glif: счётчик + кредиты (если billing доехал в payload). Дедуп по jobId.
  | { type: 'glif_cost'; jobId: string; outputType?: string; mediaCount: number; credits?: number; model?: string }
  // text — человекочитаемая формулировка сбоя, details — сырой технический текст
  // (ответ CLI, .NET-исключение): в ленте он живёт только под «Подробностями».
  // action — признак предлагаемого действия под карточкой: единственное значение
  // "window-1m-drop" рисует кнопку «Продолжить в стандартном окне» (POST /api/chats/{id}/window-1m/drop).
  // null/undefined — обычная ошибка, кнопки нет. Сравнение по строке, а не по тексту —
  // формулировка ошибки живёт на сервере и может меняться
  | { type: 'error'; text: string; details?: string; action?: string | null }
  | { type: 'rate_limit'; limitType: string; resetsAt?: string; status?: string; utilization?: number; isUsingOverage?: boolean; overageStatus?: string; overageResetsAt?: string }
  | { type: 'compact_boundary'; trigger: string; preTokens?: number; postTokens?: number }
  // Прокси локальной модели обрезал историю хода (kind='prune'), увёл сжатие в облако
  // (kind='compact_cloud') либо перехватил петлю модели в потоке (kind='loop_guard': blocks —
  // сколько петель, resultBlocks — был ли повтор запроса, thinkingBlocks — петля в размышлениях). Приходит ПОСЛЕ первого байта ответа — до него лента молчит, и
  // это нормально: своего «ожидания» у прунинга нет.
  // blocks — сколько блоков выкинуто всего, дальше разбивка по видам (вывод инструмента,
  // вход инструмента, размышление). prefillSeconds — сколько ждали пересчёт префикса после
  // сдвига; cacheReadTokens/promptTokens — доля запроса, взятая из кэша (cacheRead/prompt).
  // eventId — личность сдвига: внеходовая рассылка веерная (session- + project-группа), и
  // вкладка открытого чата состоит в обеих, то есть получает одно событие дважды. По eventId
  // редьюсер отличает вторую доставку от второго сдвига (карточки до появления поля — без него)
  | {
      type: 'context_pruned'; kind: 'prune' | 'compact_cloud' | 'loop_guard'; eventId?: string;
      tokensBefore: number; tokensAfter: number;
      blocks: number; resultBlocks: number; inputBlocks: number; thinkingBlocks: number;
      prefillSeconds?: number; cacheReadTokens?: number; promptTokens?: number;
      // Сколько старых картинок прокси вырезал из истории (всего за чат) и их оценка в токенах
      imageBlocks?: number; imageTokens?: number;
    }
  | { type: 'compact_status'; status?: string; compactResult?: string; compactError?: string }
  // Живое заполнение контекста посреди хода (после каждого ответа модели основного агента);
  // транзитное, в историю не попадает — итог хода несёт result.contextTokens
  | { type: 'context_usage'; contextTokens: number }
  // Записи ленты чата картинки (ADR-018 §1, §2): живые копии StoredImageLaunchMessage и
  // StoredImageFileMovedMessage. Рисует их модуль редактора через слот chat-item-tool
  | ({ type: 'image_launch' } & ImageLaunchFields)
  | { type: 'image_file_moved'; from: string; to: string; timestamp?: number }
  // Запись модуля в ленте (ADR-019 §2): живая копия StoredModuleRecord
  | ({ type: 'module_record' } & ModuleRecordFields)
  | { type: 'truncated' }
  | { type: 'redacted_thinking' }
  | { type: 'exited' }
  | { type: 'status_changed'; status: string; lastMessage?: string; messageCount?: number }
  | { type: 'chat_deleted' }
  // Чат убран в архив или возвращён из архива (archived — направление): архив ПРЯЧЕТ
  // чат, а не удаляет (на chat_deleted строятся ChatsPage/awaiting, здесь — нет).
  // Принимает строго по этому типу: подписки на chat_archived не должны случайно
  // отрабатывать событие удаления, и наоборот.
  | { type: 'chat_archived'; archived: boolean }
  | { type: 'chat_renamed'; name: string; topic?: string | null }
  // «Продолжить в новом чате»: исходный чат (sessionId) продолжён в newSessionId.
  // Живое событие: если открыт именно исходный чат, фронт сам переходит в новый (тост).
  // projectId — для построения навигации (проектный чат); null — чат вне проекта.
  | { type: 'chat_continued'; newSessionId: string; projectId?: string | null }
  // Сторожа чатов: ПОЛНЫЙ состав id чатов и проектов владельца с активными
  // сторожами. Событие глобальное (не про один чат) и заменяет состояние стора целиком —
  // диффы потребитель не считает
  | { type: 'watchdogs_changed'; sessions: string[]; projects: string[] }
  // Состав контекста чата заменён (фича chat-context): полный состав после PUT.
  // entries — записи БЕЗ признака missing (бэкенд его в broadcast не кладёт);
  // нужен missing — читай кеш (GET {sessionId}/context), событие его обновит
  | { type: 'context_updated'; entries: SessionContextEntry[] }
  | { type: 'workflow_progress'; toolUseId: string; agents: WorkflowAgentInfo[]; isDone: boolean }
  | { type: 'task_changed'; action: 'created' | 'updated' | 'deleted'; task: Task }
  | { type: 'notes_changed'; action: 'created' | 'updated' | 'deleted'; noteId?: string }
  | { type: 'knowledge_changed'; action: string; datasetId?: string }
  | { type: 'personas_changed'; action: 'created' | 'updated' | 'deleted' | 'memory' | 'default'; personaId?: string }
  // Картинку догнала фоновая генерация (ImageBackfillService): kind — место
  // (project-icon | persona-avatar), entityId — id проекта или персоны. Имя файла картинки
  // при каждой генерации новое (icon-{guid}), поэтому получатель ПЕРЕЧИТЫВАЕТ сущность,
  // а не дёргает прежний URL. Персонам бэк вдобавок шлёт personas_changed
  | { type: 'image_backfilled'; kind: 'project-icon' | 'persona-avatar'; entityId: string }
  // Онбординг завершён: дефолт-персона назначена из онбординг-сессии.
  // Гейт снимается НЕ по этому событию mid-turn, а по концу хода
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
  // недоступна» (см. providerSwitchReasonLabel в lib/providerLimit.ts).
  // errorDetails — сырой текст промежуточной ошибки, которую погасила эта подмена: красной
  // карточки в ленте нет (ход состоялся на другой модели), текст живёт под «Подробностями»
  // внутри маркера подмены
  | { type: 'provider_switched'; provider: string; model?: string; label?: string; auto?: boolean; reason?: string; errorDetails?: string }
  // Лимит подписки исчерпан, в пуле переключиться некуда — предложение продолжить
  // чат на стороннем провайдере (карточка с кнопками)
  | { type: 'provider_limit'; resetsAt?: string; providers: ProviderFallbackOption[] }
  | { type: 'work_loop'; active: boolean; iteration: number; maxIterations: number; phase: string | null; waitingReason?: string | null; waitingTicks?: number }
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
  // resolutionNote — примечание от штаба (chosenActionId="resolvedByStaff"), показывает,
  // чем координатор закрыл карточку. Поле опциональное — старый бэкенд его не шлёт,
  // и фронт рисует карточку с фолбэком («Снят штабом» без подробностей)
  | { type: 'team_escalation'; escalationId: string; kind: TeamEscalationKind; title: string; details: string; actions: TeamEscalationAction[]; taskId: string | null; taskTitle?: string | null; wave: number; resolved: boolean; chosenActionId: string | null; personaId?: string | null; resolutionNote?: string | null }
  // Жизненный цикл вызова планировщика (не путать с team_implement — тот про стадию режима).
  // Транзитное: в историю не пишется, после рестарта не восстанавливается — карточка плана
  // (team_plan) или отказа (team_escalation) уже несут итог. start=true — планировщик запущен;
  // start=false — закончил: success=true → subtaskCount/waveCount/elapsedMs, success=false →
  // failure (готовый текст причины, тот же, что уйдёт в title карточки отказа следом)
  | { type: 'team_planning'; start: boolean; success: boolean; subtaskCount: number; waveCount: number; elapsedMs: number; route: string | null; failure: string | null; promptChars: number; responseChars: number; personaId?: string | null }
  // Пульс волны командной реализации (Э2 КР-наблюдаемости). Эфемерное: в ленту НЕ
  // попадает и в историю НЕ пишется, состояние держится в ChatState.teamWavePulse.
  // Список задач приходит отдельным REST-снапшотом (/chats/{id}/team-wave-snapshot) при
  // открытии поповера бейджа — иначе пришлось бы раздувать каждое событие массивом.
  // stage: бэк шлёт пульс и в финальной проверке — она тоже работа команды и может
  // «зависнуть», отбрасывать её WS-события как мусор было ошибкой
  | { type: 'team_wave_pulse'; stage: 'wave' | 'checking'; waveNumber: number; plannedWaves: number; tasksActive: number; tasksTotal: number; lastActivityAt: string; quietSeconds: number; liveness: TeamWaveLiveness }
  | { type: 'preview_status'; status: string; port?: number; error?: string; serviceId?: string }
  // Вывод дев-сервера — приходит только подписчикам группы конкретного сервиса
  // (JoinPreviewLog), а не всем вкладкам пользователя. data — накопленное за тик
  // (~100 мс) сразу куском: построчная рассылка захлёбывалась на сборках
  | { type: 'preview_log'; serviceId: string; data: string }
  | { type: 'notification'; title: string; body: string; url?: string; kind: 'reminder' | 'claude' | 'info' | 'success' | 'meeting'; notificationId?: string; notifType?: string; projectId?: string; sessionId?: string; taskId?: string; source?: string; tag?: string; personaId?: string; personaName?: string; personaRole?: string; personaColor?: string; personaHasAvatar?: boolean; projectName?: string; sticky?: boolean }
  | { type: 'recall_manifest'; items: RecallItem[] }
  // Полный снимок очереди сообщений занятой сессии (постановка/отмена/доставка).
  // kind: 'user' — сообщение человека из «честной очереди» (рисуется карточкой «Вы» с
  // вложениями и режимом, который вернётся в композер по «Стоп»); 'agent' — chats_send.
  | { type: 'pending_messages'; items: { id: string; text: string; senderPersonaId?: string; senderOrigin?: string; enqueuedAt: string; senderChatName?: string; kind?: 'user' | 'agent'; attachedPaths?: string[]; mode?: string | null; waitingForDevice?: boolean }[] }
  // «Стоп» вернул прерванное пользовательское сообщение в композер (фича «честная
  // очередь»). text=null — восстанавливать нечего (прерван авто/агентский ход, очередь
  // пуста): композер не трогаем. Иначе — текст, вложения и режим, которые подставятся
  // в пустое поле (черновик пользователя важнее — непустой композер игнорируется).
  | { type: 'composer_restore'; text?: string | null; attachedPaths?: string[] | null; mode?: string | null }
  // Подсказка следующего сообщения — чип в композере
  | { type: 'prompt_suggestion'; text: string }
  // Руки локального проекта в чате (ADR-016 §7): эфемерные, в историю не пишутся.
  // state — HandsChatStates, reason — HandsEndReason у stopped, deviceName — имя устройства
  | { type: 'hands_status'; state: string; deviceName?: string | null; reason?: string | null }
  // Строка ленты о руках, не ошибка хода (понижение «Без ограничений» и подобное)
  | { type: 'hands_notice'; text: string }
  // Снимок промпта хода записан: id для кнопки «какой промпт ушёл» под постом.
  // Текст сюда не кладём — шторка забирает его отдельным REST-запросом.
  // applied=false — ход доигрывался в живом процессе, и этот промпт модели не уходил
  | { type: 'prompt_snapshot'; snapshotId: string; applied: boolean; inheritedFromId?: string }
  // Живые метрики локальной модели (флаг local-model-stats). Эфемерные: в историю не
  // пишутся, после F5 подхватываются следующим снимком/REST. phase — фаза генерации
  // (queued|prefill|generating), genTps — ток/с, waitingTotal — сколько прогонов ждут GPU.
  | { type: 'local_model_live'; phase: string; genTps?: number | null; waitingTotal?: number | null }
  // Итоговые метрики завершённого хода на локальной модели: скорость генерации/префилла,
  // time-to-first-token, оценка времени в очереди, доля prefix-кэша, число выданных токенов.
  | { type: 'local_model_turn_stats'; genTps?: number | null; prefillTps?: number | null; ttftMs?: number | null; queueMsApprox?: number | null; cacheHitPerc?: number | null; outTokens?: number | null; mainRequests?: number; subagentRequests?: number; compactionRequests?: number; auxiliaryRequests?: number; agentTypes?: string[] | null; compactionKind?: 'auto' | 'manual' | 'reactive' | null; toolCallLeakTail?: number; toolCallLeakWholeCall?: number }
  // Ход ждёт свободный слот локальной модели (гейт допуска, бэк 4683acb29). В отличие от
  // local_model_live идёт БЕЗ флага local-model-stats: человек обязан видеть, что ход ждёт,
  // а не «виснет молча». position — место в очереди (1-based), active/limit — «заняты N из M».
  // position=0 — ожидание кончилось (ход допущен/отказан/остановлен): строку снять.
  // Эфемерное: в историю не пишется, свою строку ленты снимает и на конце хода (редьюсер).
  | { type: 'local_turn_queue'; position: number; active: number; limit: number }
);

// Снимок промпта хода (GET /api/sessions/{id}/prompt/{snapshotId})
export interface PromptSection {
  key: string;
  title: string;
  text: string;
  // system — часть --append-system-prompt; turn — текст сообщения хода с обвязками;
  // cli-file — файл слоя CLI (CLAUDE.md с раскрытыми импортами); truncated — секция,
  // срезанная ради лимита командной строки Windows (живёт в PromptSnapshot.TruncatedSections)
  kind: 'system' | 'turn' | 'cli-file' | 'truncated';
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
  // Секции, вырезанные из промпта ради лимита командной строки Windows: в модель НЕ ушли,
  // а здесь — чтобы человек видел, чем пришлось пожертвовать. null/пусто — срезки не было,
  // обычный ход. У каждой записи kind='truncated', text — пометка о факте срезки (что
  // вырезано и сколько символов осталось бы), не исходное содержимое. Задача dc641949
  truncatedSections?: PromptSection[] | null;
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
  // Стенд поднят агентом из чата (инструмент start_stand): только подпись — жизнь стенда от
  // чата не зависит
  origin?: { sessionId: string; label: string } | null;
}

// Одна конфигурация из .claude/launch.json (формат Claude Desktop)
// Выданная ссылка внешнего доступа к дев-серверу (поддомен, а не путь /preview/**).
export interface ExternalPreviewLink {
  jti: string;
  projectId: string;
  serviceId: string;
  issuedAt: string;
  expiresAt: string;
  // Имя проекта приходит с сервера: список сквозной, и чужой проект надо назвать
  projectName?: string | null;
}

export interface ExternalLinkIssued {
  url: string;
  jti: string;
  expiresAt: string;
  // Вытесненные потолком ссылки — их закрыли, и человек обязан об этом узнать
  evicted: { jti: string; projectId: string; serviceId: string }[];
}

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
// монотонно, сбрасывается по resetsAt); 'alive' — "6/6", живых платформ из всех (FreeLLM),
// чем больше, тем лучше — не расход и не исчерпание. count/consumed/alive — уже
// отформатированная строка «N/M»
export type ProviderQuotaWindowUnit = 'percent' | 'count' | 'consumed' | 'alive';
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
  // Порог утилизации недельного окна (дефолт бэка 0.95) — вторая причина вывода из ротации:
  // без него бейдж объяснял бы недельную перегрузку фразой «5ч 35% ≥ порога 80%»
  weeklyThreshold?: number;
  // Ключ аккаунта, куда фактически ушёл бы новый чат сейчас (детерминированный выбор):
  // при отсутствии свободных пул спиллит на перегруженный — бейдж показывает это честно
  routingTarget?: string;
  // Снимки окон лимитов сторонних CLI-провайдеров (glm/deepseek) — их Anthropic-совместимые
  // эндпоинты тоже шлют rate_limit_event, снимки пишутся под ключ провайдера
  providers?: Record<string, UsageSnapshot[]>;
  // Статус опроса api/oauth/usage по ключам аккаунтов: "ok" | "unauthorized" (токен
  // не подходит — setup-токен вместо полноценного входа) | "rate_limited" (429: опрос
  // упёрся в лимит частоты, ждёт backoff) | "refresh_rate_limited" (429 на продлении
  // access-токена профиля: вход живой, повтор сам) | "error"
  pollStatuses?: Record<string, string>;
  // Локальная модель (Ollama или llama-server): какая модель и на какие фоновые действия
  // она заведена. Имя поля контрактное и не переименовывается вместе с движком.
  ollama?: OllamaUsageInfo;
}

// Блок «Локальная модель» на экране использования. У локального движка нет лимитов/баланса
// (бесплатно), показываем модель и маршрут каждого фонового действия (локаль/claude).
export interface OllamaUsageInfo {
  enabled: boolean;
  model?: string | null;
  baseUrl?: string | null;
  // Какой движок поднят на бэкенде: 'ollama' | 'llama-server' (конфиг LocalLlm:Provider).
  // Поля может не быть — старый бэкенд его не шлёт, подпись тогда прежняя, «Ollama».
  provider?: string;
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
  // Подпись именованного локального движка (например «Gemma 4»), когда route='local' ведёт на него,
  // а не на основную локальную модель; null/нет — основной движок
  engine?: string | null;
  // Действию нужна сильная модель (лицо продукта, генерация артефактов) — локаль ему не
  // годится; пресеты подбирают Claude/облачную модель. В UI помечается бейджем.
  requiresStrong?: boolean;
  // Агентное место (группа «Чаты и персоны»): запускает сессию claude CLI, поэтому
  // локальная модель и direct:-модели ему недоступны
  agentic?: boolean;
  // Что место получает на вход (ADR-027 §2): 'text' — только текст, 'images' — текст плюс
  // картинки (разметка фото персонажа). Для мест с картинками локаль, direct:-модели,
  // пресеты и модели без зрения недоступны. Старый бэк поле не шлёт — дефолт 'text'.
  input?: 'text' | 'images';
  // Место умеет считать ракурс лица локально (InsightFace, маршрут face-pose) — только
  // место ракурса. Пикер на нём показывает пункт «Локально, InsightFace».
  facePose?: boolean;
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
  // Способности аккаунта по тарифу: пул отсекает его раньше сравнения утилизаций
  // (SupportsModel в ClaudeSubscriptionPool), карточка рисует пилюлю «Без Opus» / «Без 1M»
  // рядом с тарифом. false → не принимает соответствующие ходы (но всё ещё в ротации
  // для доступных моделей); null → поле не пришло со старого бэка снимков (обратная
  // совместимость data/usage.json), пилюлю не рисуем, дефолт true неинформативен.
  supportsOpus?: boolean | null;
  supports1M?: boolean | null;
  // Эффективная утилизация недельного окна (0..1) — вторая ось вывода из ротации наравне
  // с utilization: аккаунт с 5ч 35% и 7д 99% пул уже не берёт (ClaudeSubscriptionPool.IsOverloaded)
  weeklyUtilization?: number;
  // Живые пометки «модель недоступна на ЭТОЙ подписке» (пара подписка × модель). Истёкшие
  // бэкенд не отдаёт; пустой список = пометок нет. Без них подписка выглядит полностью
  // здоровой («В ротации», лимит не исчерпан), а ходы конкретной модели на неё не идут
  unavailableModels?: ModelUnavailableMark[];
}

// Пометка «модель недоступна на подписке». reason — «model_no_access» (нет доступа по
// тарифу) | «model_out_of_credits» (кончились usage credits модели); until — момент, до
// которого пара не пробуется. model — нормализованный id (нижний регистр), в этом же виде
// его ждёт эндпоинт досрочного сброса
export interface ModelUnavailableMark {
  model: string;
  reason: string;
  until: string;
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

// Деньги Yandex Cloud (GET /api/yandex/account): остаток на биллинг-аккаунте и НАШ расход
// на озвучку за период. Баланс приходит строкой — так его отдаёт Billing API, и валюта у него
// своя (обычно RUB). balanceHidden=true — смотрит не админ: кошелёк инстанса ему не показываем.
// spend считает продукт сам: разбивку по услугам Billing API не отдаёт в принципе.
export interface YandexAccountResponse {
  enabled: boolean;
  error?: string | null;
  account?: {
    id: string;
    name?: string | null;
    balance?: string | null;
    currency?: string | null;
    active: boolean;
  } | null;
  asOf?: string | null;
  balanceHidden: boolean;
  days: number;
  spend?: { total: number; requests: number } | null;
}

export type * from '../features/imageGeneration/types';

// Live-состояние цикла «до готово» (из события work_loop; флаг work-loop)
export interface WorkLoopState {
  active: boolean;
  iteration: number;
  maxIterations: number;
  phase: 'working' | 'waiting' | 'verifying' | null;
  // Причина ожидания по маркеру `<waiting>` (только при phase='waiting'). null — обычное
  // ожидание по живой делегированной задаче, счётчик тиков не идёт
  waitingReason?: string | null;
  // Число тиков ожидания (Loop:WaitingTickSeconds, дефолт 300 с). Потолок — Loop:MaxWaitingTicks.
  // 0 — счётчик ещё не стартовал или только что сброшен
  waitingTicks?: number;
}

export type * from '../features/team/types';

// Живой прогресс идущего инструмента (событие tool_progress, ToolProgressMessage на бэке).
// percent — оценка с потолком 95, «готово» — только результат; exact — процент по настоящим
// шагам семплера ComfyUI (потолок 99), полоса сплошная, а не пунктир оценки.
// Сабагент: stage 'working', label — описание, lastTool/toolUses/durationMs.
// Локальная генерация (local_jobs_wait): stage 'queued' | 'running', queuePosition, percent,
// exact, etaSeconds; label — «шаг N из M» и/или «K из N» задач
export interface ToolProgress {
  stage?: string | null;
  label?: string | null;
  percent?: number | null;
  exact?: boolean | null;
  queuePosition?: number | null;
  etaSeconds?: number | null;
  lastTool?: string | null;
  // Клиентское: вид прогона, когда lastTool — run_tests («vitest»); ставит редьюсер по
  // вложенному вызову сабагента, сервер это поле не шлёт
  lastToolKind?: string | null;
  toolUses?: number | null;
  durationMs?: number | null;
  // run_tests: полный снимок этапов и (в последнем событии) итоговые счётчики — редьюсер
  // переносит их в поля карточки stages/totals, они же лежат в истории вызова
  stages?: ToolStage[] | null;
  totals?: ToolRunTotals | null;
}

// Этап долгого инструмента (run_tests, ToolStage на бэке): ключ queued | build | list | stand |
// running, короткая подпись, начало и конец — Unix-мс по часам сервера. endedAt нет — этап идёт
// (или вызов оборвался, не закрыв его); failed — этап кончился неудачей (крестик, а не галочка)
export interface ToolStage {
  stage: string;
  label: string;
  startedAt: number;
  endedAt?: number | null;
  failed?: boolean | null;
}

// Итоговые счётчики прогона тестов для закрытой карточки: «174 из 177 · упало 3»
export interface ToolRunTotals {
  passed: number;
  failed: number;
  total: number;
  // Первые упавшие (до трёх): имя и первая строка сообщения — список под строкой этапов
  failures?: ToolRunFailure[] | null;
}

export interface ToolRunFailure {
  name: string;
  message?: string | null;
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
  // delegationTaskId — доклад о завершении делегированной задачи: id задачи, к которой
  // ведёт карточка доклада. Нет у обычных сообщений и у историй до появления поля
  // imageSnapshot — сообщение из чата картинки: приложен ли снимок холста (ADR-018 §3)
  | { kind: 'user_message'; text: string; attachedPaths?: string[]; viaAgent?: boolean; senderPersonaId?: string; systemDirective?: boolean; auto?: boolean; senderOrigin?: string; senderChatName?: string; staffNote?: string; ts?: number; promptSnapshotId?: string; delegationTaskId?: string; imageSnapshot?: ImageSnapshotMark }
  | { kind: 'session_started'; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[]; turnWorktree?: { path: string; name: string } | null }
  // personaId — авторство реплики (персона на момент хода); после смены собеседника
  // старые реплики сохраняют прежний аватар. Отсутствует у обычного ассистента.
  // parentToolUseId — текст/thinking сабагента: рендерится внутри карточки родительского
  // tool_use (секция «Активность»), а не в основной ленте.
  // model — чем написан ИМЕННО этот пост: приходит из истории либо клеится на result
  // (модель хода известна только к его концу). ts — когда написан (Unix-мс UTC).
  // Оба необязательны: у старой истории их нет, у сабагентского текста модели нет
  // delegationTaskId — реплика-доклад о завершении делегированной задачи (модель Z):
  // по нему карточка доклада открывает задачу. Нет у обычных реплик и у старых историй
  | { kind: 'text'; text: string; personaId?: string; parentToolUseId?: string; model?: string; ts?: number; delegationTaskId?: string }
  | { kind: 'thinking'; text: string; expanded: boolean; parentToolUseId?: string }
  // bgDone/bgAborted — завершение фонового агента (bg_agent_done / bgDone из истории);
  // workflowAborted — workflow восстановлен из истории прерванным (агенты уже не завершатся);
  // startedAt/finishedAt — Unix-мс по часам сервера, нет у старых историй;
  // started — пришёл tool_started (фактический старт Bash/агента), startedAt уже сдвинут на него;
  // appearedAt — прежний startedAt до сдвига (только живая лента): порог индикатора ожидания;
  // progress — последний tool_progress (только живая лента, в историю не пишется);
  // stages/totals — этапы и итог run_tests: из tool_progress вживую и из истории после F5
  | { kind: 'tool_use'; id: string; name: string; input: unknown; result?: string; isError?: boolean; parentToolUseId?: string; streamingArg?: string; workflowAgents?: WorkflowAgentInfo[]; workflowDone?: boolean; workflowAborted?: boolean; bgDone?: boolean; bgAborted?: boolean; startedAt?: number; finishedAt?: number; started?: boolean; appearedAt?: number; progress?: ToolProgress; progressAt?: number; stages?: ToolStage[]; totals?: ToolRunTotals }
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
  // durationApiMs — время запросов к API за ход (без инструментов и ожиданий): знаменатель
  // скорости генерации в плашке итога. Нет поля (старая история, локальный движок) — скорость
  // считается по durationMs
  | { kind: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[]; contextTokens?: number; durationApiMs?: number; promptCache?: PromptCacheWire }
  | { kind: 'fal_cost'; requestId: string; endpointId?: string; costUsd: number; outputUnits?: number; unitPrice?: number }
  | { kind: 'glif_cost'; jobId: string; outputType?: string; mediaCount: number; credits?: number; model?: string }
  | { kind: 'rate_limit'; limitType: string; resetsAt?: string; status?: string }
  | { kind: 'compact_boundary'; trigger: string; preTokens?: number; postTokens?: number }
  // Прокси локальной модели обрезал историю (см. wire-событие context_pruned). Вид сдвига
  // лежит в pruneKind, а НЕ в kind: у элемента ленты и у записи истории kind — дискриминатор
  // (TypeDiscriminatorPropertyName = "kind" в StoredMessage.cs), своего поля с тем же именем
  // запись иметь не может. В wire-событии дискриминатор — type, поэтому там поле зовётся kind.
  // eventId — личность сдвига (StoredContextPrunedMessage.EventId / wire-поле eventId):
  // по ней дедупится повторная доставка одного события. Необязательно: у карточек,
  // записанных до её появления, поля нет
  | {
      kind: 'context_pruned'; pruneKind: 'prune' | 'compact_cloud' | 'loop_guard'; eventId?: string;
      tokensBefore: number; tokensAfter: number;
      blocks: number; resultBlocks: number; inputBlocks: number; thinkingBlocks: number;
      prefillSeconds?: number; cacheReadTokens?: number; promptTokens?: number;
      imageBlocks?: number; imageTokens?: number;
    }
  // Тихие строки чата картинки: «Вы запустили: …» и «Сохранено как …» (ADR-018 §1, §2)
  | ({ kind: 'image_launch' } & ImageLaunchFields)
  | { kind: 'image_file_moved'; from: string; to: string; timestamp?: number }
  // Запись модуля (ADR-019 §2): карточку рисует модуль через слот chat-item-tool по ключу
  // `${module}:${recordType}`, без модуля — строка fallback
  | ({ kind: 'module_record' } & ModuleRecordFields)
  | { kind: 'truncated' }
  | { kind: 'redacted_thinking' }
  // ts — момент остановки (история: StoredInterruptedMessage.Timestamp); в живой ленте нет
  | { kind: 'interrupted'; ts?: number }
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
  // details — сырой текст промежуточной ошибки, погашенной этой подменой (провод:
  // provider_switched.errorDetails, история: StoredModelSwitchedMessage.Details):
  // раскрывается «Подробностями» внутри маркера
  | { kind: 'model_switched'; model: string; previousModel: string; reason?: string; rawLabel?: string; details?: string }
  // Плашка «Ветка от {имя чата}» (фича chat-branch): запись истории нового чата, созданного
  // ветвлением. sourceSessionId — id оригинала, sourceName — имя оригинала (снимок на момент
  // ветвления), ts — Unix-мс UTC (в истории — StoredBranchedFromMessage.Timestamp).
  | { kind: 'branched_from'; sourceSessionId: string; sourceName: string; ts?: number }
  // Карточка-предложение: лимит подписки исчерпан — продолжить на стороннем провайдере.
  // resolved — миграция состоялась (карточка гаснет)
  | { kind: 'provider_limit'; resetsAt?: string; providers: ProviderFallbackOption[]; resolved?: boolean }
  | { kind: 'git_turn_commit'; projectId: string; sha: string; subject: string }
  // Остановка цикла «до готово»: текст готов на сервере (лимит/ошибка/ручной стоп),
  // фронт его не собирает — иначе разъедется с сервером при смене лимита
  | { kind: 'work_loop_stopped'; reason: string; text: string }
  // Строка ленты о руках локального проекта (hands_notice или остановка рук на устройстве).
  // Live-only: события эфемерные, в историю не пишутся
  | { kind: 'hands_notice'; text: string; tone: 'neutral' | 'warning' }
  // Строка ленты: ход ждёт свободный слот локальной модели (событие local_turn_queue).
  // position — позиция в очереди (1-based), active/limit — «заняты N из M». Live-only:
  // событие эфемерное, в историю не пишется; снимается position=0 или концом хода (редьюсер).
  | { kind: 'local_turn_queue'; position: number; active: number; limit: number }
  // details — сырой технический текст сбоя за человекочитаемым text (см. wire-событие error).
  // action — признак предлагаемого действия под карточкой: сейчас только "window-1m-drop"
  // (кнопка «Продолжить в стандартном окне» под карточкой отказа Window1MUnavailable).
  // undefined — обычная ошибка, кнопки нет. Серверная формулировка может меняться,
  // признак — нет, поэтому сравниваем по строке
  | { kind: 'error'; text: string; canRetry?: boolean; details?: string; action?: string | null; ts?: number }
  // Группа ошибок прошлых дней (QA Fold 8): строится на фронте в ChatPanel из
  // последовательно идущих error с ts < сегодня. Кат/раскрытие на стороне ChatItemView.
  | { kind: 'error_group'; date: number; items: Extract<ChatItem, { kind: 'error' }>[] };

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
// Поля онбординга: defaultPersonaId — личная дефолт-персона
// (null — онбординг не пройден), needsOnboarding — обязательный гейт
// онбординга при входе, onboardingSessionId — живая сессия интервью для резюма.
export interface Me {
  userId: string;
  username: string;
  displayName?: string | null;
  role: string;
  featureFlags?: Record<string, boolean>;
  // Активные подсистемы: массив ключей реально зарегистрированных в процессе
  // (отдаётся `SubsystemStateStore.ActiveKeys()` бэка). В список попадают
  // подсистемы, прошедшие гейт `Subsystems:{Key}:Enabled=true` И зарегистрированные
  // на старте; список короткий, детерминированный. Стороной `Record` бэк НЕ
  // шлёт — `{ ...arr }` на фронте даёт числовые ключи '0','1'…, и гейт ломается.
  // Заполняется App.tsx через `setAllSubsystems(me.subsystems)` в lib/subsystems;
  // стор разворачивает массив в `Record<string, boolean>` внутри. Отдельные
  // вкрапления UI (виджеты, пункты меню, кнопки) гейтятся хуком `useSubsystem('notes')`
  // и дают fail-closed false, пока стор пуст — это нормально, лишь бы поле
  // пришло в me-ответе. Контрактный сторож — subsystems.contract.test.ts.
  subsystems?: string[];
  contextThresholds?: { warnPct: number; dangerPct: number } | null;
  // Личные пороги предупреждения об остывании prompt cache (фича «Индикатор prompt
  // cache и перенос в новый чат», F-3): минуты; null — не настроено, работают
  // дефолты индикатора. Сидирует App.tsx в тот же момент, что и contextThresholds.
  cacheWarnThresholds?: { warn1hMin: number; warn5mMin: number } | null;
  defaultPersonaId?: string | null;
  needsOnboarding?: boolean;
  onboardingSessionId?: string | null;
  // Среда исполнения процессов пользователя: 'local' — на хосте, 'container' —
  // в общей Docker-песочнице cc-sandbox. Карточка каталога MCP-серверов рисует по
  // нему бейдж среды и предупреждение для stdio-записей у local-владельцев. Бэк
  // отдаёт поле в AuthController.MeResponse наравне с role
  executionEnvironment?: 'local' | 'container';
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

export type * from '../features/changelog/types';

// eslint-disable-next-line module/no-cross-feature-import -- types/index.ts ре-экспортирует доменные типы для совместимости внешних импортов (S5-P4); публичного index.ts у notes нет, баррел заведётся отдельно
export type * from '../features/notes/types';

export type * from '../features/knowledge/types';

export type * from '../features/specialties/types';

export type * from '../features/personas/types';

export type * from '../features/notifications/types';

export type * from '../features/spend/types';

export type * from '../features/hostControl/types';

export type * from '../features/reader/types';

export type * from '../features/mcp/types';

export type * from '../features/telemetry/types';

// Быстрая фраза композера: готовое сообщение и необязательная группа (второй
// уровень попапа). Пустая/отсутствующая group — фраза лежит в корне списка.
// Логика набора (порядок, раскладка по группам) — в lib/quickPhrases.ts
export interface QuickPhrase {
  text: string;
  group?: string | null;
}

export type * from '../features/video/types';

export type * from '../features/desktop/types';

export type * from '../features/plan/types';

export type * from '../features/mapHygiene/types';
