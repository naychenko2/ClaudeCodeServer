// Нити картинок основного чата (ADR-019 §1): REST …/sessions/{sessionId}/threads и
// событие image_thread_changed. Каждая мутация несёт revision и отвечает полным
// состоянием; старая ревизия — 409 с актуальным состоянием в теле.

import { onMessage, request } from 'aihome_shell/kit';

export interface ImageThreadStack {
  stackId: string;
  // id шагов ImageEditStep по порядку
  steps: string[];
  // Шаг, от которого стопка продолжилась после отката
  forkedFromStepId: string | null;
  // «Старая стопка»: заморожена откатом и новой правкой
  old: boolean;
}

export interface ImageThreadSettings {
  provider: string | null;
  model: string | null;
  count: number;
  matchSourceSize: boolean;
}

export interface ImageThread {
  id: string;
  // Путь от корня проекта; null — черновик «Новая картинка»
  file: string | null;
  lineage: string[];
  // Папка сохранения черновика ("" — корень), после сохранения — null
  draftFolder: string | null;
  stacks: ImageThreadStack[];
  currentStackId: string | null;
  // Текущий шаг; null — исходник (или пустой черновик)
  currentStepId: string | null;
  settings: ImageThreadSettings | null;
  // Задача, чьи варианты ждут выбора; живёт до take или dismiss
  pendingJobId: string | null;
  createdAt: string;
}

export interface ImageThreadsState {
  focus: string | null;
  revision: number;
  threads: ImageThread[];
}

export const EMPTY_THREADS: ImageThreadsState = { focus: null, revision: 0, threads: [] };

// Событие SignalR владельцу на каждую запись нити
export interface ImageThreadChangedEvent {
  type: 'image_thread_changed';
  sessionId: string;
  projectId: string;
  revision: number;
  state: ImageThreadsState;
}

// Ровно одно: вариант задачи этой нити или шаг правки без ИИ (stepId из /transform)
export type ImageThreadTake = { jobId: string; variant: number } | { stepId: string };

const base = (projectId: string, sessionId: string) =>
  `/projects/${encodeURIComponent(projectId)}/image-editor/sessions/${encodeURIComponent(sessionId)}/threads`;
const one = (projectId: string, sessionId: string, threadId: string) =>
  `${base(projectId, sessionId)}/${encodeURIComponent(threadId)}`;

const post = <T>(url: string, body: unknown, method = 'POST') =>
  request<T>(url, { method, body: JSON.stringify(body) });

export const threadsApi = {
  get: (projectId: string, sessionId: string) =>
    request<ImageThreadsState>(base(projectId, sessionId), { live: true }),
  // file — взять файл проекта в работу; draftFolder — черновик «Новая картинка»
  create: (projectId: string, sessionId: string, body: { file?: string; draftFolder?: string; revision: number }) =>
    post<ImageThreadsState>(base(projectId, sessionId), body),
  remove: (projectId: string, sessionId: string, threadId: string, revision: number) =>
    request<ImageThreadsState>(`${one(projectId, sessionId, threadId)}?revision=${revision}`, { method: 'DELETE' }),
  focus: (projectId: string, sessionId: string, threadId: string | null, revision: number) =>
    post<ImageThreadsState>(`${base(projectId, sessionId)}/focus`, { threadId, revision }, 'PUT'),
  take: (projectId: string, sessionId: string, threadId: string, what: ImageThreadTake, revision: number) =>
    post<ImageThreadsState>(`${one(projectId, sessionId, threadId)}/take`, { ...what, revision }),
  dismiss: (projectId: string, sessionId: string, threadId: string, jobId: string, revision: number) =>
    post<ImageThreadsState>(`${one(projectId, sessionId, threadId)}/dismiss`, { jobId, revision }),
  // stepId = null — вернуться к исходнику
  rollback: (projectId: string, sessionId: string, threadId: string, stepId: string | null, revision: number) =>
    post<ImageThreadsState>(`${one(projectId, sessionId, threadId)}/rollback`, { stepId, revision }),
  settings: (projectId: string, sessionId: string, threadId: string, settings: ImageThreadSettings, revision: number) =>
    post<ImageThreadsState>(`${one(projectId, sessionId, threadId)}/settings`, { settings, revision }, 'PUT'),
  subscribe: (handler: (e: ImageThreadChangedEvent) => void) => onMessage(msg => {
    const m = msg as unknown as { type?: string };
    if (m.type === 'image_thread_changed') handler(m as unknown as ImageThreadChangedEvent);
  }),
};

// Актуальное состояние из тела 409 — перечитывать не нужно
export function conflictState(e: unknown): ImageThreadsState | null {
  const err = e as { status?: unknown; body?: { state?: unknown } } | null;
  if (err?.status !== 409) return null;
  const st = err.body?.state as ImageThreadsState | undefined;
  return st && Array.isArray(st.threads) ? st : null;
}
