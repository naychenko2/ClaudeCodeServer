// Типы домена «Проекты» (вынесены из шапки lib/api.ts и types/index.ts,
// docs/research/frontend-api-split-2026-11.md). types/index.ts ре-экспортирует их через
// `export type *` (ProjectFeature — именованным реэкспортом значения) — внешние импорты не меняются.
// Здесь: карточка Project и её спутники (права доступа, теги, темы, фон, возможности и ключи
// ProjectFeature, устройство, значок), граф кода (CodeGraph*), превью и запуск
// (ProjectService, ExternalPreviewLink…), значки-кандидаты, «Архитектура» и участники.

// Project.boardColumns читает контракт колонок доски из домена «Задач»: связь в графе типов
// (поле было и в хребте), новой междоменной связи не заводим — переносим существующую.
import type { BoardColumn } from '../tasks/types';

// --- Карточка проекта и её спутники (шаг P14b линии разноса) ---

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
  // Участники общего проекта: сводка из DTO (S1-2b, ProjectsController.WithCount →
  // ProjectMembersView.Summary): { count, first }. null у обычного проекта, где
  // вызывающий — владелец, и у старого бэка. Полный список — только в
  // /api/projects/{id}/members; здесь решать «кого пригласили» нельзя.
  members?: ProjectMembersSummary | null;
  // Роль вызывающего в общем проекте (Summary.MyRole): null у обычного проекта и у старого
  // бэка. Где есть — авторитетна; где её нет — право действия по ownerId.
  myRole?: ProjectRoleView;
  // Владелец общего проекта для заголовка «Проект Андрея» и метки на чужом проекте
  // (Summary.OwnerUsername/OwnerDisplayName). null у обычного проекта и у старого бэка.
  ownerUsername?: string | null;
  ownerDisplayName?: string | null;
  // Бывшие участники общего проекта: подпись «больше не в проекте» над их сообщениями.
  // Нет у обычного проекта и у старого бэка
  formerMembers?: { userId: string; displayName?: string | null }[];
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

// Кандидат значка проекта (ADR-009 §2.2): name — имя lucide-иконки из белого списка.
// Сервер валидирует на входе; на фронте достаточно хранить как есть.
export interface GlyphCandidate {
  name?: string | null;
}

// Итог «Собрать архитектуру» (POST /projects/{id}/architecture/generate).
// graphBuiltAt — время снимка графа кода, которым помечена карта (ISO); added/matched —
// новые и сохранённые элементы: повторная сборка ручные описания не трогает.
export interface ArchitectureGenerateResult {
  modelPath: string;
  graphBuiltAt: string | null;
  generatedAt: string;
  containers: number;
  components: number;
  added: number;
  matched: number;
  connectionsAdded: number;
  // Проход 1 «Собрать архитектуру»: внешние системы-кандидаты (тег «кандидат»), элементы из
  // кода, которых больше нет (тег «нет в коде»), снятые пометки и удалённые руками (не
  // пересоздаются); missing/candidatesSkipped — имена для сводки
  candidates?: number;
  markedMissing?: number;
  unmarked?: number;
  skippedDeleted?: number;
  missing?: string[] | null;
  candidatesSkipped?: string[] | null;
  // Проход 2 (withAgent): задача исполнителю, его персона (null — без персоны-архитектора)
  // и код отказа; launch_failed — задача создана, но исполнитель не стартовал
  agentTaskId?: string | null;
  agentPersonaId?: string | null;
  agentError?: string | null;
  warnings?: ArchitectureModelFinding[] | null;
}

// Несостыковка модели (ArchitectureModelValidator на бэке): висящая ссылка, точка связи,
// которой у карточки нет, дубль id. Предупреждение — запись она не блокирует
export interface ArchitectureModelFinding {
  kind: 'dangling_parent' | 'dangling_connection' | 'dangling_flow_step' | 'unknown_handle' | 'duplicate_id';
  elementId: string;
  text: string;
}

// Модель раздела «Архитектура» (GET /projects/{id}/architecture/model). version — SHA-256
// содержимого файла; updatedBy — имя человека или «Сборка из кода».
export interface ArchitectureModelDto {
  exists: boolean;
  content: string | null;
  version: string | null;
  updatedAt: string | null;
  updatedBy: string | null;
  warnings?: ArchitectureModelFinding[];
}

export interface ArchitectureSaveResult {
  version: string;
  updatedAt: string | null;
  updatedBy: string | null;
  warnings?: ArchitectureModelFinding[];
}

// --- Участники общего проекта (shared-projects, план docs/research/shared-projects-plan-2026-10.md §5) ---
//
// Роль участника: editor (читает и пишет) или reader (только читает) — матрица прав
// разреза §2.1. «Владелец» в списке участников не живёт никогда: он выводится из
// Project.OwnerId (ProjectAccess.Check), поэтому ни на бэке, ни здесь его в списке нет.
export type ProjectMemberRole = 'editor' | 'reader';

// Роль ТЕКУЩЕГО человека в проекте. Сервер пока не отдаёт её нигде (в DTO проекта её
// нет — ProjectsController.WithCount), поле резервируем под следующий шаг UI. Сегодня
// право действия определяют по ownerId, а не по этому полю.
export type ProjectRoleView = 'owner' | 'editor' | 'reader';

// Строка списка участников в ответах /api/projects/{id}/members. Владельца в списке нет:
// он приходит отдельным полем ответа (ProjectMembersView.owner). username/displayName/
// addedAt остаются необязательными: старые данные и тесты строят участника из одной пары
// userId + role, а UI в таком случае показывает то, что есть.
export interface ProjectMember {
  userId: string;
  role: ProjectMemberRole;
  username?: string;
  displayName?: string;
  addedAt?: string;
  addedBy?: string;
}

// Владелец проекта в ответе списка участников
export interface ProjectOwnerRef {
  userId: string;
  username: string;
  displayName?: string | null;
}

// Тело GET /api/projects/{id}/members и ответ каждой мутации членства: участники, владелец
// и роль того, кто спрашивает (по ней вид настроек — владелец или участник).
export interface ProjectMembersView {
  members: ProjectMember[];
  owner: ProjectOwnerRef;
  myRole: ProjectRoleView;
}

// Человек в сводке участников (ProjectMembersView.Summary): только опознавательные поля,
// роли нет — она есть в полном списке.
export interface ProjectMemberSummaryRef {
  userId: string;
  username?: string;
  displayName?: string | null;
}

// Сводка участников в DTO проекта (S1-2b): количество + первые трое. Не полный список:
// он только в GET /api/projects/{id}/members (ProjectMembersView).
export interface ProjectMembersSummary {
  count: number;
  first: ProjectMemberSummaryRef[];
}

// Машинные коды отказов операций членства. Текст отказа бэк отдаёт готовым для человека;
// по коду фронт выбирает действие («включить флаг», «это проект на устройстве»,
// «человек уже здесь»). Тело ошибки приносит lib/offline целиком (err.body).
export const MEMBER_REFUSAL = {
  // Рубильник «Общие проекты» выключен на сервере (503)
  SwitchDisabled: 'shared_projects_disabled',
  // Флаг выключен у владельца ИЛИ у приглашаемого (409)
  FlagDisabled: 'flag_disabled',
  // Проект на устройстве — его нельзя открыть другим людям (409)
  LocalProject: 'local_project',
  // Роль не editor/reader (400)
  InvalidRole: 'invalid_role',
  // Пользователя с таким username нет (404)
  UserNotFound: 'user_not_found',
  // Владелец приглашает сам себя (400)
  Self: 'self',
  // Человек уже в списке (409)
  AlreadyMember: 'already_member',
  // Меняют/убирают не участника (404)
  NotMember: 'not_member',
  // Проект не виден вызывающему (404, ответ сервиса членства)
  NotFound: 'not_found',
  // Проект виден, но управляет им только владелец (403)
  NotOwner: 'not_owner',
  // Владелец не может покинуть свой проект (400)
  OwnerCannotLeave: 'owner_cannot_leave',
  // Не удалось подготовить git-репозиторий проекта (500)
  GitInitFailed: 'git_init_failed',
  // Отказ точки доступа [ProjectScope] до тела действия: 404 «не найдено» или 403
  // «нет права на запись» — один код на оба (ProjectScopeAttribute.ErrorCode)
  ProjectScope: 'project_scope',
} as const;
export type MemberRefusalCode = (typeof MEMBER_REFUSAL)[keyof typeof MEMBER_REFUSAL];

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
