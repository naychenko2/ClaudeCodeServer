// Персонажи проекта — общий список на вкладку для панели «Персонажи» и чипа в полосе
// «Картинки»: правка в панели сразу видна в полосе, без второго запроса.

import { useEffect, useSyncExternalStore } from 'react';
import { imageEditorApi, type ImageEditCharacter } from '../api';

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
  const loading = imageEditorApi().listCharacters(projectId)
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

export function useCharacters(projectId: string): { list: ImageEditCharacter[] | null; error: boolean } {
  useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
  useEffect(() => { void load(projectId); }, [projectId]);
  const e = _entries.get(projectId);
  return { list: e?.list ?? null, error: e?.error ?? false };
}
