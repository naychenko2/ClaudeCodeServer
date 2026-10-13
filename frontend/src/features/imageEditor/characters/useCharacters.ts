// Персонажи проекта — общий список на вкладку для панели «Персонажи» и чипа в полосе
// «Картинки»: правка в панели сразу видна в полосе, без второго запроса.

import { useEffect, useSyncExternalStore } from 'react';
import { imageEditorApi, type ImageEditCharacter } from '../api';
import { isPersonalScope } from '../scope';

interface Entry { list: ImageEditCharacter[] | null; loading: Promise<void> | null; error: boolean }

const _entries = new Map<string, Entry>();
let _version = 0;
const _listeners = new Set<() => void>();

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

function load(projectId: string, force = false): Promise<void> {
  const e = _entries.get(projectId);
  if (e?.loading) return e.loading;
  if (e?.list && !force) return Promise.resolve();
  // Синхронный отказ (projectBase у личной области) — в ветку .catch, а не из useEffect
  const loading = Promise.resolve().then(() => imageEditorApi().listCharacters(projectId))
    .then(list => { _entries.set(projectId, { list, loading: null, error: false }); })
    // Модуль выключен или связи нет — список пуст, панель покажет ошибку с повтором
    .catch(() => { _entries.set(projectId, { list: e?.list ?? [], loading: null, error: true }); })
    .finally(emit);
  _entries.set(projectId, { list: e?.list ?? null, loading, error: false });
  return loading;
}

export function reloadCharacters(projectId: string): Promise<void> {
  return load(projectId, true);
}

// Сохранённый или удалённый персонаж — сразу в списке, не дожидаясь перечитывания
export function putCharacter(projectId: string, c: ImageEditCharacter) {
  const list = _entries.get(projectId)?.list ?? [];
  const next = list.some(x => x.slug === c.slug) ? list.map(x => (x.slug === c.slug ? c : x)) : [...list, c];
  _entries.set(projectId, { list: next, loading: null, error: false });
  emit();
}

export function dropCharacter(projectId: string, slug: string) {
  const list = _entries.get(projectId)?.list ?? [];
  _entries.set(projectId, { list: list.filter(x => x.slug !== slug), loading: null, error: false });
  emit();
}

// projectId = null — персонажей нет (личный чат вне проекта): список пуст, запроса нет.
// Область (включая 'personal') нормализуем в null: проектная ручка персонажей у неё
// по построению не зовётся, а projectBase('personal') бросает синхронно — одна точка на всех
// вызывающих, им не нужно самим различать область и проект
export function useCharacters(projectId: string | null): { list: ImageEditCharacter[] | null; error: boolean } {
  const pid = projectId && !isPersonalScope(projectId) ? projectId : null;
  useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
  useEffect(() => { if (pid) void load(pid); }, [pid]);
  if (!pid) return { list: [], error: false };
  const e = _entries.get(pid);
  return { list: e?.list ?? null, error: e?.error ?? false };
}
