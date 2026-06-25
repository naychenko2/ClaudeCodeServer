import type { Project, Session, FileEntry, SyncMark, WorkflowAgentInfo, AppSettings } from '../types';
import { request } from './offline';

export type { WorkflowAgentInfo };

// Projects
export const api = {
  auth: {
    login: (username: string, password: string) =>
      request<{ token: string; expiresAt: string; username: string }>('/auth/login', {
        method: 'POST',
        body: JSON.stringify({ username, password }),
      }),
    me: () =>
      request<{ userId: string; username: string; role: string }>('/auth/me'),
  },

  settings: {
    get: () => request<AppSettings>('/settings'),
    save: (s: AppSettings) => request<AppSettings>('/settings', { method: 'PUT', body: JSON.stringify(s) }),
  },

  projects: {
    list: () => request<Project[]>('/projects'),
    create: (name: string, rootPath: string | null, createDirectory = false) =>
      request<Project>('/projects', { method: 'POST', body: JSON.stringify({ name, rootPath, createDirectory }) }),
    update: (id: string, data: { name?: string; rootPath?: string }) =>
      request<Project>(`/projects/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: string) => request<void>(`/projects/${id}`, { method: 'DELETE' }),
  },

  sessions: {
    list: (projectId: string) => request<Session[]>(`/projects/${projectId}/sessions`),
    create: (projectId: string, mode = 'auto', resumeSessionId?: string, name?: string, model?: string) =>
      request<Session>(`/projects/${projectId}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model }),
      }),
    update: (projectId: string, sessionId: string, data: { name?: string | null; model?: string | null }) =>
      request<Session>(`/projects/${projectId}/sessions/${sessionId}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, sessionId: string) =>
      request<void>(`/projects/${projectId}/sessions/${sessionId}`, { method: 'DELETE' }),
    getHistory: (projectId: string, sessionId: string) =>
      request<unknown[]>(`/projects/${projectId}/sessions/${sessionId}/history`),
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
