import type { Mode } from '../lib/modes';
export type { Mode };

export interface PermissionRule {
  pattern: string;
  action: 'allow' | 'deny';
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
}

// Колонка Kanban-доски проекта. category — семантическая категория статуса
// (за ней recurrence/календарь/Claude/MCP); несколько колонок могут делить категорию.
export interface BoardColumn {
  id: string;
  name: string;
  category: TaskStatus;   // 'todo' | 'inProgress' | 'done'
  color?: string;
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
  claudeStartedAt?: string;  // отметка запуска Claude-исполнителя
  claudeResult?: 'success' | 'error';  // итог последнего запуска (null — выполняется/не запускалась)
  linkedFiles: string[];
  subtasks: TaskSubtask[];
  labels: string[];
  // Связь с чекбоксом заметки (флаг notes-task-sync)
  sourceNoteId?: string;
  sourceNoteLine?: number;
  order: number;             // порядок карточки на Kanban-доске (ручная сортировка)
  createdAt: string;
  updatedAt: string;
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
  linkedFiles?: string[];
  subtasks?: TaskSubtask[];
  labels?: string[];
  order?: number;            // порядок карточки на доске (drag); undefined = не менять
  columnId?: string;         // колонка доски проекта; undefined = не менять, '' = сброс на дефолт
}

// Тип доступа к Claude: подписка (стоимость ≈ API-эквивалент) или оплата по API-ключу (реальная цена)
export type ClaudeBilling = 'subscription' | 'api';

export interface AppSettings {
  defaultProjectsPath: string;
  claudeBilling?: ClaudeBilling;
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
  model?: string;
  effort?: string;
  agentName?: string;
  // Временный чат: авто-удаление через N минут после последней активности (updatedAt)
  expiresAfterMinutes?: number | null;
  // Цикл «до готово» (флаг work-loop); null/отсутствует — цикл выключен
  workLoop?: { promise: string; iteration: number; maxIterations: number; phase: 'working' | 'verifying' } | null;
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
}

// WebSocket сообщения от сервера — sessionId присутствует во всех типах
export type ServerMessage = { sessionId: string } & (
  | { type: 'session_started'; claudeSessionId: string; isResume: boolean; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[] }
  | { type: 'text_delta'; text: string }
  | { type: 'thinking_delta'; text: string }
  | { type: 'tool_use'; id: string; name: string; input: unknown; parentToolUseId?: string }
  | { type: 'tool_input_delta'; toolUseId: string; partialJson: string }
  | { type: 'tool_result'; toolUseId: string; content: string; isError: boolean }
  | { type: 'permission_request'; requestId: string; toolName: string; toolInput: unknown }
  | { type: 'ask_question'; toolUseId: string; input: unknown }
  | { type: 'plan_review'; requestId: string; plan: string }
  | { type: 'file_changed'; path: string; added: number; removed: number }
  | { type: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[] }
  | { type: 'fal_cost'; requestId: string; endpointId?: string; costUsd: number; outputUnits?: number; unitPrice?: number }
  | { type: 'error'; text: string }
  | { type: 'rate_limit'; limitType: string; resetsAt?: string; status?: string; utilization?: number; isUsingOverage?: boolean; overageStatus?: string; overageResetsAt?: string }
  | { type: 'compact_boundary'; trigger: string; preTokens?: number; postTokens?: number }
  | { type: 'compact_status'; status?: string; compactResult?: string; compactError?: string }
  | { type: 'truncated' }
  | { type: 'redacted_thinking' }
  | { type: 'exited' }
  | { type: 'status_changed'; status: string; lastMessage?: string; messageCount?: number }
  | { type: 'chat_deleted' }
  | { type: 'workflow_progress'; toolUseId: string; agents: WorkflowAgentInfo[]; isDone: boolean }
  | { type: 'task_changed'; action: 'created' | 'updated' | 'deleted'; task: Task }
  | { type: 'notes_changed'; action: 'created' | 'updated' | 'deleted'; noteId?: string }
  | { type: 'knowledge_changed'; action: string; datasetId?: string }
  | { type: 'personas_changed'; action: 'created' | 'updated' | 'deleted' | 'memory'; personaId?: string }
  | { type: 'speaker_changed'; personaId: string; label: string }
  | { type: 'work_loop'; active: boolean; iteration: number; maxIterations: number; phase: string | null }
  | { type: 'meeting_progress'; meetingId: string; phase: string; personaId?: string; status?: string; error?: string }
  | { type: 'meeting_phase'; meetingId: string; phase: MeetingPhaseKey; question: string; entries: MeetingEntryItem[] }
  | { type: 'pipeline_progress'; pipelineId: string; phase: string; status?: string; error?: string }
  | { type: 'pipeline_phase'; pipelineId: string; phase: PipelinePhaseKey; task: string; personaId: string; text: string; round?: number }
  | { type: 'notification'; title: string; body: string; url?: string; kind: 'reminder' | 'claude' | 'info' }
);

export interface UsageInfo {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
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

// Снимок использования окна во времени (история с бэка, data/usage.json) — для экрана usage и тренда
export interface UsageSnapshot {
  timestamp: string;
  limitType: string;
  utilization?: number;
  status?: string;
  isUsingOverage?: boolean;
  resetsAt?: string;
  overageStatus?: string;
  overageResetsAt?: string;
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

// === Совещание персон (P7, флаг persona-group-chats) ===

// Фазы совещания: независимые позиции → перекрёстная критика → синтез ведущей
export type MeetingPhaseKey = 'independent' | 'attack' | 'synthesis';

// Реплика участника фазы; isError — персона не ответила (text = текст ошибки)
export interface MeetingEntryItem {
  personaId: string;
  text: string;
  isError: boolean;
}

// Live-статусы участников текущей фазы (из meeting_progress)
export interface MeetingRunningState {
  phase: string;
  persona: Record<string, 'running' | 'done' | 'error'>;
}

// Live-состояние цикла «до готово» (из события work_loop; флаг work-loop)
export interface WorkLoopState {
  active: boolean;
  iteration: number;
  maxIterations: number;
  phase: string | null;
}

// Фазы конвейера пантеона: анализ → план → ревью → исполнение
export type PipelinePhaseKey = 'analysis' | 'plan' | 'review' | 'execute';

// Завершённая фаза конвейера (карточка в ленте). round — круг доработки плана (по REJECT)
export interface PipelinePhaseItem {
  phase: PipelinePhaseKey;
  personaId: string;
  text: string;
  round: number;
}

// Элементы чата
export type ChatItem =
  // viaAgent — сообщение прислано не человеком, а агентом из другой сессии (chats_send);
  // senderPersonaId — персона-автор (рендерим сообщение её лицом);
  // systemDirective — служебная директива цикла «до готово» (компактная плашка вместо пузыря);
  // auto — опубликовано автоматически (не человеком): совещание/конвейер/задача — показываем источник
  | { kind: 'user_message'; text: string; attachedPaths?: string[]; viaAgent?: boolean; senderPersonaId?: string; systemDirective?: boolean; auto?: boolean }
  | { kind: 'session_started'; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[] }
  // personaId — авторство реплики (персона на момент хода); после смены собеседника
  // старые реплики сохраняют прежний аватар. Отсутствует у обычного ассистента.
  | { kind: 'text'; text: string; personaId?: string }
  | { kind: 'thinking'; text: string; expanded: boolean }
  | { kind: 'tool_use'; id: string; name: string; input: unknown; result?: string; isError?: boolean; parentToolUseId?: string; streamingArg?: string; workflowAgents?: WorkflowAgentInfo[]; workflowDone?: boolean }
  | { kind: 'permission_request'; requestId: string; toolName: string; toolInput: unknown; resolved: boolean }
  | { kind: 'ask_question'; toolUseId: string; input: unknown; resolved: boolean; answers?: Record<string, string | string[]> }
  | { kind: 'plan_review'; requestId: string; plan: string; resolved: boolean; approved?: boolean; feedback?: string }
  | { kind: 'file_changed'; path: string; added: number; removed: number }
  | { kind: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[] }
  | { kind: 'fal_cost'; requestId: string; endpointId?: string; costUsd: number; outputUnits?: number; unitPrice?: number }
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
  // Карточка совещания персон: агрегат всех фаз одного meetingId (собирается из
  // meeting_phase/meeting_progress и из истории — StoredMeetingPhaseMessage)
  | { kind: 'meeting'; meetingId: string; question: string;
      phases: Partial<Record<MeetingPhaseKey, MeetingEntryItem[]>>;
      running?: MeetingRunningState; status?: 'running' | 'done' | 'error'; error?: string }
  // Карточка конвейера пантеона: последовательность фаз одного pipelineId
  // (собирается из pipeline_phase/pipeline_progress и из истории)
  | { kind: 'pipeline'; pipelineId: string; task: string; phases: PipelinePhaseItem[];
      runningPhase?: string | null; status?: 'running' | 'done' | 'error'; error?: string }
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

export interface AuthState {
  serverUrl: string;
  token: string;
  username: string;
  role?: string;
  id?: string;
}

export interface UserProfile {
  id: string;
  username: string;
  role: 'admin' | 'user';
  createdAt: string;
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

// Сводка изменений за один день (по всем проектам)
export interface ChangelogDay {
  date: string; // yyyy-MM-dd
  items: ChangelogItem[];
  degraded?: boolean;       // сводку собрать не удалось — пункты сырые (subject'ы коммитов)
  degradedReason?: string;  // что сломалось и как это починить
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
  handle: string;             // машинное имя (@handle) — уникально у владельца
  description?: string;
  systemPrompt?: string;      // «характер» — legacy-текст (у персон без contract)
  contract?: PersonaContract | null; // структурированный контракт (P1)
  model?: string;
  effort?: string;
  scope: PersonaScope;
  projectId?: string;         // задан только для scope === 'project'
  avatar: PersonaAvatar;
  greeting?: string;          // приветствие персоны в начале чата
  memoryEnabled: boolean;     // долгая память (этап 2)
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
  connectedPersonaId?: string | null;
}

// === Привязки персоны: «знания и правила» (фича persona-bindings) ===
// Тип источника: project — проект целиком; projectPath — папка/файл проекта;
// knowledge — база знаний (Dify-датасет); notes — источник заметок; tool —
// инструмент workspace; skill — глобальный навык.
export type PersonaBindingType = 'project' | 'projectPath' | 'knowledge' | 'notes' | 'tool' | 'skill';

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
  meta?: string | null;
}

// Тело создания персоны (POST /api/personas). Большинство полей опциональны.
export interface CreatePersonaDto {
  name: string;
  role?: string;
  description?: string;
  systemPrompt?: string;
  // Контракт характера; при обновлении: undefined — не менять, пустые слоты — сбросить
  contract?: PersonaContract;
  model?: string;
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
