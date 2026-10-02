// Нити картинок основного чата (ADR-019 §1): REST …/sessions/{sessionId}/threads (у личного
// чата — …/chats/{sessionId}/threads) и
// событие image_thread_changed. Каждая мутация несёт revision и отвечает полным
// состоянием; старая ревизия — 409 с актуальным состоянием в теле.

import { onMessage, request } from 'aihome_shell/kit';
import { threadsBase } from '../scope';

export interface ImageThreadStack {
  stackId: string;
  // id шагов ImageEditStep по порядку
  steps: string[];
  // Шаг, от которого стопка продолжилась после отката
  forkedFromStepId: string | null;
  // «Старая стопка»: заморожена откатом и новой правкой
  old: boolean;
}

// Версия картинки (изменение 27.09 к ADR-019): каждый вариант каждого запуска ИИ. Первая
// всегда исходник: id "origin", number 0, jobId null. baseVersionId/baseStepId — от чего
// правили; steps — шаги версии (у версии от ИИ первым идёт сам вариант, дальше правки без ИИ)
export interface ImageThreadVersion {
  id: string;
  number: number;
  jobId: string | null;
  variant: number | null;
  baseVersionId: string | null;
  baseStepId: string | null;
  steps: string[];
  currentStepId: string | null;
  createdAt: string;
}

export type ImageThreadLaunchStatus = 'running' | 'done' | 'failed' | 'cancelled' | 'interrupted';

// Запуск ИИ в нить: его якорь в ленте — запись image_launch_versions с тем же jobId
export interface ImageThreadLaunch {
  jobId: string;
  baseVersionId: string | null;
  baseStepId: string | null;
  at: string;
  status: ImageThreadLaunchStatus;
  initiator: 'human' | 'agent';
  prompt: string | null;
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
  // Задача, которую оборвал перезапуск сервера: вариантов не будет. Снимают следующий
  // запуск или «Не брать» (dismiss с этим jobId)
  interruptedJobId?: string | null;
  createdAt: string;
  // Версии и запуски (с 27.09); у ответа старого сервера их нет
  versions?: ImageThreadVersion[];
  currentVersionId?: string | null;
  launches?: ImageThreadLaunch[];
}

// Запись журнала нитей «с прошлого сообщения»: launched, taken, saved, interrupted
export interface ImageThreadEvent {
  at: string;
  kind: string;
  text: string;
  threadId?: string | null;
  jobId?: string | null;
}

export interface ImageThreadsState {
  focus: string | null;
  revision: number;
  threads: ImageThread[];
  events?: ImageThreadEvent[];
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

const base = threadsBase;
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
  // «Продолжить от версии»: версия становится текущей, нить — в работе. Ничего не удаляет
  current: (projectId: string, sessionId: string, threadId: string, versionId: string, revision: number) =>
    post<ImageThreadsState>(`${one(projectId, sessionId, threadId)}/current`, { versionId, revision }, 'PUT'),
  // Правка без ИИ (шаг из /transform) — шагом текущей версии, новой версии не создаёт
  addStep: (projectId: string, sessionId: string, threadId: string, stepId: string, revision: number) =>
    post<ImageThreadsState>(`${one(projectId, sessionId, threadId)}/steps`, { stepId, revision }),
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
