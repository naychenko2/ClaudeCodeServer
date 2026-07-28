// Индекс файлов проекта для ссылок на файлы в тексте ассистента: одно рекурсивное
// дерево на проект, дальше «существует ли такой файл» решается локально по Map.
// Без индекса каждое упоминание пути в ответе персоны стучало бы в API.
// Паттерн стора — как у featureFlags.ts: модульное состояние + useSyncExternalStore.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { toRelative } from './paths';

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
  api.files.tree(projectId)
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

// Резолв упоминания пути в файл проекта: абсолютный путь внутри корня приводится к
// относительному, якорь/квери и URL-экранирование снимаются. Возвращает путь как в
// дереве проекта либо null — тогда это не ссылка, а обычный текст.
export function lookupProjectFile(index: ProjectFileIndex, raw: string, rootPath: string): string | null {
  if (!raw || index.size === 0) return null;
  let p = raw.trim().split(/[#?]/)[0];
  if (!p) return null;
  try { p = decodeURIComponent(p); } catch { /* не URL-экранирован — как есть */ }
  const rel = toRelative(p, rootPath);
  if (!rel) return null;
  return index.get(rel.replace(/\/+$/, '').toLowerCase()) ?? null;
}
