// Индекс файлов проекта для ссылок на файлы в тексте ассистента: одно рекурсивное
// дерево на проект, дальше «существует ли такой файл» решается локально по Map.
// Без индекса каждое упоминание пути в ответе персоны стучало бы в API.
// Паттерн стора — как у featureFlags.ts: модульное состояние + useSyncExternalStore.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { toRelative, basename } from './paths';

// путь в нижнем регистре → путь как в дереве (ФС Windows регистронезависимая)
export type ProjectFileIndex = ReadonlyMap<string, string>;

const EMPTY: ProjectFileIndex = new Map();
const _indexes = new Map<string, ProjectFileIndex>();
const _checkedAt = new Map<string, number>();
const _inFlight = new Set<string>();
const _listeners = new Set<() => void>();

// Дерево устаревает: файлы, созданные в ходе чата, должны становиться кликабельными
// без перезагрузки страницы. Обновление ленивое — при следующем рендере текста.
const TTL_MS = 60_000;

function load(projectId: string) {
  if (_inFlight.has(projectId)) return;
  const at = _checkedAt.get(projectId);
  if (at != null && Date.now() - at < TTL_MS) return;
  _inFlight.add(projectId);
  api.files.tree(projectId, '', true)
    .then(entries => {
      const idx = new Map<string, string>();
      for (const e of entries) if (!e.isDirectory) idx.set(e.path.toLowerCase(), e.path);
      _indexes.set(projectId, idx);
      _listeners.forEach(fn => fn());
    })
    .catch(() => { /* индекса нет — пути остаются обычным текстом */ })
    .finally(() => { _checkedAt.set(projectId, Date.now()); _inFlight.delete(projectId); });
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Индекс файлов проекта; пока дерево не загружено (или чат вне проекта) — пустой,
// и упоминания путей рендерятся как обычный текст.
export function useProjectFileIndex(projectId: string | null): ProjectFileIndex {
  useEffect(() => { if (projectId) load(projectId); }, [projectId]);
  const snapshot = () => (projectId ? _indexes.get(projectId) ?? EMPTY : EMPTY);
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

// Вторичный индекс для B1 (голое имя файла / частичный путь-суффикс): basename в нижнем
// регистре → список путей дерева. Строится лениво из основного индекса и кэшируется по его
// ссылке (WeakMap) — пересчёт только когда дерево реально перезагрузилось (новый Map в load()).
const _basenameIndexes = new WeakMap<ProjectFileIndex, ReadonlyMap<string, string[]>>();

function basenameIndexOf(index: ProjectFileIndex): ReadonlyMap<string, string[]> {
  const cached = _basenameIndexes.get(index);
  if (cached) return cached;
  const map = new Map<string, string[]>();
  for (const treePath of index.values()) {
    const name = basename(treePath).toLowerCase();
    const list = map.get(name);
    if (list) list.push(treePath); else map.set(name, [treePath]);
  }
  _basenameIndexes.set(index, map);
  return map;
}

// Похоже на путь к файлу: заканчивается расширением с буквы (отсекает версии «v1.2.3» —
// после точки цифра, а не буква — и просто слова без расширения). Используется и в B1
// (суффиксный поиск ниже), и в A1 (MarkdownContent) — отсекать POSIX-роуты без расширения.
export const FILE_LIKE_MENTION = /\.[a-z][a-z0-9]{0,7}$/i;

// B1: голое имя файла («ChatItemView.tsx») или частичный путь-суффикс («chat/ChatItemView.tsx») —
// ссылка, только если в дереве проекта РОВНО один файл с таким basename и суффиксом (границы —
// начало строки или разделитель перед совпадением). Ноль или несколько кандидатов — не гадаем.
function lookupBySuffix(index: ProjectFileIndex, rel: string): string | null {
  if (!FILE_LIKE_MENTION.test(rel)) return null;
  const suffix = rel.replace(/\\/g, '/').replace(/^\/+/, '').toLowerCase();
  const name = basename(suffix);
  const candidates = basenameIndexOf(index).get(name);
  if (!candidates) return null;
  const matches = candidates.filter(treePath => {
    const pl = treePath.replace(/\\/g, '/').toLowerCase();
    return pl === suffix || pl.endsWith('/' + suffix);
  });
  return matches.length === 1 ? matches[0] : null;
}

// Резолв упоминания пути в файл проекта: абсолютный путь внутри корня приводится к
// относительному, якорь/квери и URL-экранирование снимаются. Точный путь приоритетнее
// суффиксного (B1). Возвращает путь как в дереве проекта либо null — тогда это не ссылка,
// а обычный текст (для абсолютных путей вне корня см. A1 в MarkdownContent).
export function lookupProjectFile(index: ProjectFileIndex, raw: string, rootPath: string): string | null {
  if (!raw || index.size === 0) return null;
  let p = raw.trim().split(/[#?]/)[0];
  if (!p) return null;
  try { p = decodeURIComponent(p); } catch { /* не URL-экранирован — как есть */ }
  const rel = toRelative(p, rootPath);
  if (!rel) return null;
  const exact = index.get(rel.replace(/\/+$/, '').toLowerCase());
  return exact ?? lookupBySuffix(index, rel);
}

// ── Пакетный кэш files/locate (F1b, разрез §4) ────────────────────────────────
// Пути, которых нет в индексе основного дерева, резолвятся не по одному запросу:
// упоминания копятся в пакет, дебаунс 50 мс доливает их в текущий пакет, жёсткий
// потолок 50 (сервер 400) flush'ит пакет сразу. Ответы и «не найдено» кэшируются на
// TTL индекса (60 с) — повторное упоминание того же пути в том же чате в сеть не идёт.
// Ключ кэша — projectId + нормализованный путь + sessionId: один rel в разных
// проектах и разных чатах резолвится в разные файлы (анкер — дерево чата).
export type LocatedFile = { path: string; branch?: string; isAnchor: boolean; alternatives?: number };

const LOCATE_DEBOUNCE_MS = 50;
const LOCATE_BATCH_MAX = 50;
const LOCATE_TTL_MS = TTL_MS; // тот же срок жизни, что у индекса

type LocateEntry = { key: string; path: string; resolve: (hit: LocatedFile | null) => void };
type LocateBatch = { entries: LocateEntry[]; timer: ReturnType<typeof setTimeout> | null };

const _locateCache = new Map<string, { at: number; hit: LocatedFile | null }>();
const _locateBatches = new Map<string, LocateBatch>();
// Promise запроса, который ещё не ответил: повтор того же пути в полёте — не второй вызов.
const _locateInFlight = new Map<string, Promise<LocatedFile | null>>();

// Ключ — projectId + нормализованный путь (слэши, без хвостовых, в нижнем регистре) + sessionId.
function locateKey(projectId: string, path: string, sessionId: string | undefined): string {
  const norm = path.replace(/\\/g, '/').replace(/^\/+/, '').toLowerCase();
  return `${projectId}\u0000${sessionId ?? ''}\u0000${norm}`;
}

function flushLocate(batchKey: string) {
  const batch = _locateBatches.get(batchKey);
  if (!batch || batch.entries.length === 0) return;
  if (batch.timer != null) clearTimeout(batch.timer);
  _locateBatches.delete(batchKey);
  const [projectId, sessionId] = batchKey.split('\u0000');
  const entries = batch.entries;
  api.files.locate(projectId, entries.map(e => e.path), sessionId || undefined)
    .then(res => {
      const byKey = new Map<string, LocatedFile>();
      for (const item of res) byKey.set(locateKey(projectId, item.path, sessionId), item);
      const at = Date.now();
      for (const e of entries) {
        const hit = byKey.get(e.key) ?? null; // «не найдено» — тоже ответ, тоже кэш
        _locateCache.set(e.key, { at, hit });
        _locateInFlight.delete(e.key);
        e.resolve(hit);
      }
    })
    .catch(() => {
      // Сбой сети — не «не найдено»: не кэшируем, следующая попытка реальна.
      for (const e of entries) { _locateInFlight.delete(e.key); e.resolve(null); }
    });
}

// Резолв одного «ленивого» пути через пакетный кэш (F2a: индекс → абсолютный → это).
// Из кэша отвечает сразу; иначе путь доливается в пакет и уходит после дебаунса —
// или сразу, если пакет набрал 50. Ответ — найденная запись или null.
export function locateFile(projectId: string, path: string, sessionId?: string): Promise<LocatedFile | null> {
  const key = locateKey(projectId, path, sessionId);
  const cached = _locateCache.get(key);
  if (cached && Date.now() - cached.at < LOCATE_TTL_MS) return Promise.resolve(cached.hit);
  const inflight = _locateInFlight.get(key);
  if (inflight) return inflight;

  const batchKey = `${projectId}\u0000${sessionId ?? ''}`;
  let batch = _locateBatches.get(batchKey);
  if (!batch) { batch = { entries: [], timer: null }; _locateBatches.set(batchKey, batch); }

  const entry: LocateEntry = { key, path, resolve: () => undefined };
  const promise = new Promise<LocatedFile | null>(resolve => { entry.resolve = resolve; });
  batch.entries.push(entry);
  _locateInFlight.set(key, promise);

  if (batch.entries.length >= LOCATE_BATCH_MAX) {
    flushLocate(batchKey);
  } else {
    if (batch.timer != null) clearTimeout(batch.timer);
    batch.timer = setTimeout(() => flushLocate(batchKey), LOCATE_DEBOUNCE_MS);
  }
  return promise;
}
