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
  builtInSystemPrompt?: string;
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
  priority: TaskPriority;
  dueDate?: string;          // YYYY-MM-DD
  dueTime?: string;          // HH:MM
  reminderMinutes?: number;  // офсет напоминания до срока в минутах (0 = в момент срока)
  reminderSentAt?: string;   // UTC-отметка отправленного напоминания
  assignee?: TaskAssignee;
  recurrence?: TaskRecurrence;
  seriesId?: string;         // общий id серии регулярной задачи
  linkedSessionId?: string;
  claudeStartedAt?: string;  // отметка запуска Claude-исполнителя
  claudeResult?: 'success' | 'error';  // итог последнего запуска (null — выполняется/не запускалась)
  linkedFiles: string[];
  subtasks: TaskSubtask[];
  labels: string[];
  createdAt: string;
  updatedAt: string;
  // UI-проекция повторяющейся задачи в календаре (не приходит с бэка):
  // occurrenceOf — id реального экземпляра серии, который надо открыть по клику;
  // virtual — признак вычисленного будущего повтора (реально существует только один экземпляр)
  occurrenceOf?: string;
  virtual?: boolean;
}

export interface CreateTaskDto {
  title: string;
  description?: string;
  status?: TaskStatus;
  priority?: TaskPriority;
  dueDate?: string;
  dueTime?: string;
  reminderMinutes?: number;
  assignee?: TaskAssignee;
  recurrence?: TaskRecurrence;
  linkedSessionId?: string;
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
  linkedFiles?: string[];
  subtasks?: TaskSubtask[];
  labels?: string[];
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
  | { type: 'workflow_progress'; toolUseId: string; agents: WorkflowAgentInfo[]; isDone: boolean }
  | { type: 'task_changed'; action: 'created' | 'updated' | 'deleted'; task: Task }
  | { type: 'notes_changed'; action: 'created' | 'updated' | 'deleted'; noteId?: string }
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

// Элементы чата
export type ChatItem =
  | { kind: 'user_message'; text: string; attachedPaths?: string[] }
  | { kind: 'session_started'; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[] }
  | { kind: 'text'; text: string }
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
  agents: AgentInfo[];
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

export interface CreateNoteDto {
  title: string;
  content?: string;
  source?: string;
  templateId?: string;
}

export interface UpdateNoteDto {
  title?: string;
  content?: string;
}
