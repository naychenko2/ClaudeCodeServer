// Настройки запуска и персонаж. У выбранной картинки настройки — её (settings нити на
// сервере), без выбора — умолчания проекта: их подхватит следующая картинка («Настройки
// без выбора не пропадают»), и по ним же рисует агент. Персонаж — на проект: при смене
// выбранной картинки он остаётся подключённым.
//
// Источник правды — сервер (GET/PUT …/image-editor/prefs и событие image_prefs_changed),
// localStorage — только кэш для мгновенного первого рендера. Запись оптимистичная: в
// полёте не больше одного PUT, быстрые правки сливаются в следующий.

import { useEffect, useSyncExternalStore } from 'react';
import { onMessage, onReconnected, request, showToast } from 'aihome_shell/kit';
import { scopeBase } from '../scope';
import type { ImageThreadSettings } from './threadsApi';
import type { EditMode, ImageEditOp } from '../api';
import type { OutpaintRatio } from '../editorInputs';
import type { ImageMode } from './modeState';

// Выбор режимов «Создать» и «Править» панели v5; null в полях — берётся плоское поле. Сервер
// сливает PUT: нет режима в теле — сохранённый остаётся
export interface ImageCreatePrefs { provider: string | null; model: string | null; count: number | null }
export interface ImageEditPrefs {
  provider: string | null;
  model: string | null;
  count: number | null;
  op: Exclude<ImageEditOp, 'generate'> | null;
  editMode: EditMode | null;
  ratio: OutpaintRatio | null;
}

export interface ProjectPrefs extends ImageThreadSettings {
  characterSlug: string | null;
  create?: ImageCreatePrefs | null;
  edit?: ImageEditPrefs | null;
}

// Событие SignalR владельцу на каждую запись prefs проекта
export interface ImagePrefsChangedEvent { type: 'image_prefs_changed'; projectId: string; prefs: ProjectPrefs }

const DEFAULTS: ProjectPrefs = { provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null };
const key = (projectId: string) => `cc-image-prefs:${projectId}`;
// Разовый перенос prefs устройства на сервер: после него localStorage — лишь кэш
const migratedKey = (projectId: string) => `cc-image-prefs-migrated:${projectId}`;

const url = (projectId: string) => `${scopeBase(projectId)}/prefs`;

export const prefsApi = {
  get: (projectId: string) => request<ProjectPrefs>(url(projectId), { live: true }),
  put: (projectId: string, prefs: ProjectPrefs) =>
    request<ProjectPrefs>(url(projectId), { method: 'PUT', body: JSON.stringify(prefs) }),
  subscribe: (handler: (e: ImagePrefsChangedEvent) => void) => onMessage(msg => {
    const m = msg as unknown as { type?: string };
    if (m.type === 'image_prefs_changed') handler(m as unknown as ImagePrefsChangedEvent);
  }),
};

interface Sync {
  loaded: boolean;
  loading: boolean;
  // Локальная правка, ещё не подтверждённая сервером: чужие значения её не перетирают
  inflight: boolean;
  dirty: boolean;
  // Счётчик локальных правок: ответ GET, начатого до правки, устарел
  seq: number;
}

const _cache = new Map<string, ProjectPrefs>();
const _sync = new Map<string, Sync>();
let _version = 0;
const _listeners = new Set<() => void>();
let _unsub: (() => void) | null = null;

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

function sync(projectId: string): Sync {
  let s = _sync.get(projectId);
  if (!s) { s = { loaded: false, loading: false, inflight: false, dirty: false, seq: 0 }; _sync.set(projectId, s); }
  return s;
}

const normalize = (v: Partial<ProjectPrefs> | null | undefined): ProjectPrefs => ({ ...DEFAULTS, ...(v ?? {}) });
// Режимные части сравниваются по полям: без них эхо сервера со свежим выбором режима
// считалось бы «тем же» и не доходило до кэша
const sameCreate = (a: ImageCreatePrefs | null | undefined, b: ImageCreatePrefs | null | undefined) =>
  !a || !b ? !a === !b : a.provider === b.provider && a.model === b.model && a.count === b.count;
const sameEdit = (a: ImageEditPrefs | null | undefined, b: ImageEditPrefs | null | undefined) =>
  !a || !b ? !a === !b : sameCreate(a, b) && a.op === b.op && a.editMode === b.editMode && a.ratio === b.ratio;
const same = (a: ProjectPrefs, b: ProjectPrefs) =>
  a.provider === b.provider && a.model === b.model && a.count === b.count
  && a.matchSourceSize === b.matchSourceSize && a.characterSlug === b.characterSlug
  && sameCreate(a.create, b.create) && sameEdit(a.edit, b.edit);

// Режимные части, правленные здесь и ещё не отправленные. PUT несёт режим, только если его
// правили: сервер без режима в теле оставляет сохранённый, и устаревший кэш вкладки не
// затрёт свежий выбор, сделанный в другой
const _modesDirty = new Map<string, { create: boolean; edit: boolean }>();

function body(projectId: string): ProjectPrefs {
  const { create, edit, ...flat } = getPrefs(projectId);
  const d = _modesDirty.get(projectId);
  _modesDirty.delete(projectId);
  return { ...flat, ...(d?.create ? { create } : null), ...(d?.edit ? { edit } : null) };
}

function markModes(projectId: string, sent: ProjectPrefs) {
  const d = _modesDirty.get(projectId) ?? { create: false, edit: false };
  _modesDirty.set(projectId, { create: d.create || 'create' in sent, edit: d.edit || 'edit' in sent });
}

function readStored(projectId: string): ProjectPrefs | null {
  try {
    const raw = localStorage.getItem(key(projectId));
    return raw ? normalize(JSON.parse(raw) as Partial<ProjectPrefs>) : null;
  } catch { return null; /* приватный режим или мусор в хранилище */ }
}

function store(projectId: string, v: ProjectPrefs) {
  _cache.set(projectId, v);
  try { localStorage.setItem(key(projectId), JSON.stringify(v)); } catch { /* приватный режим */ }
  emit();
}

export function getPrefs(projectId: string): ProjectPrefs {
  const hit = _cache.get(projectId);
  if (hit) return hit;
  const v = readStored(projectId) ?? DEFAULTS;
  _cache.set(projectId, v);
  return v;
}

// Ответ сервера: локальная правка в пути старше него — не трогаем
function applyServer(projectId: string, v: ProjectPrefs) {
  const s = sync(projectId);
  s.loaded = true;
  if (s.inflight || s.dirty) return;
  if (!same(getPrefs(projectId), v)) store(projectId, v);
}

async function flush(projectId: string) {
  const s = sync(projectId);
  if (s.inflight) { s.dirty = true; return; }
  s.inflight = true;
  s.dirty = false;
  const sent = body(projectId);
  try {
    const saved = await prefsApi.put(projectId, sent);
    s.inflight = false;
    if (s.dirty) { void flush(projectId); return; }
    applyServer(projectId, normalize(saved));
  } catch (e) {
    s.inflight = false;
    // Неотправленный выбор режима уйдёт со следующей записью
    markModes(projectId, sent);
    if (s.dirty) { void flush(projectId); return; }
    showToast(`Настройки картинок не сохранились: ${(e as Error).message}`, '', 'error');
  }
}

export function setPrefs(projectId: string, patch: Partial<ProjectPrefs>) {
  const next = { ...getPrefs(projectId), ...patch };
  markModes(projectId, patch as ProjectPrefs);
  sync(projectId).seq++;
  store(projectId, next);
  void flush(projectId);
}

function markMigrated(projectId: string) {
  try { localStorage.setItem(migratedKey(projectId), '1'); } catch { /* приватный режим */ }
}

async function load(projectId: string, force = false) {
  const s = sync(projectId);
  if (s.loading || (s.loaded && !force)) return;
  s.loading = true;
  const seq = s.seq;
  const local = readStored(projectId);
  try {
    const server = normalize(await prefsApi.get(projectId));
    s.loading = false;
    if (s.seq !== seq) { s.loaded = true; return; }
    let migrated = false;
    try { migrated = localStorage.getItem(migratedKey(projectId)) === '1'; } catch { /* приватный режим */ }
    if (!migrated) {
      markMigrated(projectId);
      // На сервере умолчания, а устройство помнит выбор — переносим его один раз
      if (same(server, DEFAULTS) && local && !same(local, DEFAULTS)) {
        s.loaded = true;
        store(projectId, local);
        void flush(projectId);
        return;
      }
    }
    applyServer(projectId, server);
  } catch {
    // Модуль выключен или сервер недоступен — живём на кэше устройства
    s.loading = false;
    s.loaded = true;
  }
}

function ensureLive() {
  if (_unsub) return;
  const offEvents = prefsApi.subscribe(ev => {
    if (!_sync.get(ev.projectId)?.loaded) return;
    applyServer(ev.projectId, normalize(ev.prefs));
  });
  // После обрыва события могли потеряться — перечитываем загруженные проекты
  const offRe = onReconnected(() => {
    _sync.forEach((s, pid) => { if (s.loaded) void load(pid, true); });
  });
  _unsub = () => { offEvents(); offRe(); };
}

// Вход в проект: prefs с сервера и живые обновления с других вкладок и устройств
export function ensurePrefs(projectId: string): Promise<void> {
  ensureLive();
  return load(projectId);
}

export function usePrefs(projectId: string): ProjectPrefs {
  useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
  useEffect(() => { void ensurePrefs(projectId); }, [projectId]);
  return getPrefs(projectId);
}

// Настройки, с которыми пойдёт запуск: у нити — свои, иначе умолчания проекта
export function effectiveSettings(prefs: ProjectPrefs, own: ImageThreadSettings | null | undefined): ImageThreadSettings {
  return own ?? { provider: prefs.provider, model: prefs.model, count: prefs.count, matchSourceSize: prefs.matchSourceSize };
}

// Выбор режима «Создать» и «Править» (флаг image-panel-v5). Поставщик и модель — парой, как на
// сервере: модель плоских полей могла быть выбрана у другого поставщика
function modePick(prefs: ProjectPrefs, m: ImageCreatePrefs | null | undefined): ImageThreadSettings {
  const own = !!m && (m.provider != null || m.model != null);
  return {
    provider: own ? m!.provider : prefs.provider,
    model: own ? m!.model : prefs.model,
    count: m?.count ?? prefs.count,
    matchSourceSize: prefs.matchSourceSize,
  };
}

// Настройки, с которыми пойдёт запуск в режиме: «Создать» — выбор «Создать» проекта, «Править» —
// настройки нити, иначе выбор «Править»
export function modeSettings(mode: ImageMode, prefs: ProjectPrefs, own: ImageThreadSettings | null | undefined): ImageThreadSettings {
  return mode === 'create' ? modePick(prefs, prefs.create) : own ?? modePick(prefs, prefs.edit);
}

const EMPTY_EDIT: ImageEditPrefs = { provider: null, model: null, count: null, op: null, editMode: null, ratio: null };

// Запись выбора режима: поставщик, модель и число — в его часть, «размер оригинала» общий
// на оба режима и пишется в плоское поле
export function setModeSettings(projectId: string, mode: ImageMode, patch: Partial<ImageThreadSettings>) {
  const prefs = getPrefs(projectId);
  const cur = modePick(prefs, mode === 'create' ? prefs.create : prefs.edit);
  const part = {
    provider: patch.provider !== undefined ? patch.provider : cur.provider,
    model: patch.model !== undefined ? patch.model : cur.model,
    count: patch.count ?? cur.count,
  };
  const flat = patch.matchSourceSize !== undefined ? { matchSourceSize: patch.matchSourceSize } : null;
  setPrefs(projectId, mode === 'create'
    ? { ...flat, create: part }
    : { ...flat, edit: { ...(prefs.edit ?? EMPTY_EDIT), ...part } });
}

// Операция, режим подбора и пропорции «Править» (бывший выбор панели в памяти вкладки)
export function setEditChoice(projectId: string, patch: Partial<Pick<ImageEditPrefs, 'op' | 'editMode' | 'ratio'>>) {
  const prefs = getPrefs(projectId);
  setPrefs(projectId, { edit: { ...(prefs.edit ?? EMPTY_EDIT), ...patch } });
}

export function __resetPrefs() {
  _unsub?.();
  _unsub = null;
  _cache.clear();
  _sync.clear();
  _modesDirty.clear();
  _listeners.clear();
}
