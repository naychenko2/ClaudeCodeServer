// Настройки запуска и персонаж. У выбранной картинки настройки — её (settings нити на
// сервере), без выбора — умолчания проекта на устройстве: их подхватит следующая
// картинка («Настройки без выбора не пропадают»). Персонаж — на проект: при смене
// выбранной картинки он остаётся подключённым.

import { useSyncExternalStore } from 'react';
import type { ImageThreadSettings } from './threadsApi';

export interface ProjectPrefs extends ImageThreadSettings { characterSlug: string | null }

const DEFAULTS: ProjectPrefs = { provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null };
const key = (projectId: string) => `cc-image-prefs:${projectId}`;

const _cache = new Map<string, ProjectPrefs>();
let _version = 0;
const _listeners = new Set<() => void>();

export function getPrefs(projectId: string): ProjectPrefs {
  const hit = _cache.get(projectId);
  if (hit) return hit;
  let v = DEFAULTS;
  try {
    const raw = localStorage.getItem(key(projectId));
    if (raw) v = { ...DEFAULTS, ...(JSON.parse(raw) as Partial<ProjectPrefs>) };
  } catch { /* приватный режим или мусор в хранилище */ }
  _cache.set(projectId, v);
  return v;
}

export function setPrefs(projectId: string, patch: Partial<ProjectPrefs>) {
  const next = { ...getPrefs(projectId), ...patch };
  _cache.set(projectId, next);
  try { localStorage.setItem(key(projectId), JSON.stringify(next)); } catch { /* приватный режим */ }
  _version++;
  _listeners.forEach(fn => fn());
}

export function usePrefs(projectId: string): ProjectPrefs {
  useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
  return getPrefs(projectId);
}

// Настройки, с которыми пойдёт запуск: у нити — свои, иначе умолчания проекта
export function effectiveSettings(prefs: ProjectPrefs, own: ImageThreadSettings | null | undefined): ImageThreadSettings {
  return own ?? { provider: prefs.provider, model: prefs.model, count: prefs.count, matchSourceSize: prefs.matchSourceSize };
}
