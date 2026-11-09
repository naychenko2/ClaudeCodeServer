import { useSyncExternalStore } from 'react';
import type { PluginInfo } from '../../lib/api';

// Мелкий реактивный стор «установленные плагины проекта»: кто-то (секция в настройках
// или слот панели) загрузил список — другие подписчики узнают, есть ли плагины, и
// слот панели появляется/исчезает (гейт V5: без плагинов слота нет).
//
// Хранится per-project (Map по id): список одного проекта не смешивается с другим.
// Значение — сам список (не только флаг), чтобы подписчик мог показать и состав.

const _byProject = new Map<string, PluginInfo[]>();
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Записать список проекта (вызывает useInstalledPlugins после загрузки/обновления).
export function setInstalledPlugins(projectId: string, list: PluginInfo[]) {
  _byProject.set(projectId, list);
  emit();
}

export function getInstalledPlugins(projectId: string): PluginInfo[] {
  return _byProject.get(projectId) ?? [];
}

// Есть ли хотя бы один установленный плагин (гейт V5).
export function hasInstalledPlugins(projectId: string): boolean {
  return getInstalledPlugins(projectId).length > 0;
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Хук: true, когда у проекта есть хотя бы один установленный плагин.
// useSyncExternalStore: getSnapshot возвращает стабильный boolean, перерисовки нет,
// пока список не сменился (emit).
export function useHasInstalledPlugins(projectId: string | null | undefined): boolean {
  if (!projectId) return false;
  return useSyncExternalStore(
    subscribe,
    () => hasInstalledPlugins(projectId),
    () => hasInstalledPlugins(projectId),
  );
}
