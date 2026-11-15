// «Файлы проекта» и «Синхронизация» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// тонкая обёртка над живым REST; типы FileEntry/SyncMark — types/index.ts.
//
// Маршрут «сервер или агент» решает lib/deviceAgent — та же его копия, что у lib/api.ts:
// память о проекте одна на приложение.
import { conflictBodyOf, readStoredToken, request } from '../../lib/offline';
import type { ConflictBody, EtagRef, FileConflictAuthor, FileConflictSource } from '../../lib/offline';
import { assertServerRoute, projectRequest, projectRouteOf } from '../../lib/deviceAgent';
import type { ChangedBySession, FileEntry, SyncMark } from '../../types';
import { isPersonalFilesId, personalFilesBase } from './personalFiles';

// База файлового API: у проекта — api/projects/{id}/files, у «Мои файлы» личного чата (псевдо-id) — личный маршрут чата.
// Остальные методы ниже зовутся только из контролов, которых у личного маршрута нет (маршрут 'personal')
const filesBase = (projectId: string): string =>
  isPersonalFilesId(projectId) ? personalFilesBase(projectId) : `/projects/${projectId}/files`;

// Конфликт сохранения файла (S2c-F2a, К8): 409 с телом {code:'file_changed', …} на
// PUT files/content с If-Match. Поля — тело 409 как есть: актуальное состояние файла на
// момент отказа. author и source бэк заполняет с S3-3/S2d-4, сейчас всегда null.
// Бросается ТОЛЬКО при ifMatch в saveContent: без If-Match сервер 409 по конфликту не отдаёт
// вовсе, поэтому существующие вызывающие (без ifMatch) этой ошибки не видят.
// Тип тела и интерфейсы author/source — в lib/offline: тот же контракт К8 служит
// конфликту заметки (NoteConflictError).
// Ответ files/content (доменный тип, FS-F1a): поля прочитанного файла плюс два поля
// ветки чтения из коммита ветки (FS-B2, когда рабочий каталог задачи снесли):
// fromCommit — true, когда содержимое прочитано git show по ветке задачи;
// reason — 404 с причиной доходит до вызывающего полем, а не исключением:
// 'branch_deleted' — файл снесли вместе с веткой, 'binary' — бинарь из коммита не читается.
// Прочие 404 (без reason) остаются исключением, как раньше.
export type FileContentResult = {
  content: string | null; isBinary: boolean; isImage: boolean;
  isDocument?: boolean; docKind?: string; mimeType?: string; base64?: string; fileSize?: number;
  branch?: string | null; isAnchor?: boolean;
  fromCommit?: boolean; reason?: 'binary' | 'branch_deleted';
  etag?: string;
};

export class FileConflictError extends Error {
  readonly status = 409;
  readonly code = 'file_changed';
  // Тег актуальной версии (без кавычек, как в теле 409) — по нему повторяют сохранение
  readonly sha256: string | null;
  // Актуальный текст чужой версии; null для не-текста (видео/аудио/документ/сверх потолка)
  readonly content: string | null;
  readonly author: FileConflictAuthor | null;
  readonly changedAt: string | null;
  readonly source: FileConflictSource | null;
  constructor(body: ConflictBody) {
    // Текст тот же, что заголовок диалога конфликта (SaveConflictDialog) — отдельной копии нет
    super('Файл изменили, пока вы правили');
    this.name = 'FileConflictError';
    this.sha256 = body.sha256 ?? null;
    this.content = body.content ?? null;
    this.author = body.author ?? null;
    this.changedAt = body.changedAt ?? null;
    this.source = body.source ?? null;
  }
}

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом.
export const filesApi = {
  files: {
    // URL файла проекта для браузерного <img>/<video>: токен через ?access_token=,
    // потому что тег не шлёт заголовки. Нужен картинкам в markdown — README ссылается
    // на них относительным путём, а base64 из files/content для <img src> не подходит
    // sessionId (F1a, «Резолвинг ссылок по worktree»): забрать файл из дерева чата,
    // а не из анкера проекта; параметр необязательный — старые вызовы не меняются.
    // taskId (FS-F1a, бэк FS-B1b): анкер — дерево задачи; два анкера сразу сервер
    // отвергает 400, поэтому при taskId sessionId не шлётся.
    // Скачивание личного файла (Content-Disposition: attachment)
    downloadUrl: (projectId: string, path: string): string => {
      const token = readStoredToken();
      const params = new URLSearchParams({ path });
      if (token) params.set('access_token', token);
      return `/api${filesBase(projectId)}/download?${params}`;
    },
    fileUrl: (projectId: string, path: string, sessionId?: string, taskId?: string): string => {
      if (isPersonalFilesId(projectId)) {
        const token = readStoredToken();
        const params = new URLSearchParams({ path });
        if (token) params.set('access_token', token);
        return `/api${filesBase(projectId)}/stream?${params}`;
      }
      // Локальный проект: поток отдаёт агент по узкому билету на путь, а его выдача
      // асинхронна — синхронного URL нет (точка — agentStreamUrl в deviceAgent.ts).
      // Основной билет проекта в URL не кладём никогда
      const route = projectRouteOf(projectId);
      if (route === 'agent') return '';
      const token = readStoredToken();
      const params = new URLSearchParams({ path });
      if (taskId) params.set('taskId', taskId);
      else if (sessionId) params.set('sessionId', sessionId);
      if (token) params.set('access_token', token);
      // С другого устройства файл отдаёт ретранслятор — целиком, без Range, под тем же JWT
      const base = route === 'relay' ? 'relay/files/stream' : 'files/stream';
      return `/api/projects/${encodeURIComponent(projectId)}/${base}?${params}`;
    },
    list: (projectId: string, path = '') =>
      projectRequest<FileEntry[]>(`${filesBase(projectId)}?path=${encodeURIComponent(path)}`),
    tree: (projectId: string, path = '', showHidden?: boolean) =>
      projectRequest<FileEntry[]>(`${filesBase(projectId)}/tree?path=${encodeURIComponent(path)}${showHidden ? '&showHidden=true' : ''}`),
    search: (projectId: string, q: string) =>
      projectRequest<FileEntry[]>(`${filesBase(projectId)}/search?q=${encodeURIComponent(q)}`),
    // Где путь реально лежит в деревьях проекта (анкер + worktree). paths ≤ 50,
    // иначе сервер 400; в ответе только найденные пути. Формат — DTO endpoints
    // files/locate (не TreeHit: TreeHit — внутренний тип локатора, без path и
    // alternatives). alternatives — число других совпадений (совпадений − 1).
    // taskId (FS-F1a, бэк FS-B1b): анкер — дерево задачи; при нём sessionId не шлётся
    // (два анкера сразу — 400).
    locate: (projectId: string, paths: string[], sessionId?: string, taskId?: string) =>
      projectRequest<{ path: string; branch?: string; isAnchor: boolean; alternatives?: number }[]>(
        `/projects/${projectId}/files/locate`,
        { method: 'POST', body: JSON.stringify({ paths, ...(taskId ? { taskId } : sessionId ? { sessionId } : {}) }) },
      ),
    // featureId (W4-5B2): читать из проверенного дерева эпика; без него — корень проекта.
    // sessionId (F1a): резолвить путь в дереве чата; ответ несёт branch (в каком дереве
    // нашли) и isAnchor (true — файл найден в заявленном дереве-анкере, false — в другом).
    // etag (S2c-F2a, К8): тег прочитанной версии для сохранения по If-Match. Сервер даёт его
    // только редактируемому тексту до 10 МиБ (FilesController.IsEtaggableText); видео/аудио/
    // документы и сверх потолка — без тега, как и их правка без If-Match. Агент и ретранслятор
    // ETag не отдают вовсе (LocalApi.MapFiles, ProjectRelayController) — там etag undefined.
    // Офлайн-доставка из кэша тега не имеет: сохранять офлайн нельзя в любом случае.
    // taskId (FS-F1a, бэк FS-B1a/FS-B2): анкер — дерево задачи; при нём sessionId не шлётся
    // (два анкера сразу — 400). Ответ может нести fromCommit (прочитано из коммита ветки);
    // 404 с reason (branch_deleted/binary) возвращается полем reason, а не исключением —
    // «удалён вместе с веткой» — состояние файла, его показывают, а не ловят.
    getContent: async (projectId: string, path: string, featureId?: string, opts?: { sessionId?: string; taskId?: string }): Promise<FileContentResult> => {
      const etagRef: EtagRef = {};
      const query = `path=${encodeURIComponent(path)}${featureId ? `&featureId=${encodeURIComponent(featureId)}` : ''}${opts?.taskId ? `&taskId=${encodeURIComponent(opts.taskId)}` : opts?.sessionId ? `&sessionId=${encodeURIComponent(opts.sessionId)}` : ''}`;
      try {
        const data = await projectRequest<FileContentResult>(`${filesBase(projectId)}/content?${query}`, { etagRef });
        return { ...data, etag: etagRef.etag };
      } catch (e) {
        // Форма ошибки — request() (offline.ts) и agentRequest() (deviceAgent.ts): status + body.
        const err = e as { status?: number; body?: { reason?: string } };
        const reason = err.body?.reason === 'branch_deleted' || err.body?.reason === 'binary' ? err.body.reason : null;
        if (err.status !== 404 || !reason) throw e;
        return { content: null, isBinary: false, isImage: false, reason, etag: undefined };
      }
    },
    // ifMatch (К8): тег версии, которую видел вызывающий, — etag из getContent или sha256 из
    // FileConflictError (повторная отправка по актуальному состоянию). С If-Match сервер либо
    // пишет (200), либо отказывает 409 file_changed, не трогая файл, — он становится
    // FileConflictError с полями тела. Без ifMatch — ровно как раньше: заголовка нет,
    // 409 по конфликту сервер не отдаёт, существующие вызывающие ничего не заметили.
    // Офлайн-сохранение — OfflineError, как любая прочая мутация: очереди файлов нет,
    // запись с If-Match офлайн не ставится в очередь молча (offline.ts: мутации офлайн запрещены).
    // etagOut: новый ETag ответа 200, если сервер его прислал (у файлов пока не присылает —
    // см. дефект; у заметок присылает). На агентской ветке If-Match до агента не доходит и
    // не проверяется: там конфликта нет по построению бэка.
    saveContent: async (projectId: string, path: string, content: string, ifMatch?: string, etagOut?: EtagRef): Promise<void> => {
      try {
        await projectRequest<void>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`, {
          method: 'PUT',
          body: JSON.stringify({ content }),
          ...(ifMatch ? { headers: { 'If-Match': ifMatch } } : {}),
          etagRef: etagOut,
        });
      } catch (e) {
        const b = conflictBodyOf(e, 'file_changed'); if (b) throw new FileConflictError(b); throw e;
      }
    },
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
      projectRequest<void>(`${filesBase(projectId)}/create`, { method: 'POST', body: JSON.stringify({ path, content }) }),
    mkdir: (projectId: string, path: string) =>
      projectRequest<void>(`${filesBase(projectId)}/mkdir`, { method: 'POST', body: JSON.stringify({ path }) }),
    rename: (projectId: string, oldPath: string, newPath: string) =>
      projectRequest<void>(`${filesBase(projectId)}/rename`, {
        method: 'POST',
        body: JSON.stringify({ oldPath, newPath }),
      }),
    delete: (projectId: string, path: string) =>
      projectRequest<void>(`${filesBase(projectId)}?path=${encodeURIComponent(path)}`, { method: 'DELETE' }),
    saveFromUrl: (projectId: string, url: string, path: string) =>
      projectRequest<{ path: string }>(`/projects/${projectId}/files/save-from-url`, {
        method: 'POST',
        body: JSON.stringify({ url, path }),
      }),
    // Сохранение в «Личное» по URL (Ф-8, требование Андрея 15.11): личный вариант saveFromUrl.
    // Маршрут «Личного» обслуживает только сервер (агент устройства о нём не знает), поэтому
    // request(), а не projectRequest(). Путь ограниченный медиальными папками images/|audio/|video/
    // (invalid_path для прочих), предел 200 МБ (413) — держит бэк (PersonalFilesController.SaveFromUrl)
    saveFromUrlPersonal: (personalId: string, url: string, path: string) =>
      request<{ path: string }>(`${personalFilesBase(personalId)}/save-from-url`, {
        method: 'POST',
        body: JSON.stringify({ url, path }),
      }),
    officeDiscard: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files/office-discard?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    getOfficeVersion: (projectId: string, path: string) =>
      projectRequest<{ ms: number }>(`/projects/${projectId}/files/office-version?path=${encodeURIComponent(path)}`),
    officeForceSave: (projectId: string, path: string) =>
      projectRequest<{ ok: boolean; reason?: string }>(`/projects/${projectId}/files/office-force-save?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    // Загрузка файла в текущую папку (Ф-7): multipart-поле file, ?path= каталога. Личный
    // маршрут ('personal') обслуживает только сервер — агентской ветки у него нет по построению,
    // поэтому устройствая проверка для него не нужна; проектный маршрут — как раньше
    upload: async (projectId: string, file: File, targetPath = ''): Promise<void> => {
      if (!isPersonalFilesId(projectId)) assertServerRoute(projectId, 'POST', 'files/upload');
      const token = readStoredToken();
      const form = new FormData();
      form.append('file', file);
      const res = await fetch(
        `/api${filesBase(projectId)}/upload?path=${encodeURIComponent(targetPath)}`,
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
