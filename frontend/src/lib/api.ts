import type { Project, ProjectGroup, Session, FileEntry, SyncMark, WorkflowAgentInfo, WorkflowAgentBlock, AppSettings, UserProfile, SkillsData, SkillInfo, RegistrySkill, SkillSuggestion, PermissionRule, UsageResponse, FalAccountResponse, FeatureFlagDefinition, SystemPromptPart, Task, CreateTaskDto, UpdateTaskDto, BoardColumn, BoardItem, ChangelogDay, DaySummaryStub, ChangelogStatus, NoteSummary, NoteDetail, NoteBacklink, NoteGraph, NoteSource, NoteFolder, NoteTemplate, NoteSemanticHit, CreateNoteDto, UpdateNoteDto, NoteTask, ExtractTasksResponse, SearchHit, Persona, CreatePersonaDto, UpdatePersonaDto, PersonaScope, PersonaMemoryType, PersonaMemoryEntry, PersonaMemoryHit, PersonaContract, PersonaWorkingFocus, PantheonTemplate, PersonaBinding, PersonaBindingDto, PersonaBindingType, BindingTarget, KnowledgeBaseDetail, KnowledgeSearchHit, CreateKnowledgeBaseDto, KnowledgeListResponse, KnowledgeDocumentContent, TeamMemoryEntry, TeamMemberDraft, PersonaAutomationRule, AutomationRuleDto } from '../types';
import { request } from './offline';

export type { WorkflowAgentInfo, WorkflowAgentBlock };

export interface DifyDocument {
  id: string;
  name: string;
  indexingStatus: string;
  tags?: string[];
}

// Projects
export const api = {
  auth: {
    login: (username: string, password: string) =>
      request<{ token: string; expiresAt: string; username: string }>('/auth/login', {
        method: 'POST',
        body: JSON.stringify({ username, password }),
      }),
    me: () =>
      request<{ userId: string; username: string; role: string; featureFlags?: Record<string, boolean>; contextThresholds?: { warnPct: number; dangerPct: number } | null }>('/auth/me'),
    changePassword: (currentPassword: string, newPassword: string) =>
      request<void>('/auth/password', {
        method: 'PUT',
        body: JSON.stringify({ currentPassword, newPassword }),
      }),
    // Пороги индикатора контекста (per-user); пустой body → сброс к дефолтам
    setContextThresholds: (t: { warnPct?: number; dangerPct?: number }) =>
      request<{ contextThresholds: { warnPct: number; dangerPct: number } | null }>('/auth/context-thresholds', {
        method: 'PUT',
        body: JSON.stringify(t),
      }),
    // Таймзона устройства (IANA) — серверу для расчёта напоминаний по локальным срокам
    setTimeZone: (timeZone: string) =>
      request<void>('/auth/timezone', {
        method: 'PUT',
        body: JSON.stringify({ timeZone }),
      }),
  },

  push: {
    vapidPublicKey: () => request<{ publicKey: string }>('/push/vapid-public-key'),
    subscribe: (sub: { endpoint: string; p256dh: string; auth: string }) =>
      request<void>('/push/subscribe', { method: 'POST', body: JSON.stringify(sub) }),
    unsubscribe: (endpoint: string) =>
      request<void>('/push/unsubscribe', { method: 'POST', body: JSON.stringify({ endpoint }) }),
  },

  users: {
    list: () => request<UserProfile[]>('/users'),
    create: (data: { username: string; password: string; role: string }) =>
      request<UserProfile>('/users', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: string, data: { username?: string; role?: string }) =>
      request<UserProfile>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: string) => request<void>(`/users/${id}`, { method: 'DELETE' }),
    resetPassword: (id: string, newPassword: string) =>
      request<void>(`/users/${id}/password`, { method: 'PUT', body: JSON.stringify({ newPassword }) }),
  },

  settings: {
    get: () => request<AppSettings>('/settings'),
    save: (s: AppSettings) => request<AppSettings>('/settings', { method: 'PUT', body: JSON.stringify(s) }),
  },

  usage: {
    get: () => request<UsageResponse>('/usage'),
  },

  fal: {
    account: (days = 7) => request<FalAccountResponse>(`/fal/account?days=${days}`),
  },

  providers: {
    balance: (key: string) =>
      request<{ available: boolean; currency: string; totalBalance: string }>(`/providers/${key}/balance`),
    usage: (key: string) =>
      request<{
        balance: { available: boolean; currency: string; totalBalance: string } | null;
        snapshots: { timestamp: string; balance: number; currency: string }[];
      }>(`/providers/${key}/usage`),
  },

  models: {
    list: () =>
      request<{
        models: { value: string; displayName: string; description?: string | null; provider?: string | null; contextWindow?: number | null; isCurated?: boolean }[];
        providers?: Record<string, import('./models').ProviderCapabilities>;
      }>('/models'),
  },

  featureFlags: {
    get: () => request<{ definitions: FeatureFlagDefinition[]; values: Record<string, boolean> }>('/feature-flags'),
    set: (key: string, enabled: boolean) =>
      request<{ values: Record<string, boolean> }>(`/feature-flags/${key}`, {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
  },

  projects: {
    list: () => request<Project[]>('/projects'),
    events: (id: string, opts?: { since?: string; type?: string; actor?: string; limit?: number }) => {
      const qs = new URLSearchParams();
      if (opts?.since) qs.set('since', opts.since);
      if (opts?.type) qs.set('type', opts.type);
      if (opts?.actor) qs.set('actor', opts.actor);
      if (opts?.limit) qs.set('limit', String(opts.limit));
      return request<unknown[]>(`/projects/${encodeURIComponent(id)}/events${qs.toString() ? `?${qs}` : ''}`);
    },
    // Память команды проекта (③-3.4)
    teamMemory: (id: string) => request<TeamMemoryEntry[]>(`/projects/${encodeURIComponent(id)}/team-memory`),
    addTeamMemory: (id: string, text: string) =>
      request<TeamMemoryEntry>(`/projects/${encodeURIComponent(id)}/team-memory`, {
        method: 'POST', body: JSON.stringify({ text }),
      }),
    updateTeamMemory: (id: string, entryId: string, text: string) =>
      request<TeamMemoryEntry>(`/projects/${encodeURIComponent(id)}/team-memory/${encodeURIComponent(entryId)}`, {
        method: 'PUT', body: JSON.stringify({ text }),
      }),
    removeTeamMemory: (id: string, entryId: string) =>
      request<void>(`/projects/${encodeURIComponent(id)}/team-memory/${encodeURIComponent(entryId)}`, { method: 'DELETE' }),
    create: (name: string, rootPath: string | null, createDirectory = false, groupId?: string | null) =>
      request<Project>('/projects', { method: 'POST', body: JSON.stringify({ name, rootPath, createDirectory, groupId }) }),
    update: (id: string, data: { name?: string; rootPath?: string; systemPrompt?: string; showHiddenFiles?: boolean; toolsEnabled?: boolean; permissionRules?: PermissionRule[]; groupId?: string | null }) =>
      request<Project>(`/projects/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: string) => request<void>(`/projects/${id}`, { method: 'DELETE' }),
    getBuiltinPrompt: () => request<{ content: string }>('/projects/builtin-prompt'),
    getEffectivePrompt: (id: string) => request<{ parts: SystemPromptPart[] }>(`/projects/${id}/effective-prompt`),
    // Кастомные колонки Kanban-доски проекта (пустой массив → дефолтные 3)
    updateBoardColumns: (id: string, columns: BoardColumn[]) =>
      request<Project>(`/projects/${id}/board-columns`, { method: 'PUT', body: JSON.stringify({ columns }) }),
    // Dev-server live preview
    previewStart: (id: string, command: string, args: string[], port?: number) =>
      request<{ status: string; port?: number; error?: string }>(`/projects/${id}/preview/start`, {
        method: 'POST', body: JSON.stringify({ command, args, port }),
      }),
    previewStop: (id: string) =>
      request<void>(`/projects/${id}/preview/stop`, { method: 'POST' }),
    previewStatus: (id: string) =>
      request<{ status: string; port?: number }>(`/projects/${id}/preview/status`),
  },

  // Доска агентов (диспетчерская)
  board: {
    agents: () => request<{ items: BoardItem[] }>('/board/agents'),
    interrupt: (sessionId: string) =>
      request<void>(`/board/agents/${sessionId}/interrupt`, { method: 'POST' }),
    allowPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/allow`, { method: 'POST' }),
    denyPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/deny`, { method: 'POST' }),
  },

  // Группы проектов
  projectGroups: {
    list: () => request<ProjectGroup[]>('/project-groups'),
    create: (name: string, color: string) =>
      request<ProjectGroup>('/project-groups', { method: 'POST', body: JSON.stringify({ name, color }) }),
    update: (id: string, data: { name?: string; color?: string }) =>
      request<ProjectGroup>(`/project-groups/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    reorder: (orderedIds: string[]) =>
      request<ProjectGroup[]>('/project-groups/reorder', { method: 'POST', body: JSON.stringify({ orderedIds }) }),
    delete: (id: string) => request<void>(`/project-groups/${id}`, { method: 'DELETE' }),
  },

  tasks: {
    // Все задачи пользователя (для календаря)
    listAll: (from?: string, to?: string) => {
      const qs = new URLSearchParams();
      if (from) qs.set('from', from);
      if (to) qs.set('to', to);
      const q = qs.toString();
      return request<Task[]>(`/tasks${q ? `?${q}` : ''}`);
    },
    listByProject: (projectId: string) => request<Task[]>(`/projects/${projectId}/tasks`),
    // Задачи, порученные персоне-исполнителю (assignee=claude + personaId)
    listByPersona: (personaId: string) =>
      request<Task[]>(`/tasks?personaId=${encodeURIComponent(personaId)}`),
    // projectId === null → личная задача (вне проекта)
    create: (projectId: string | null, dto: CreateTaskDto) =>
      request<Task>(projectId ? `/projects/${projectId}/tasks` : '/tasks', { method: 'POST', body: JSON.stringify(dto) }),
    get: (taskId: string) => request<Task>(`/tasks/${taskId}`),
    update: (taskId: string, dto: UpdateTaskDto) =>
      request<Task>(`/tasks/${taskId}`, { method: 'PUT', body: JSON.stringify(dto) }),
    delete: (taskId: string) => request<void>(`/tasks/${taskId}`, { method: 'DELETE' }),
    // Запустить выполнение задачи Claude-ом (отдельная сессия)
    execute: (taskId: string) => request<Task>(`/tasks/${taskId}/execute`, { method: 'POST' }),
    // Генерация Claude: описание по названию (+контекст проекта), подзадачи по описанию
    aiDescription: (title: string, projectId?: string | null) =>
      request<{ description: string }>('/tasks/ai/description', {
        method: 'POST', body: JSON.stringify({ title, projectId: projectId ?? null }),
      }),
    aiSubtasks: (title: string, description: string, projectId?: string | null) =>
      request<{ subtasks: string[] }>('/tasks/ai/subtasks', {
        method: 'POST', body: JSON.stringify({ title, description, projectId: projectId ?? null }),
      }),
  },

  // Заметки (Obsidian-совместимая база знаний): .md файлы в личном vault + notes/ проектов
  notes: {
    list: (source?: string, q?: string) => {
      const qs = new URLSearchParams();
      if (source) qs.set('source', source);
      if (q) qs.set('q', q);
      const s = qs.toString();
      return request<NoteSummary[]>(`/notes${s ? `?${s}` : ''}`);
    },
    sources: () => request<NoteSource[]>('/notes/sources'),
    graph: () => request<NoteGraph>('/notes/graph'),
    templates: () => request<NoteTemplate[]>('/notes/templates'),
    // Резолв по имени вики-ссылки (+ фрагмент по якорю) — hover-preview и embeds
    resolve: (name: string, anchor?: string) => {
      const qs = new URLSearchParams({ name });
      if (anchor) qs.set('anchor', anchor);
      return request<{ note: NoteDetail; fragment: string | null }>(`/notes/resolve?${qs}`);
    },
    // Дневниковая заметка: date — локальная дата клиента YYYY-MM-DD
    daily: (date: string) =>
      request<NoteDetail>('/notes/daily', { method: 'POST', body: JSON.stringify({ date }) }),
    caps: () => request<{ semantic: boolean }>('/notes/caps'),
    semantic: (q: string, topK = 8) =>
      request<{ available: boolean; results: NoteSemanticHit[] }>(
        `/notes/semantic?q=${encodeURIComponent(q)}&topK=${topK}`),
    reindex: () => request<{ changed: number }>('/notes/reindex', { method: 'POST' }),
    // Переименование/перенос папки целиком (newPath — полный новый путь)
    moveFolder: (source: string, path: string, newPath: string) =>
      request<{ notes: { oldId: string; newId: string }[] }>('/notes/folder/move', {
        method: 'POST', body: JSON.stringify({ source, path, newPath }),
      }),
    // Физические папки (в т.ч. пустые) — для дерева и «куда создать»
    folders: () => request<NoteFolder[]>('/notes/folders'),
    createFolder: (source: string, path: string) =>
      request<NoteFolder>('/notes/folder', { method: 'POST', body: JSON.stringify({ source, path }) }),
    deleteFolder: (source: string, path: string) =>
      request<{ removed: number }>(
        `/notes/folder?source=${encodeURIComponent(source)}&path=${encodeURIComponent(path)}`,
        { method: 'DELETE' }),
    // Перенос: в папку и/или другой источник (личный vault ↔ notes/ проекта)
    move: (id: string, folder: string | null, targetSource?: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/move`, {
        method: 'POST', body: JSON.stringify({ folder, targetSource }),
      }),
    linkMention: (id: string, targetTitle: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/link-mention`, {
        method: 'POST', body: JSON.stringify({ targetTitle }),
      }),
    // ✨ one-shot AI: связи, теги, конспект дня
    suggestLinks: (id: string) =>
      request<{ title: string; why: string }[]>(`/notes/${encodeURIComponent(id)}/suggest-links`, { method: 'POST' }),
    suggestTags: (id: string) =>
      request<string[]>(`/notes/${encodeURIComponent(id)}/suggest-tags`, { method: 'POST' }),
    dailySummary: (date: string) =>
      request<NoteDetail>('/notes/daily/summary', { method: 'POST', body: JSON.stringify({ date }) }),
    get: (id: string) => request<NoteDetail>(`/notes/${encodeURIComponent(id)}`),
    backlinks: (id: string) => request<NoteBacklink[]>(`/notes/${encodeURIComponent(id)}/backlinks`),
    create: (dto: CreateNoteDto) =>
      request<NoteDetail>('/notes', { method: 'POST', body: JSON.stringify(dto) }),
    update: (id: string, dto: UpdateNoteDto) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(dto) }),
    delete: (id: string) =>
      request<void>(`/notes/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Задачи из заметок (флаг notes-task-sync): чекбоксы .md ↔ задачи
    tasks: (id: string) => request<NoteTask[]>(`/notes/${encodeURIComponent(id)}/tasks`),
    promoteTask: (id: string, line: number) =>
      request<Task>(`/notes/${encodeURIComponent(id)}/tasks/promote`, {
        method: 'POST', body: JSON.stringify({ line }),
      }),
    toggleTask: (id: string, line: number, done: boolean) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/tasks/toggle`, {
        method: 'POST', body: JSON.stringify({ line, done }),
      }),
    // Срок 📅 на строке-чекбоксе (дейт-пикер в секции); due=null — убрать
    setNoteTaskDue: (id: string, line: number, due: string | null) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/tasks/set-due`, {
        method: 'POST', body: JSON.stringify({ line, due }),
      }),
  },

  // Персоны (олицетворённые ИИ-собеседники): CRUD персон владельца (флаг personas)
  personas: {
    // scope=context&projectId= — только доступные в контексте (глобальные + этого проекта)
    list: (opts?: { scope?: string; projectId?: string }) => {
      const qs = new URLSearchParams();
      if (opts?.scope) qs.set('scope', opts.scope);
      if (opts?.projectId) qs.set('projectId', opts.projectId);
      const s = qs.toString();
      return request<Persona[]>(`/personas${s ? `?${s}` : ''}`);
    },
    get: (id: string) => request<Persona>(`/personas/${encodeURIComponent(id)}`),
    create: (dto: CreatePersonaDto) =>
      request<Persona>('/personas', { method: 'POST', body: JSON.stringify(dto) }),
    update: (id: string, dto: UpdatePersonaDto) =>
      request<Persona>(`/personas/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(dto) }),
    remove: (id: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Чаты, ведущиеся от лица персоны (этап 2): список + создание нового.
    // projectId — глобальная персона, позванная из проекта, получает чат В этом проекте.
    chats: (id: string) => request<Session[]>(`/personas/${encodeURIComponent(id)}/chats`),
    createChat: (id: string, body: { mode?: string; resumeSessionId?: string; name?: string; projectId?: string }) =>
      request<Session>(`/personas/${encodeURIComponent(id)}/chats`, { method: 'POST', body: JSON.stringify(body) }),

    // Пантеон OmO: каталог ролей-специалистов с бэкенда + идемпотентное подключение
    // всей команды (keys не передаём = все роли). После connect прилетит personas_changed.
    pantheon: () => request<{ templates: PantheonTemplate[] }>('/personas/pantheon'),
    connectPantheon: (keys?: string[]) =>
      request<Persona[]>('/personas/pantheon/connect', {
        method: 'POST',
        body: JSON.stringify({ keys: keys ?? null }),
      }),

    // Назначить/снять собеседника чату вне проекта: персона (personaId) либо .md-агент
    // (agentName) — взаимоисключающе, оба null = снять. 400, если чат уже начат.
    assignPersonaToChat: (chatId: string, personaId: string | null, agentName: string | null = null) =>
      request<Session>(`/chats/${encodeURIComponent(chatId)}/persona`, {
        method: 'POST',
        body: JSON.stringify({ personaId, agentName }),
      }),
    // То же для проектной сессии
    assignPersonaToSession: (projectId: string, sessionId: string, personaId: string | null, agentName: string | null = null) =>
      request<Session>(`/projects/${projectId}/sessions/${sessionId}/persona`, {
        method: 'POST',
        body: JSON.stringify({ personaId, agentName }),
      }),

    // Долгая память персоны (этап 3): список / поиск / ручное добавление / забывание.
    // type — необязательный фильтр по категории.
    memory: (id: string, type?: PersonaMemoryType) =>
      request<PersonaMemoryEntry[]>(
        `/personas/${encodeURIComponent(id)}/memory${type ? `?type=${encodeURIComponent(type)}` : ''}`,
      ),
    memorySearch: (id: string, q: string, topK?: number) =>
      request<PersonaMemoryHit[]>(
        `/personas/${encodeURIComponent(id)}/memory/search?q=${encodeURIComponent(q)}${topK ? `&topK=${topK}` : ''}`,
      ),
    remember: (id: string, body: { type: PersonaMemoryType; text: string; tags?: string[] }) =>
      request<PersonaMemoryEntry>(`/personas/${encodeURIComponent(id)}/memory`, {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    updateMemory: (id: string, entryId: string, text: string) =>
      request<PersonaMemoryEntry>(`/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}`, {
        method: 'PUT',
        body: JSON.stringify({ text }),
      }),
    // Насосы Memory↔Notes (③-3.3)
    memoryToNote: (id: string, entryId: string) =>
      request<{ noteId: string; noteTitle: string }>(
        `/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}/to-note`,
        { method: 'POST' },
      ),
    noteToMemory: (id: string, noteId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/memory/from-note`, {
        method: 'POST',
        body: JSON.stringify({ noteId }),
      }),
    forget: (id: string, entryId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}`, {
        method: 'DELETE',
      }),
    // Подтвердить предложенную autolearn запись (③-3.2)
    confirmMemory: (id: string, entryId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}/confirm`, {
        method: 'POST',
      }),

    // Рабочий фокус персоны («что я сейчас делаю»): 204 без фокуса → null
    focus: (id: string) =>
      request<PersonaWorkingFocus | undefined>(`/personas/${encodeURIComponent(id)}/focus`)
        .then(f => f ?? null),
    clearFocus: (id: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/focus`, { method: 'DELETE' }),

    // Аватар (этап 4): можно ли генерировать (настроен ли fal),
    // генерация картинки и построение URL для <img>.
    avatarCaps: () => request<{ generate: boolean }>('/personas/avatar/caps'),
    // Генерация галереи кандидатов: возвращает имена файлов (аватар персоны НЕ меняется
    // до явного выбора). count — сколько вариантов (1..4).
    generateAvatar: (id: string, opts?: { prompt?: string; count?: number }) =>
      request<{ candidates: string[] }>(`/personas/${encodeURIComponent(id)}/avatar/generate`, {
        method: 'POST',
        body: JSON.stringify({
          prompt: opts?.prompt?.trim() || undefined,
          count: opts?.count,
        }),
      }),
    // Выбор кандидата — он становится аватаром персоны, возвращается обновлённая персона
    selectAvatar: (id: string, file: string) =>
      request<Persona>(`/personas/${encodeURIComponent(id)}/avatar/select`, {
        method: 'POST',
        body: JSON.stringify({ file }),
      }),
    // URL картинки-аватара для браузерного <img>: токен уходит через ?access_token=
    // (заголовок Authorization <img> не шлёт — как у notes attachment / files stream).
    // Возвращает null, если у персоны нет картинки. cache-busting по imageFile —
    // иначе после перегенерации браузер покажет старый кадр из кэша.
    avatarUrl: (persona: Persona): string | null => {
      if (persona.avatar?.kind !== 'image' || !persona.avatar.imageFile) return null;
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', persona.avatar.imageFile);
      return `/api/personas/${encodeURIComponent(persona.id)}/avatar?${params}`;
    },
    // Загрузка своего аватара: оригинал + кропнутый квадрат + параметры кропа.
    // Multipart — request() не ставит Content-Type для FormData (boundary от браузера).
    uploadAvatar: (id: string, original: File, cropped: Blob, crop: { scale: number; offsetX: number; offsetY: number }) => {
      const form = new FormData();
      form.append('original', original, original.name || 'original');
      form.append('cropped', cropped, 'avatar.jpg');
      form.append('crop', JSON.stringify(crop));
      return request<Persona>(`/personas/${encodeURIComponent(id)}/avatar/upload`, {
        method: 'POST', body: form, timeoutMs: 60_000,
      });
    },
    // Перекроп сохранённого оригинала (без повторной загрузки файла)
    recropAvatar: (id: string, cropped: Blob, crop: { scale: number; offsetX: number; offsetY: number }) => {
      const form = new FormData();
      form.append('cropped', cropped, 'avatar.jpg');
      form.append('crop', JSON.stringify(crop));
      return request<Persona>(`/personas/${encodeURIComponent(id)}/avatar/recrop`, {
        method: 'POST', body: form, timeoutMs: 60_000,
      });
    },
    // URL оригинала загруженного аватара (для перекропа) — токен через ?access_token=
    avatarOriginalUrl: (persona: Persona): string | null => {
      if (!persona.avatar?.originalFile) return null;
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', persona.avatar.originalFile);
      return `/api/personas/${encodeURIComponent(persona.id)}/avatar/original?${params}`;
    },
    // URL картинки-кандидата (галерея генерации) для <img>: токен через ?access_token=
    avatarCandidateUrl: (id: string, file: string): string => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      return `/api/personas/${encodeURIComponent(id)}/avatar/candidate/${encodeURIComponent(file)}?${params}`;
    },
    // Быстрое создание персоны по свободному промпту: LLM заполняет роль/имя/описание/
    // характер/приветствие/цвет, фото-аватар генерируется автоматически.
    // Запрос долгий (LLM ~10-40с + fal ~10-40с, до ~90с) — таймаут расширен. 502 — можно повторить.
    quickCreate: (body: { prompt: string; scope?: PersonaScope; projectId?: string }) =>
      request<Persona>('/personas/ai/quick-create', {
        method: 'POST',
        body: JSON.stringify(body),
        timeoutMs: 150_000,
      }),
    // AI-формирование команды: промпт + проект → LLM предлагает состав (черновики)
    aiTeam: (projectId: string, prompt: string) =>
      request<{ members: TeamMemberDraft[] }>('/personas/ai/team', {
        method: 'POST',
        body: JSON.stringify({ projectId, prompt }),
        timeoutMs: 150_000,
      }),
    // AI-редактирование характера: без current — генерация с нуля по имени/роли/описанию;
    // с current (legacy-текст или сериализованный контракт, + опц. instruction) — улучшение.
    // Возвращает структурированный контракт (P1). Может занять до ~30с; 502 при ошибке.
    aiCharacter: (body: { name?: string; role?: string; description?: string; current?: string; instruction?: string }) =>
      request<{ contract: PersonaContract }>('/personas/ai/character', {
        method: 'POST',
        body: JSON.stringify(body),
      }),

    // === Привязки «Знания и правила» (фича persona-bindings) ===
    // Мгновенное сохранение: каждая мутация — отдельный запрос, без общей формы.
    bindings: (id: string) =>
      request<PersonaBinding[]>(`/personas/${encodeURIComponent(id)}/bindings`),
    addBinding: (id: string, dto: PersonaBindingDto) =>
      request<PersonaBinding>(`/personas/${encodeURIComponent(id)}/bindings`, {
        method: 'POST', body: JSON.stringify(dto),
      }),
    updateBinding: (id: string, bindingId: string, dto: PersonaBindingDto) =>
      request<PersonaBinding>(`/personas/${encodeURIComponent(id)}/bindings/${encodeURIComponent(bindingId)}`, {
        method: 'PUT', body: JSON.stringify(dto),
      }),
    removeBinding: (id: string, bindingId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/bindings/${encodeURIComponent(bindingId)}`, {
        method: 'DELETE',
      }),
    // Каталог целей для пикера: type = project | knowledge | notes | tool | skill;
    // для notes с source= — папки внутри источника
    bindingTargets: (type: string, source?: string) => {
      const qs = new URLSearchParams({ type });
      if (source) qs.set('source', source);
      return request<BindingTarget[]>(`/personas/binding-targets?${qs}`);
    },
    // AI-формулировка условия «когда пользоваться» по содержимому источника (LLM, до ~60с)
    aiBindingCondition: (body: { type: PersonaBindingType; target: string; path?: string | null }) =>
      request<{ condition: string }>('/personas/bindings/ai-condition', {
        method: 'POST', body: JSON.stringify(body), timeoutMs: 90_000,
      }),
    // AI-подбор привязок под роль персоны: кандидаты, ничего не сохраняется
    suggestBindings: (id: string) =>
      request<{ candidates: PersonaBinding[] }>(`/personas/${encodeURIComponent(id)}/bindings/suggest`, {
        method: 'POST', timeoutMs: 150_000,
      }),

    // === Проактивность/автоматизации (правила «событие → действие») ===
    automation: (id: string) =>
      request<PersonaAutomationRule[]>(`/personas/${encodeURIComponent(id)}/automation`),
    addAutomation: (id: string, dto: AutomationRuleDto) =>
      request<PersonaAutomationRule>(`/personas/${encodeURIComponent(id)}/automation`, {
        method: 'POST', body: JSON.stringify(dto),
      }),
    updateAutomation: (id: string, ruleId: string, dto: AutomationRuleDto) =>
      request<PersonaAutomationRule>(
        `/personas/${encodeURIComponent(id)}/automation/${encodeURIComponent(ruleId)}`,
        { method: 'PUT', body: JSON.stringify(dto) },
      ),
    removeAutomation: (id: string, ruleId: string) =>
      request<void>(
        `/personas/${encodeURIComponent(id)}/automation/${encodeURIComponent(ruleId)}`,
        { method: 'DELETE' },
      ),
    // Ручной прогон: синтетическое событие, байпас троттлинга (UX «Проверить»)
    testAutomation: (id: string, ruleId: string) =>
      request<void>(
        `/personas/${encodeURIComponent(id)}/automation/${encodeURIComponent(ruleId)}/test`,
        { method: 'POST' },
      ),
    // AI-подбор правил автоматизации под роль персоны: кандидаты, ничего не сохраняется
    suggestAutomation: (id: string) =>
      request<{ candidates: PersonaAutomationRule[] }>(`/personas/${encodeURIComponent(id)}/automation/suggest`, {
        method: 'POST', timeoutMs: 150_000,
      }),
  },

  // Утренний бриф (флаг daily-briefing): собрать план дня в дневник
  briefing: {
    today: (date?: string) =>
      request<NoteDetail>('/briefing/today', { method: 'POST', body: JSON.stringify({ date: date ?? null }) }),
  },

  // Единый поиск (флаг unified-search): заметки + задачи в одной выдаче
  search: (q: string, topK = 8) =>
    request<SearchHit[]>(`/search?q=${encodeURIComponent(q)}&topK=${topK}`),

  sessions: {
    list: (projectId: string) => request<Session[]>(`/projects/${projectId}/sessions`),
    create: (projectId: string, mode = 'acceptEdits', resumeSessionId?: string, name?: string, model?: string, agentName?: string, effort?: string) =>
      request<Session>(`/projects/${projectId}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, agentName, effort }),
      }),
    update: (projectId: string, sessionId: string, data: { name?: string | null; model?: string | null; effort?: string | null; expiresAfterMinutes?: number | null }) =>
      request<Session>(`/projects/${projectId}/sessions/${sessionId}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, sessionId: string) =>
      request<void>(`/projects/${projectId}/sessions/${sessionId}`, { method: 'DELETE' }),
    getHistory: (projectId: string, sessionId: string) =>
      request<unknown[]>(`/projects/${projectId}/sessions/${sessionId}/history`),
    // «Итог сессии»: конспект сессии заметкой (флаг notes-session-summary).
    // Маршрут по id сессии — работает и для проектных сессий, и для чатов
    summary: (sessionId: string) =>
      request<NoteDetail>(`/sessions/${sessionId}/summary`, { method: 'POST' }),
    // «Задачи из чата» (флаг chat-extract-tasks): извлечь кандидатов (не создаёт)
    extractTasks: (sessionId: string) =>
      request<ExtractTasksResponse>(`/sessions/${sessionId}/extract-tasks`, { method: 'POST' }),
  },

  // Чаты вне проекта (project-less)
  chats: {
    list: () => request<Session[]>('/chats'),
    get: (id: string) => request<Session>(`/chats/${id}`),
    create: (mode = 'auto', resumeSessionId?: string, name?: string, model?: string, effort?: string) =>
      request<Session>('/chats', {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, effort }),
      }),
    update: (id: string, data: { name?: string | null; model?: string | null; effort?: string | null; pinned?: boolean; expiresAfterMinutes?: number | null }) =>
      request<Session>(`/chats/${id}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    // Групповой чат персон (флаг persona-group-chats): 2-4 участника, первый — ведущая.
    // Зона — по ведущей: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    createGroup: (personaIds: string[], mode = 'auto', name?: string) =>
      request<Session>('/chats/group', {
        method: 'POST',
        body: JSON.stringify({ personaIds, mode, name }),
      }),
    // Обновить состав участников группового чата (спикер сохраняется, если остался)
    setParticipants: (id: string, personaIds: string[]) =>
      request<Session>(`/chats/${id}/participants`, {
        method: 'PUT',
        body: JSON.stringify({ personaIds }),
      }),
    // Цикл «до готово» (флаг work-loop): агент работает итерациями до отчёта о завершении.
    // Работает и для проектных сессий, и для чатов вне проекта
    setWorkLoop: (id: string, enabled: boolean) =>
      request<Session>(`/chats/${id}/loop`, {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
    delete: (id: string) => request<void>(`/chats/${id}`, { method: 'DELETE' }),
    getHistory: (id: string) => request<unknown[]>(`/chats/${id}/history`),
    uploadFile: async (id: string, file: File): Promise<{ path: string }> => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const form = new FormData();
      form.append('file', file);
      const res = await fetch(`/api/chats/${id}/files/upload`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form });
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
      return res.json();
    },
  },

  // Продуктовая история (AI-сводка по всем проектам): дни, сводка дня, счетчик новых
  history: {
    days: (sinceDays = 0) =>
      request<DaySummaryStub[]>(`/history/days${sinceDays > 0 ? `?sinceDays=${sinceDays}` : ''}`),
    day: (date: string) =>
      request<ChangelogDay>(`/history/day/${date}`),
    newCount: (sinceIso: string) =>
      request<{ count: number }>(`/history/new-count?since=${encodeURIComponent(sinceIso)}`),
    status: () => request<ChangelogStatus>('/history/status'),
    // Сбросить кеш одного дня (перегенерация) / всей истории (очистка)
    invalidateDay: (date: string) =>
      request<void>(`/history/day/${date}`, { method: 'DELETE' }),
    clear: () =>
      request<void>('/history', { method: 'DELETE' }),
  },

  files: {
    list: (projectId: string, path = '') =>
      request<FileEntry[]>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`),
    tree: (projectId: string, path = '') =>
      request<FileEntry[]>(`/projects/${projectId}/files/tree?path=${encodeURIComponent(path)}`),
    search: (projectId: string, q: string) =>
      request<FileEntry[]>(`/projects/${projectId}/files/search?q=${encodeURIComponent(q)}`),
    getContent: (projectId: string, path: string) =>
      request<{ content: string | null; isBinary: boolean; isImage: boolean; isDocument?: boolean; docKind?: 'pdf' | 'docx' | 'xlsx'; mimeType?: string; base64?: string; fileSize?: number }>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`),
    saveContent: (projectId: string, path: string, content: string) =>
      request<void>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`, {
        method: 'PUT',
        body: JSON.stringify({ content }),
      }),
    getDiff: (projectId: string, path: string) =>
      request<{ diff: string | null }>(`/projects/${projectId}/files/diff?path=${encodeURIComponent(path)}`),
    revert: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files/revert`, { method: 'POST', body: JSON.stringify({ path }) }),
    createFile: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files/create`, { method: 'POST', body: JSON.stringify({ path }) }),
    mkdir: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files/mkdir`, { method: 'POST', body: JSON.stringify({ path }) }),
    rename: (projectId: string, oldPath: string, newPath: string) =>
      request<void>(`/projects/${projectId}/files/rename`, {
        method: 'POST',
        body: JSON.stringify({ oldPath, newPath }),
      }),
    delete: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`, { method: 'DELETE' }),
    saveFromUrl: (projectId: string, url: string, path: string) =>
      request<{ path: string }>(`/projects/${projectId}/files/save-from-url`, {
        method: 'POST',
        body: JSON.stringify({ url, path }),
      }),
    officeDiscard: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files/office-discard?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    getOfficeVersion: (projectId: string, path: string) =>
      request<{ ms: number }>(`/projects/${projectId}/files/office-version?path=${encodeURIComponent(path)}`),
    officeForceSave: (projectId: string, path: string) =>
      request<{ ok: boolean; reason?: string }>(`/projects/${projectId}/files/office-force-save?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    upload: async (projectId: string, file: File, targetPath = ''): Promise<void> => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const form = new FormData();
      form.append('file', file);
      const res = await fetch(
        `/api/projects/${projectId}/files/upload?path=${encodeURIComponent(targetPath)}`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form },
      );
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
    },
  },

  knowledge: {
    getStatus: (projectId: string) =>
      request<{ datasetId: string | null; documents: DifyDocument[]; total: number }>(`/projects/${projectId}/knowledge`),
    indexFile: (projectId: string, relativePath: string) =>
      request<{ datasetId: string; document: DifyDocument }>(
        `/projects/${projectId}/knowledge/index`,
        { method: 'POST', body: JSON.stringify({ relativePath }) }
      ),
    indexFolder: (projectId: string, relativePath: string) =>
      request<{ indexed: number; skipped: number; documents: DifyDocument[] }>(
        `/projects/${projectId}/knowledge/index-folder`,
        { method: 'POST', body: JSON.stringify({ relativePath }) }
      ),
    deleteDocument: (projectId: string, documentId: string) =>
      request<void>(`/projects/${projectId}/knowledge/documents/${documentId}`, { method: 'DELETE' }),
    deleteDataset: (projectId: string) =>
      request<void>(`/projects/${projectId}/knowledge`, { method: 'DELETE' }),
    setDocumentTags: (projectId: string, documentName: string, documentId: string, tags: string[]) =>
      request<void>(`/projects/${projectId}/knowledge/tags`, {
        method: 'PUT',
        body: JSON.stringify({ documentName, documentId, tags }),
      }),
  },

  // Раздел «Знания»: менеджер баз знаний Dify (личные + публичные), не путать с
  // проектным knowledge выше. Dify — источник истины; configured=false — не настроен.
  knowledgeBases: {
    list: () => request<KnowledgeListResponse>('/knowledge'),
    get: (id: string) => request<KnowledgeBaseDetail>(`/knowledge/${encodeURIComponent(id)}`),
    create: (dto: CreateKnowledgeBaseDto) =>
      request<{ id: string; title: string; visibility: string }>('/knowledge', {
        method: 'POST', body: JSON.stringify(dto),
      }),
    remove: (id: string) => request<void>(`/knowledge/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Добавить документ текстом
    addDocumentText: (id: string, name: string, text: string) =>
      request<{ id: string; name: string; indexingStatus: string }>(
        `/knowledge/${encodeURIComponent(id)}/documents`,
        { method: 'POST', body: JSON.stringify({ name, text }) },
      ),
    // Загрузить документ файлом (multipart — request() не ставит Content-Type для FormData)
    addDocumentFile: async (id: string, file: File, name?: string): Promise<{ id: string; name: string; indexingStatus: string }> => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const form = new FormData();
      form.append('file', file);
      if (name) form.append('name', name);
      const res = await fetch(`/api/knowledge/${encodeURIComponent(id)}/documents/file`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form });
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
      return res.json();
    },
    removeDocument: (id: string, docId: string) =>
      request<void>(`/knowledge/${encodeURIComponent(id)}/documents/${encodeURIComponent(docId)}`, { method: 'DELETE' }),
    // Содержимое документа — сегменты (чанки) по порядку
    getDocument: (id: string, docId: string) =>
      request<KnowledgeDocumentContent>(`/knowledge/${encodeURIComponent(id)}/documents/${encodeURIComponent(docId)}`),
    // method: semantic (по смыслу) | fulltext (точный полнотекстовый)
    search: (id: string, q: string, method: 'semantic' | 'fulltext', topK = 8) =>
      request<{ items: KnowledgeSearchHit[] }>(
        `/knowledge/${encodeURIComponent(id)}/search?q=${encodeURIComponent(q)}&topK=${topK}&method=${method}`),
  },

  skills: {
    list: (projectId: string) => request<SkillsData>(`/projects/${projectId}/skills`),
    // Глобальные скиллы без привязки к проекту (для чатов вне проекта)
    listGlobal: () => request<SkillInfo[]>('/skills'),
    getSkill: (skillName: string) => request<{ content: string }>(`/skills/${skillName}`),
    saveSkill: (skillName: string, content: string) =>
      request<void>(`/skills/${skillName}`, { method: 'PUT', body: JSON.stringify({ content }) }),
    createSkill: (name: string, content: string) =>
      request<{ name: string }>('/skills', { method: 'POST', body: JSON.stringify({ name, content }) }),
    getAgent: (projectId: string, agentName: string) =>
      request<{ content: string }>(`/projects/${projectId}/agents/${agentName}`),
    saveAgent: (projectId: string, agentName: string, content: string) =>
      request<void>(`/projects/${projectId}/agents/${agentName}`, { method: 'PUT', body: JSON.stringify({ content }) }),
    createAgent: (projectId: string, name: string, content: string) =>
      request<{ name: string }>(`/projects/${projectId}/agents`, { method: 'POST', body: JSON.stringify({ name, content }) }),

    // --- Реестр skills.sh (обёртка npx skills) ---
    // Поиск навыков по реестру; owner — опциональное сужение по GitHub-владельцу.
    // Русский запрос переводится на английский (реестр англоязычный) — translatedQuery
    // показывает, что реально искали (null, если перевод не понадобился).
    // Все операции с реестром долгие (перевод LLM, клонирование репозиториев, подбор) —
    // задаём щедрый timeoutMs, иначе дефолтные 30с обрывают запрос и офлайн-слой
    // ложно решает, что мы офлайн («Действие недоступно офлайн»).
    find: (q: string, owner?: string) =>
      request<{ query: string; translatedQuery: string | null; results: RegistrySkill[] }>(
        `/skills/find?q=${encodeURIComponent(q)}${owner ? `&owner=${encodeURIComponent(owner)}` : ''}`,
        { timeoutMs: 90_000 }),
    // Установка навыка: scope 'project' требует projectId, 'global' — нет
    install: (source: string, skill: string, scope: 'project' | 'global', projectId?: string) =>
      request<{ installed: string; scope: string }>('/skills/install', {
        method: 'POST', body: JSON.stringify({ source, skill, scope, projectId }), timeoutMs: 180_000,
      }),
    uninstall: (skill: string, scope: 'project' | 'global', projectId?: string) =>
      request<void>(`/skills/installed?skill=${encodeURIComponent(skill)}&scope=${scope}${projectId ? `&projectId=${projectId}` : ''}`,
        { method: 'DELETE', timeoutMs: 90_000 }),
    // LLM-подбор: ровно один из personaId / projectId / query
    suggest: (ctx: { personaId?: string; projectId?: string; query?: string }) =>
      request<{ candidates: SkillSuggestion[] }>('/skills/suggest',
        { method: 'POST', body: JSON.stringify(ctx), timeoutMs: 200_000 }),
    // Установить навык персоне: глобальная установка + привязка (Skill)
    installForPersona: (personaId: string, source: string, skill: string) =>
      request<{ installed: string; bound: boolean; warning?: string }>(`/personas/${personaId}/skills`, {
        method: 'POST', body: JSON.stringify({ source, skill }), timeoutMs: 180_000,
      }),
  },

  workflow: {
    getAgents: (transcriptDir: string) =>
      request<{ agents: WorkflowAgentInfo[] }>(
        `/workflow-agents?transcriptDir=${encodeURIComponent(transcriptDir)}`
      ),
    // Полный поток одного агента (текст/thinking/инструменты) — лениво при раскрытии карточки
    getTimeline: (transcriptDir: string, agentId: string) =>
      request<{ blocks: WorkflowAgentBlock[] }>(
        `/workflow-agents/timeline?transcriptDir=${encodeURIComponent(transcriptDir)}&agentId=${encodeURIComponent(agentId)}`
      ),
  },

  sync: {
    list: (projectId: string) => request<SyncMark[]>(`/projects/${projectId}/sync`),
    add: (projectId: string, path: string, isDirectory: boolean) =>
      request<void>(`/projects/${projectId}/sync`, {
        method: 'POST',
        body: JSON.stringify({ path, isDirectory }),
      }),
    remove: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/sync?path=${encodeURIComponent(path)}`, { method: 'DELETE' }),
  },
};
