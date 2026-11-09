// Методы REST «Заметок» (Obsidian-совместимая база знаний): .md файлы в личном vault
// и notes/ проектов. lib/api.ts подмешивает их в объект api spread'ом (адрес
// api.notes.* остаётся прежним для потребителей); типы — в features/notes/types.ts,
// доступны через ../types. Файл вне features/notes/: прямой импорт lib/offline там
// запрещён сторожем графа хранилища заметок. Класс конфликта — в features/notes/api.ts.
import { conflictBodyOf, request } from './offline';
import type { EtagRef } from './offline';
import type {
  CreateNoteDto,
  DocAnnotation,
  NoteBacklink,
  NoteFolder,
  NoteGraph,
  NoteReply,
  NoteSemanticHit,
  NoteSource,
  NoteSummary,
  NoteTask,
  NoteTemplate,
  Task,
  UpdateNoteDto,
} from '../types';
import type { NoteDetail } from '../features/notes/types';
// eslint-disable-next-line module/no-cross-feature-import -- контракт конфликта заметки живёт в домене
import { NoteConflictError } from '../features/notes/api';
// eslint-disable-next-line module/no-cross-feature-import -- контракт конфликта заметки живёт в домене
import type { NoteWithEtag } from '../features/notes/api';

export const notesApi = {
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
    graph: (annotations?: boolean) =>
      request<NoteGraph>(`/notes/graph${annotations ? '?annotations=true' : ''}`),
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
    // Комментарии к документам (флаг doc-annotations): создание с verify-guard (409 —
    // документ изменился), список с резолвом привязки, смена статуса open/resolved
    annotate: (dto: {
      doc: { scope: string; path: string };
      selection: { start: number; end: number; text: string };
      comment?: string; tags?: string[]; title?: string;
    }) => request<NoteDetail>('/notes/annotate', { method: 'POST', body: JSON.stringify(dto) }),
    annotations: (scope: string, path: string) =>
      request<DocAnnotation[]>(
        `/notes/annotations?scope=${encodeURIComponent(scope)}&path=${encodeURIComponent(path)}`),
    setStatus: (id: string, status: 'open' | 'resolved') =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/status`, {
        method: 'POST', body: JSON.stringify({ status }),
      }),
    repin: (id: string, selection: { start: number; end: number; text: string }) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/repin`, {
        method: 'POST', body: JSON.stringify(selection),
      }),
    reply: (id: string, comment: string, tags?: string[]) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/reply`, {
        method: 'POST', body: JSON.stringify({ comment, tags }),
      }),
    replies: (id: string) => request<NoteReply[]>(`/notes/${encodeURIComponent(id)}/replies`),
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
    suggestTitle: (id: string) =>
      request<{ title: string }>(`/notes/${encodeURIComponent(id)}/suggest-title`, { method: 'POST' }),
    dailySummary: (date: string) =>
      request<NoteDetail>('/notes/daily/summary', { method: 'POST', body: JSON.stringify({ date }) }),
    toc: (id: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/toc`, { method: 'POST' }),
    translate: (id: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/translate`, { method: 'POST' }),
    // etag (S2c-F3, К8): тег прочитанной версии — заголовок ETag ответа (quoted sha256,
    // NotesController.Get). По нему следующее сохранение идёт по If-Match. Офлайн-доставка
    // из кэша тега не имеет: офлайн-правка идёт своим путём (notesOffline), не по If-Match.
    get: async (id: string): Promise<NoteWithEtag> => {
      const etagRef: EtagRef = {};
      const d = await request<NoteDetail>(`/notes/${encodeURIComponent(id)}`, { etagRef });
      return { ...d, etag: etagRef.etag };
    },
    backlinks: (id: string) => request<NoteBacklink[]>(`/notes/${encodeURIComponent(id)}/backlinks`),
    create: (dto: CreateNoteDto) =>
      request<NoteDetail>('/notes', { method: 'POST', body: JSON.stringify(dto) }),
    // ifMatch (К8): тег версии, которую видел вызывающий — etag из get или sha256 из
    // NoteConflictError (повторная отправка по актуальному состоянию). С If-Match сервер либо
    // пишет (200 + новый ETag), либо отказывает 409 note_changed, не трогая заметку, — он
    // становится NoteConflictError с полями тела. Без ifMatch — ровно как раньше: заголовка
    // нет, 409 по конфликту сервер не отдаёт, существующие вызывающие ничего не заметили.
    // etagOut: новый ETag ответа 200 — по нему идёт следующее сохранение (NotesController.Update).
    update: async (id: string, dto: UpdateNoteDto, ifMatch?: string, etagOut?: EtagRef): Promise<NoteWithEtag> => {
      const sink: EtagRef = etagOut ?? {};
      try {
        const d = await request<NoteDetail>(`/notes/${encodeURIComponent(id)}`, {
          method: 'PUT',
          body: JSON.stringify(dto),
          ...(ifMatch ? { headers: { 'If-Match': ifMatch } } : {}),
          etagRef: sink,
        });
        return { ...d, etag: sink.etag };
      } catch (e) {
        const b = conflictBodyOf(e, 'note_changed'); if (b) throw new NoteConflictError(b); throw e;
      }
    },
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
};
