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
import type { ImageThreadSettings } from './threadsApi';

export interface ProjectPrefs extends ImageThreadSettings { characterSlug: string | null }

// Событие SignalR владельцу на каждую запись prefs проекта
export interface ImagePrefsChangedEvent { type: 'image_prefs_changed'; projectId: string; prefs: ProjectPrefs }

const DEFAULTS: ProjectPrefs = { provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null };
const key = (projectId: string) => `cc-image-prefs:${projectId}`;
// Разовый перенос prefs устройства на сервер: после него localStorage — лишь кэш
const migratedKey = (projectId: string) => `cc-image-prefs-migrated:${projectId}`;

const url = (projectId: string) => `/projects/${encodeURIComponent(projectId)}/image-editor/prefs`;

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
const same = (a: ProjectPrefs, b: ProjectPrefs) =>
  a.provider === b.provider && a.model === b.model && a.count === b.count
  && a.matchSourceSize === b.matchSourceSize && a.characterSlug === b.characterSlug;

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
  try {
    const saved = await prefsApi.put(projectId, getPrefs(projectId));
    s.inflight = false;
    if (s.dirty) { void flush(projectId); return; }
    applyServer(projectId, normalize(saved));
  } catch (e) {
    s.inflight = false;
    if (s.dirty) { void flush(projectId); return; }
    showToast(`Настройки картинок не сохранились: ${(e as Error).message}`, '', 'error');
  }
}

export function setPrefs(projectId: string, patch: Partial<ProjectPrefs>) {
  const next = { ...getPrefs(projectId), ...patch };
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

export function __resetPrefs() {
  _unsub?.();
  _unsub = null;
  _cache.clear();
  _sync.clear();
  _listeners.clear();
}
