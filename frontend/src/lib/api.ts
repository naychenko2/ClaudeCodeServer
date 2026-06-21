import type { Project, Session, FileEntry } from '../types';

const BASE = '/api';

async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const res = await fetch(BASE + url, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(err.error ?? res.statusText);
  }
  if (res.status === 204) return undefined as T;
  return res.json();
}

// Projects
export const api = {
  auth: {
    ping: (serverUrl: string, apiKey: string) =>
      request<{ ok: boolean }>('/auth/ping', {
        method: 'POST',
        body: JSON.stringify({ serverUrl, apiKey }),
      }),
  },

  projects: {
    list: () => request<Project[]>('/projects'),
    create: (name: string, rootPath: string) =>
      request<Project>('/projects', { method: 'POST', body: JSON.stringify({ name, rootPath }) }),
    update: (id: string, data: { name?: string; rootPath?: string }) =>
      request<Project>(`/projects/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: string) => request<void>(`/projects/${id}`, { method: 'DELETE' }),
  },

  sessions: {
    list: (projectId: string) => request<Session[]>(`/projects/${projectId}/sessions`),
    create: (projectId: string, mode = 'auto', resumeSessionId?: string, name?: string) =>
      request<Session>(`/projects/${projectId}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name }),
      }),
    delete: (projectId: string, sessionId: string) =>
      request<void>(`/projects/${projectId}/sessions/${sessionId}`, { method: 'DELETE' }),
  },

  files: {
    list: (projectId: string, path = '') =>
      request<FileEntry[]>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`),
    search: (projectId: string, q: string) =>
      request<FileEntry[]>(`/projects/${projectId}/files/search?q=${encodeURIComponent(q)}`),
    getContent: (projectId: string, path: string) =>
      request<{ content: string | null; isBinary: boolean; isImage: boolean; mimeType?: string; base64?: string; fileSize?: number }>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`),
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
  },
};
