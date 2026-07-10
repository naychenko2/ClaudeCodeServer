// Офлайн-слой заметок. Заметки — .md файлы; серверный id = f(source, path) и МЕНЯЕТСЯ
// при переименовании/переносе. Поэтому офлайн-слой ключуется стабильным клиентским
// localKey (uuid), а serverId хранится рядом и ПЕРЕ-УКАЗЫВАЕТСЯ при смене id (свой
// create/update-ответ). Rename/move офлайн запрещены (см. фич-флаг notes-offline).
//
// Хранение: noteContent (редактируемый контент по localKey) + notesOutbox (очередь
// мутаций) + meta:notes:localKey-index (serverId → localKey). Конфликт правки —
// копией «{Title} (conflict …)» (стиль Obsidian Sync), данные не теряются.

import { api } from './api';
import { isOnline, OfflineError } from './offline';
import {
  idbGet, idbSet,
  noteContentGet, noteContentPut, noteContentDelete, noteContentAll,
  notesOutboxAll, notesOutboxPut, notesOutboxDelete,
} from './idb';
import type { CreateNoteDto, NoteDetail, NoteSummary } from '../types';

export interface NoteRecord {
  localKey: string;             // стабильный клиентский ключ
  serverId: string | null;      // текущий серверный id (меняется при rename/move)
  source: string;
  path: string;
  title: string;
  content: string;
  tags: string[];
  baseUpdatedAt: string | null; // updatedAt версии, на которой базируется правка (для конфликтов)
  dirty: boolean;               // есть несинхронизированная локальная правка
  createdOffline: boolean;      // создана офлайн, ещё не на сервере (пока serverId=null)
  deletedOffline: boolean;      // удалена офлайн, ждёт синка
  localUpdatedAt: number;
}

type NoteOpKind = 'create' | 'update' | 'delete';
interface NotePayload { title?: string; content?: string; source?: string; folder?: string }

export interface NotesOutboxOp {
  opId: number;                 // монотонный — FIFO
  localKey: string;
  kind: NoteOpKind;
  payload: NotePayload;
  baseUpdatedAt?: string | null;
  attempts: number;
}

// === Индекс serverId → localKey (в meta + кэш в памяти) ===

const INDEX_KEY = 'notes:localKey-index';
let _index: Record<string, string> | null = null;

async function loadIndex(): Promise<Record<string, string>> {
  if (!_index) _index = (await idbGet<Record<string, string>>(INDEX_KEY).catch(() => undefined))?.data ?? {};
  return _index;
}
async function saveIndex(): Promise<void> {
  if (_index) await idbSet(INDEX_KEY, { data: _index, savedAt: Date.now() }).catch(() => {});
}
async function setIndex(serverId: string, localKey: string): Promise<void> {
  (await loadIndex())[serverId] = localKey; await saveIndex();
}
async function removeIndex(serverId: string): Promise<void> {
  delete (await loadIndex())[serverId]; await saveIndex();
}
async function localKeyForServerId(serverId: string): Promise<string | undefined> {
  return (await loadIndex())[serverId];
}
async function findLocalKeyByPath(source: string, path: string): Promise<string | undefined> {
  const recs = await noteContentAll<NoteRecord>().catch(() => []);
  return recs.find(r => r.source === source && r.path === path)?.localKey;
}

// Найти запись по id (localKey напрямую или serverId через индекс)
async function resolveRecord(id: string): Promise<NoteRecord | undefined> {
  const direct = await noteContentGet<NoteRecord>(id).catch(() => undefined);
  if (direct) return direct;
  const lk = await localKeyForServerId(id);
  return lk ? noteContentGet<NoteRecord>(lk).catch(() => undefined) : undefined;
}

// === Построители NoteDetail ===

function labelFor(source: string): string {
  return source === 'personal' ? 'Личный' : source;
}

// NoteDetail из локальной записи (связи/backlinks — серверные, офлайн пусты)
function synthesizeDetail(rec: NoteRecord): NoteDetail {
  const iso = new Date(rec.localUpdatedAt).toISOString();
  return {
    id: rec.serverId ?? rec.localKey,
    title: rec.title, source: rec.source, sourceLabel: labelFor(rec.source),
    path: rec.path, content: rec.content, tags: rec.tags,
    links: [], backlinks: [], unlinkedMentions: [],
    createdAt: iso, updatedAt: iso,
  };
}

// Записать/обновить запись из серверной детали (не затираем dirty-черновик)
async function upsertFromDetail(d: NoteDetail): Promise<NoteRecord> {
  const lk = (await localKeyForServerId(d.id)) ?? (await findLocalKeyByPath(d.source, d.path)) ?? crypto.randomUUID();
  const rec: NoteRecord = {
    localKey: lk, serverId: d.id, source: d.source, path: d.path, title: d.title,
    content: d.content, tags: d.tags, baseUpdatedAt: d.updatedAt,
    dirty: false, createdOffline: false, deletedOffline: false, localUpdatedAt: Date.now(),
  };
  await noteContentPut(rec);
  await setIndex(d.id, lk);
  return rec;
}

const dirOf = (path: string): string => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '');

// === Чтение для NoteView ===

export async function getNoteForView(id: string): Promise<NoteDetail> {
  const rec = await resolveRecord(id);
  if (isOnline()) {
    const serverId = rec?.serverId ?? id;
    try {
      const detail = await api.notes.get(serverId);
      if (rec?.dirty) return { ...detail, content: rec.content };   // черновик поверх серверных связей
      await upsertFromDetail(detail);
      return detail;
    } catch (e) {
      if (!(e instanceof OfflineError)) throw e;   // не сеть — реальная ошибка
    }
  }
  if (!rec) throw new OfflineError('Заметка недоступна офлайн');
  return synthesizeDetail(rec);
}

// === Запись ===

let _seq = 0;
let _seqInit: Promise<void> | null = null;
async function nextNoteSeq(): Promise<number> {
  if (!_seqInit) {
    _seqInit = notesOutboxAll<NotesOutboxOp>()
      .then(ops => { _seq = ops.reduce((m, o) => Math.max(m, o.opId), 0); })
      .catch(() => {});
  }
  await _seqInit;
  return ++_seq;
}

// Наложить только определённые поля payload
function assignDefined(base: NotePayload, patch: NotePayload): NotePayload {
  const out: NotePayload = { ...base };
  for (const [k, v] of Object.entries(patch) as [keyof NotePayload, unknown][])
    if (v !== undefined) (out as Record<string, unknown>)[k] = v;
  return out;
}

export type NoteMerge = { kind: NoteOpKind; payload: NotePayload } | 'drop';
export function mergeNoteOps(last: NotesOutboxOp, kind: NoteOpKind, payload: NotePayload): NoteMerge {
  if (last.kind === 'delete') return { kind: 'delete', payload: {} };
  if (last.kind === 'create') {
    if (kind === 'delete') return 'drop';                 // создано и удалено офлайн → 0 сети
    return { kind: 'create', payload: assignDefined(last.payload, payload) };
  }
  // last.kind === 'update'
  if (kind === 'delete') return { kind: 'delete', payload: {} };
  return { kind: 'update', payload: assignDefined(last.payload, payload) };
}

async function enqueueNote(localKey: string, kind: NoteOpKind, payload: NotePayload, baseUpdatedAt?: string | null): Promise<void> {
  const ops = (await notesOutboxAll<NotesOutboxOp>().catch(() => []))
    .filter(o => o.localKey === localKey).sort((a, b) => a.opId - b.opId);
  const last = ops[ops.length - 1];
  if (!last) {
    await notesOutboxPut({ opId: await nextNoteSeq(), localKey, kind, payload, baseUpdatedAt, attempts: 0 }).catch(() => {});
    return;
  }
  const merged = mergeNoteOps(last, kind, payload);
  if (merged === 'drop') { for (const o of ops) await notesOutboxDelete(o.opId).catch(() => {}); return; }
  for (const o of ops.slice(0, -1)) await notesOutboxDelete(o.opId).catch(() => {});
  await notesOutboxPut({ ...last, kind: merged.kind, payload: merged.payload, baseUpdatedAt: baseUpdatedAt ?? last.baseUpdatedAt, attempts: 0 }).catch(() => {});
}

async function dropNoteOps(localKey: string): Promise<void> {
  const ops = (await notesOutboxAll<NotesOutboxOp>().catch(() => [])).filter(o => o.localKey === localKey);
  for (const o of ops) await notesOutboxDelete(o.opId).catch(() => {});
}

// Сохранить правку контента (title меняем только у созданной офлайн — rename запрещён)
export async function saveNoteOffline(id: string, patch: { content: string; title?: string }): Promise<void> {
  const rec = await resolveRecord(id);
  if (!rec) return;
  rec.content = patch.content;
  if (patch.title !== undefined && rec.createdOffline && !rec.serverId) rec.title = patch.title;
  rec.dirty = true;
  rec.localUpdatedAt = Date.now();
  await noteContentPut(rec);

  const asCreate = rec.createdOffline && !rec.serverId;
  await enqueueNote(
    rec.localKey,
    asCreate ? 'create' : 'update',
    asCreate
      ? { title: rec.title, content: patch.content, source: rec.source, folder: dirOf(rec.path) }
      : { content: patch.content },
    rec.baseUpdatedAt,
  );
}

// Создать заметку офлайн: localKey = временный id; в список попадёт через overlay
export async function createNoteOffline(dto: CreateNoteDto): Promise<string> {
  const localKey = crypto.randomUUID();
  const folder = dto.folder?.trim();
  const title = dto.title.trim();
  const path = folder ? `${folder}/${title}.md` : `${title}.md`;
  const rec: NoteRecord = {
    localKey, serverId: null, source: dto.source ?? 'personal', path, title,
    content: dto.content ?? `# ${title}\n`, tags: [],
    baseUpdatedAt: null, dirty: true, createdOffline: true, deletedOffline: false, localUpdatedAt: Date.now(),
  };
  await noteContentPut(rec);
  await enqueueNote(localKey, 'create', { title, content: rec.content, source: rec.source, folder });
  return localKey;
}

export async function deleteNoteOffline(id: string): Promise<void> {
  const rec = await resolveRecord(id);
  if (!rec) return;
  if (rec.createdOffline && !rec.serverId) {
    await dropNoteOps(rec.localKey);            // не дошла до сервера — тихо убираем
    await noteContentDelete(rec.localKey);
    return;
  }
  rec.deletedOffline = true; rec.dirty = true; rec.localUpdatedAt = Date.now();
  await noteContentPut(rec);
  await enqueueNote(rec.localKey, 'delete', {}, rec.baseUpdatedAt);
}

// === Синхронизация ===

// Уведомить UI, что localKey/старый serverId сменились на новый serverId
function notifyRemap(from: string[], to: string): void {
  if (typeof window !== 'undefined')
    window.dispatchEvent(new CustomEvent('cc-note-remapped', { detail: { from, to } }));
}

async function remapNote(localKey: string, d: NoteDetail): Promise<void> {
  const prev = await noteContentGet<NoteRecord>(localKey).catch(() => undefined);
  const prevServerId = prev?.serverId ?? null;
  const next: NoteRecord = {
    localKey, serverId: d.id, source: d.source, path: d.path, title: d.title,
    content: d.content, tags: d.tags, baseUpdatedAt: d.updatedAt,
    dirty: false, createdOffline: false, deletedOffline: false, localUpdatedAt: Date.now(),
  };
  await noteContentPut(next);
  if (prevServerId && prevServerId !== d.id) await removeIndex(prevServerId);
  await setIndex(d.id, localKey);
  const from = [localKey, ...(prevServerId ? [prevServerId] : [])];
  if (d.id !== localKey) notifyRemap(from, d.id);
}

function conflictStamp(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function notifyConflict(title: string): void {
  if (typeof window !== 'undefined')
    window.dispatchEvent(new CustomEvent('cc-note-conflict', { detail: { title } }));
}

// Серверная версия изменилась под нами → сохраняем локальную правку конфликт-копией,
// а канонической оставляем серверную (перечитываем её в локальную запись).
async function makeConflictCopy(rec: NoteRecord, op: NotesOutboxOp): Promise<void> {
  const content = op.payload.content ?? rec.content;
  await api.notes.create({ title: `${rec.title} (conflict ${conflictStamp()})`, content, source: rec.source, folder: dirOf(rec.path) });
  if (rec.serverId) {
    const cur = await api.notes.get(rec.serverId).catch(() => null);
    if (cur) await remapNote(rec.localKey, cur);
  }
  notifyConflict(rec.title);
}

// Заметки на сервере уже нет (переименована/удалена) — воскрешаем правку новой заметкой
async function resurrectAsCopy(rec: NoteRecord, op: NotesOutboxOp): Promise<void> {
  const d = await api.notes.create({
    title: `${rec.title} (offline ${conflictStamp()})`,
    content: op.payload.content ?? rec.content, source: rec.source, folder: dirOf(rec.path),
  });
  await remapNote(rec.localKey, d);
  notifyConflict(rec.title);
}

async function applyNoteOp(op: NotesOutboxOp): Promise<void> {
  const rec = await noteContentGet<NoteRecord>(op.localKey).catch(() => undefined);
  if (op.kind === 'create') {
    const d = await api.notes.create({
      title: op.payload.title ?? rec?.title ?? 'Без названия',
      content: op.payload.content, source: op.payload.source, folder: op.payload.folder,
    });
    await remapNote(op.localKey, d);
    return;
  }
  if (op.kind === 'delete') {
    if (rec?.serverId) {
      try { await api.notes.delete(rec.serverId); }
      catch (e) { if (httpStatus(e) !== 404) throw e; }   // уже нет — успех
      await removeIndex(rec.serverId);
    }
    await noteContentDelete(op.localKey);
    return;
  }
  // update
  if (!rec?.serverId) return;   // нечего обновлять (ещё не создана) — create-op сделает это
  const cur = await api.notes.get(rec.serverId);   // проверка конфликта (может кинуть 404)
  if (op.baseUpdatedAt && cur.updatedAt !== op.baseUpdatedAt) {
    await makeConflictCopy(rec, op);
  } else {
    const d = await api.notes.update(rec.serverId, { content: op.payload.content });
    await remapNote(op.localKey, d);
  }
}

function httpStatus(e: unknown): number | undefined {
  return (e as { status?: number } | null)?.status;
}

// true — операция обработана и должна быть снята; false — транзиентная, повтор
async function handleNoteOpError(op: NotesOutboxOp, e: unknown): Promise<boolean> {
  const status = httpStatus(e);
  if (op.kind === 'update' && status === 404) {
    const rec = await noteContentGet<NoteRecord>(op.localKey).catch(() => undefined);
    if (rec) await resurrectAsCopy(rec, op);   // правка не теряется
    return true;
  }
  if (typeof status === 'number' && status >= 400 && status < 500) {
    console.warn('[notesOffline] операция отклонена сервером, снята:', op.kind, op.localKey, e);
    return true;
  }
  return false;   // 5xx/прочее — повтор позже
}

let _draining = false;

export async function drainNotesOutbox(): Promise<boolean> {
  if (!isOnline() || _draining) return false;
  _draining = true;
  let changed = false;
  try {
    const ops = (await notesOutboxAll<NotesOutboxOp>().catch(() => [] as NotesOutboxOp[])).sort((a, b) => a.opId - b.opId);
    for (const op of ops) {
      try {
        await applyNoteOp(op);
        await notesOutboxDelete(op.opId).catch(() => {});
        changed = true;
      } catch (e) {
        if (e instanceof OfflineError) break;
        const handled = await handleNoteOpError(op, e).catch(() => false);
        if (handled) { await notesOutboxDelete(op.opId).catch(() => {}); changed = true; }
        else {
          const attempts = op.attempts + 1;
          if (attempts >= 6) { await notesOutboxDelete(op.opId).catch(() => {}); changed = true; }
          else { await notesOutboxPut({ ...op, attempts }).catch(() => {}); break; }
        }
      }
    }
  } finally {
    _draining = false;
  }
  return changed;
}

// === Оверлей списка (createdOffline добавить, deletedOffline убрать) ===

function recToSummary(rec: NoteRecord): NoteSummary {
  const iso = new Date(rec.localUpdatedAt).toISOString();
  return {
    id: rec.serverId ?? rec.localKey, title: rec.title, source: rec.source,
    sourceLabel: labelFor(rec.source), path: rec.path, tags: rec.tags,
    createdAt: iso, updatedAt: iso,
  };
}

export async function overlayNotesList(serverList: NoteSummary[]): Promise<NoteSummary[]> {
  const recs = await noteContentAll<NoteRecord>().catch(() => [] as NoteRecord[]);
  if (!recs.length) return serverList;
  const deletedServerIds = new Set(recs.filter(r => r.deletedOffline && r.serverId).map(r => r.serverId!));
  const list = serverList.filter(n => !deletedServerIds.has(n.id));
  const created = recs.filter(r => r.createdOffline && !r.serverId && !r.deletedOffline).map(recToSummary);
  return [...created, ...list];
}

// === Офлайн-резолв вики-ссылки (hover-preview / embed ![[…]]) ===

export function sliceFragment(content: string, anchor: string): string {
  const norm = anchor.trim().toLowerCase();
  const lines = content.split('\n');
  const start = lines.findIndex(l => /^#{1,6}\s+/.test(l) && l.replace(/^#{1,6}\s+/, '').trim().toLowerCase() === norm);
  if (start < 0) return content;
  const level = (lines[start].match(/^#+/)?.[0].length) ?? 1;
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i++) {
    const m = lines[i].match(/^(#{1,6})\s+/);
    if (m && m[1].length <= level) { end = i; break; }
  }
  return lines.slice(start, end).join('\n');
}

export async function offlineResolve(name: string, anchor?: string): Promise<{ title: string; content: string } | null> {
  const target = name.split('/').pop()!.split('#')[0].trim().toLowerCase();
  const recs = await noteContentAll<NoteRecord>().catch(() => []);
  const rec = recs.find(r => !r.deletedOffline && r.title.trim().toLowerCase() === target);
  if (!rec) return null;
  return { title: rec.title, content: anchor ? sliceFragment(rec.content, anchor) : rec.content };
}

// === Прогрев (для sync.ts) ===

// Скачать контент заметки в локальный слой, не затирая dirty-черновик
export async function warmNote(d: NoteDetail): Promise<void> {
  const lk = (await localKeyForServerId(d.id)) ?? (await findLocalKeyByPath(d.source, d.path));
  if (lk) {
    const rec = await noteContentGet<NoteRecord>(lk).catch(() => undefined);
    if (rec?.dirty || rec?.deletedOffline) return;
  }
  await upsertFromDetail(d);
}

// Есть ли несинхронизированные правки (для индикаторов/условий)
export async function hasPendingNotes(): Promise<boolean> {
  const ops = await notesOutboxAll<NotesOutboxOp>().catch(() => []);
  return ops.length > 0;
}
