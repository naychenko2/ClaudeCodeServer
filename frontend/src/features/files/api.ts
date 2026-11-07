// «Файлы проекта» и «Синхронизация» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// тонкая обёртка над живым REST; типы FileEntry/SyncMark — types/index.ts.
//
// Маршрут «сервер или агент» решает lib/deviceAgent — та же его копия, что у lib/api.ts:
// память о проекте одна на приложение.
import { readStoredToken, request } from '../../lib/offline';
import { assertServerRoute, projectRequest, projectRouteOf } from '../../lib/deviceAgent';
import type { ChangedBySession, FileEntry, SyncMark } from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом.
export const filesApi = {
  files: {
    // URL файла проекта для браузерного <img>/<video>: токен через ?access_token=,
    // потому что тег не шлёт заголовки. Нужен картинкам в markdown — README ссылается
    // на них относительным путём, а base64 из files/content для <img src> не подходит
    // sessionId (F1a, «Резолвинг ссылок по worktree»): забрать файл из дерева чата,
    // а не из анкера проекта; параметр необязательный — старые вызовы не меняются.
    fileUrl: (projectId: string, path: string, sessionId?: string): string => {
      // Локальный проект: поток отдаёт агент по узкому билету на путь, а его выдача
      // асинхронна — синхронного URL нет (точка — agentStreamUrl в deviceAgent.ts).
      // Основной билет проекта в URL не кладём никогда
      const route = projectRouteOf(projectId);
      if (route === 'agent') return '';
      const token = readStoredToken();
      const params = new URLSearchParams({ path });
      if (sessionId) params.set('sessionId', sessionId);
      if (token) params.set('access_token', token);
      // С другого устройства файл отдаёт ретранслятор — целиком, без Range, под тем же JWT
      const base = route === 'relay' ? 'relay/files/stream' : 'files/stream';
      return `/api/projects/${encodeURIComponent(projectId)}/${base}?${params}`;
    },
    list: (projectId: string, path = '') =>
      projectRequest<FileEntry[]>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`),
    tree: (projectId: string, path = '', showHidden?: boolean) =>
      projectRequest<FileEntry[]>(`/projects/${projectId}/files/tree?path=${encodeURIComponent(path)}${showHidden ? '&showHidden=true' : ''}`),
    search: (projectId: string, q: string) =>
      projectRequest<FileEntry[]>(`/projects/${projectId}/files/search?q=${encodeURIComponent(q)}`),
    // Где путь реально лежит в деревьях проекта (анкер + worktree). paths ≤ 50,
    // иначе сервер 400; в ответе только найденные пути. Формат — DTO endpoints
    // files/locate (не TreeHit: TreeHit — внутренний тип локатора, без path и
    // alternatives). alternatives — число других совпадений (совпадений − 1).
    locate: (projectId: string, paths: string[], sessionId?: string) =>
      projectRequest<{ path: string; branch?: string; isAnchor: boolean; alternatives?: number }[]>(
        `/projects/${projectId}/files/locate`,
        { method: 'POST', body: JSON.stringify({ paths, ...(sessionId ? { sessionId } : {}) }) },
      ),
    // featureId (W4-5B2): читать из проверенного дерева эпика; без него — корень проекта.
    // sessionId (F1a): резолвить путь в дереве чата; ответ несёт branch (в каком дереве
    // нашли) и isAnchor (true — файл найден в заявленном дереве-анкере, false — в другом).
    getContent: (projectId: string, path: string, featureId?: string, opts?: { sessionId?: string }) =>
      projectRequest<{ content: string | null; isBinary: boolean; isImage: boolean; isDocument?: boolean; docKind?: string; mimeType?: string; base64?: string; fileSize?: number; branch?: string | null; isAnchor?: boolean }>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}${featureId ? `&featureId=${encodeURIComponent(featureId)}` : ''}${opts?.sessionId ? `&sessionId=${encodeURIComponent(opts.sessionId)}` : ''}`),
    saveContent: (projectId: string, path: string, content: string) =>
      projectRequest<void>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`, {
        method: 'PUT',
        body: JSON.stringify({ content }),
      }),
    // Документы (pdf/docx/xlsx/pptx): конвертация в MD + ИИ-помощь (локальная модель / claude)
    documentConvert: (projectId: string, path: string) =>
      projectRequest<{ markdown: string }>(`/projects/${projectId}/files/document/convert?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentSummary: (projectId: string, path: string) =>
      projectRequest<{ summary: string }>(`/projects/${projectId}/files/document/summary?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentExtract: (projectId: string, path: string) =>
      projectRequest<{ decisions: string[]; dates: string[]; people: string[]; actionItems: string[] }>(`/projects/${projectId}/files/document/extract?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentTags: (projectId: string, path: string) =>
      projectRequest<{ tags: string[] }>(`/projects/${projectId}/files/document/tags?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    // Трансформировать любой файл в Markdown и сохранить (рядом или в targetDir).
    // enhance — восстановить разметку локальной моделью (заголовки/списки, для pdf).
    toMarkdown: (projectId: string, path: string, targetDir?: string | null, enhance = false) =>
      projectRequest<{ savedPath: string; markdown: string }>(`/projects/${projectId}/files/document/to-markdown`, {
        method: 'POST', body: JSON.stringify({ path, targetDir: targetDir ?? null, enhance }),
      }),
    getDiff: (projectId: string, path: string) =>
      projectRequest<{ diff: string | null }>(`/projects/${projectId}/files/diff?path=${encodeURIComponent(path)}`),
    // Панель «Изменения»: для присланных путей — какие ЕЩЁ чаты проекта их меняли.
    // Ключи ответа — ровно присланные строки path (см. FilesController.ChangedBy)
    changedBy: (projectId: string, paths: string[]) =>
      projectRequest<{ files: Record<string, ChangedBySession[]> }>(`/projects/${projectId}/files/changed-by`, {
        method: 'POST',
        body: JSON.stringify({ paths }),
      }),
    revert: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files/revert`, { method: 'POST', body: JSON.stringify({ path }) }),
    createFile: (projectId: string, path: string, content?: string) =>
      projectRequest<void>(`/projects/${projectId}/files/create`, { method: 'POST', body: JSON.stringify({ path, content }) }),
    mkdir: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files/mkdir`, { method: 'POST', body: JSON.stringify({ path }) }),
    rename: (projectId: string, oldPath: string, newPath: string) =>
      projectRequest<void>(`/projects/${projectId}/files/rename`, {
        method: 'POST',
        body: JSON.stringify({ oldPath, newPath }),
      }),
    delete: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`, { method: 'DELETE' }),
    saveFromUrl: (projectId: string, url: string, path: string) =>
      projectRequest<{ path: string }>(`/projects/${projectId}/files/save-from-url`, {
        method: 'POST',
        body: JSON.stringify({ url, path }),
      }),
    officeDiscard: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files/office-discard?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    getOfficeVersion: (projectId: string, path: string) =>
      projectRequest<{ ms: number }>(`/projects/${projectId}/files/office-version?path=${encodeURIComponent(path)}`),
    officeForceSave: (projectId: string, path: string) =>
      projectRequest<{ ok: boolean; reason?: string }>(`/projects/${projectId}/files/office-force-save?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    upload: async (projectId: string, file: File, targetPath = ''): Promise<void> => {
      assertServerRoute(projectId, 'POST', 'files/upload');
      const token = readStoredToken();
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
