import type { Project, ProjectGroup, Session, FileEntry, SyncMark, WorkflowAgentInfo, AppSettings, UserProfile, SkillsData, SkillInfo, PermissionRule, UsageResponse, FalAccountResponse, FeatureFlagDefinition, SystemPromptPart, Task, CreateTaskDto, UpdateTaskDto, ChangelogDay, DaySummaryStub, ChangelogStatus, NoteSummary, NoteDetail, NoteBacklink, NoteGraph, NoteSource, NoteTemplate, NoteSemanticHit, CreateNoteDto, UpdateNoteDto, NoteTask } from '../types';
import { request } from './offline';

export type { WorkflowAgentInfo };

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
    create: (name: string, rootPath: string | null, createDirectory = false, groupId?: string | null) =>
      request<Project>('/projects', { method: 'POST', body: JSON.stringify({ name, rootPath, createDirectory, groupId }) }),
    update: (id: string, data: { name?: string; rootPath?: string; systemPrompt?: string; showHiddenFiles?: boolean; permissionRules?: PermissionRule[]; groupId?: string | null }) =>
      request<Project>(`/projects/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: string) => request<void>(`/projects/${id}`, { method: 'DELETE' }),
    getBuiltinPrompt: () => request<{ content: string }>('/projects/builtin-prompt'),
    getEffectivePrompt: (id: string) => request<{ parts: SystemPromptPart[] }>(`/projects/${id}/effective-prompt`),
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
  },

  // Утренний бриф (флаг daily-briefing): собрать план дня в дневник
  briefing: {
    today: (date?: string) =>
      request<NoteDetail>('/briefing/today', { method: 'POST', body: JSON.stringify({ date: date ?? null }) }),
  },

  sessions: {
    list: (projectId: string) => request<Session[]>(`/projects/${projectId}/sessions`),
    create: (projectId: string, mode = 'acceptEdits', resumeSessionId?: string, name?: string, model?: string, agentName?: string, effort?: string) =>
      request<Session>(`/projects/${projectId}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, agentName, effort }),
      }),
    update: (projectId: string, sessionId: string, data: { name?: string | null; model?: string | null; effort?: string | null }) =>
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
  },

  // Чаты вне проекта (project-less)
  chats: {
    list: () => request<Session[]>('/chats'),
    create: (mode = 'auto', resumeSessionId?: string, name?: string, model?: string, effort?: string) =>
      request<Session>('/chats', {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, effort }),
      }),
    update: (id: string, data: { name?: string | null; model?: string | null; effort?: string | null; pinned?: boolean }) =>
      request<Session>(`/chats/${id}`, {
        method: 'PUT',
        body: JSON.stringify(data),
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
  },

  workflow: {
    getAgents: (transcriptDir: string) =>
      request<{ agents: WorkflowAgentInfo[] }>(
        `/workflow-agents?transcriptDir=${encodeURIComponent(transcriptDir)}`
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
