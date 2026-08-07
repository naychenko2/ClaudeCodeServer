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
