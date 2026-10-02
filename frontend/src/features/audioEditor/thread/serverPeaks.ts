// Волна версии с сервера (ручка пиков, ffmpeg на хосте): главный поток не декодирует звук. Файлы
// версии не меняются, поэтому ответ кэшируется на вкладку по версии, роли и числу точек — с
// потолком. Нет ffmpeg (503) или файла — волна ровная, плеер работает и без неё.

import { useEffect, useState } from 'react';
import { audioApi, type AudioPeaks } from '../api';
import { lruGet, lruSet } from '../player/peaks';

export interface PeaksRequest { scope: string; sessionId: string; threadId: string; versionId: string; role: string | null; points: number }

export const CACHE_MAX = 64;
const cache = new Map<string, Promise<AudioPeaks>>();

export const peaksKey = (r: PeaksRequest) => [r.scope, r.sessionId, r.threadId, r.versionId, r.role ?? 'main', r.points].join('|');

export function loadPeaks(r: PeaksRequest): Promise<AudioPeaks> {
  const key = peaksKey(r);
  let p = lruGet(cache, key);
  if (!p) {
    p = audioApi.peaks(r.scope, r.sessionId, r.threadId, r.versionId, r.points, r.role);
    lruSet(cache, key, p, CACHE_MAX);
    // Отказ не кэшируем: ffmpeg могли поставить, файл — вернуть
    p.catch(() => { if (cache.get(key) === p) cache.delete(key); });
  }
  return p;
}

// Пики по списку запросов в том же порядке; null — ещё грузим или не вышло
export function useServerPeaks(reqs: PeaksRequest[]): (AudioPeaks | null)[] {
  const keys = reqs.map(peaksKey);
  const joined = keys.join('\n');
  const [got, setGot] = useState<Record<string, AudioPeaks>>({});
  useEffect(() => {
    let alive = true;
    reqs.forEach((r, i) => {
      const key = keys[i];
      loadPeaks(r).then(
        value => { if (alive) setGot(prev => (prev[key] === value ? prev : { ...prev, [key]: value })); },
        () => { /* волна останется ровной */ },
      );
    });
    return () => { alive = false; };
    // Запросы сравниваем по ключам: массив пересобирается на каждый рендер
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [joined]);
  return keys.map(k => got[k] ?? null);
}

// Сброс — только для тестов
export function __resetPeaksCache() { cache.clear(); }
export const __peaksCacheSize = () => cache.size;
