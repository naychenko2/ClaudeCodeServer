import { useEffect, useState } from 'react';
import { api } from './api';
import type { Sphere } from '../types';

// Список сфер (групп проектов) для подписей и выбора зоны персоны. enabled=false — флаг
// spheres выключен: запроса нет, список пуст.
const TTL_MS = 60_000;
let cache: Sphere[] = [];
let fetchedAt = 0;
let inflight: Promise<Sphere[]> | null = null;

function load(): Promise<Sphere[]> {
  if (Date.now() - fetchedAt < TTL_MS) return Promise.resolve(cache);
  inflight ??= api.projectGroups.list()
    .then(list => { cache = list; fetchedAt = Date.now(); return list; })
    .catch(() => cache)
    .finally(() => { inflight = null; });
  return inflight;
}

export function useSpheres(enabled: boolean): Sphere[] {
  const [list, setList] = useState<Sphere[]>(cache);
  useEffect(() => {
    if (!enabled) return;
    let alive = true;
    void load().then(l => { if (alive) setList(l); });
    return () => { alive = false; };
  }, [enabled]);
  return enabled ? list : [];
}
