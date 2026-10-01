import { useEffect, useState } from 'react';
import { computePeaks } from './peaks';

export interface BrowserPeaks {
  peaks: number[];
  duration: number;
}

// Кэш на сессию вкладки: волна одной версии рисуется в ленте и в панели разом
const cache = new Map<string, Promise<BrowserPeaks>>();

function decode(url: string, count: number): Promise<BrowserPeaks> {
  const key = `${count}|${url}`;
  let p = cache.get(key);
  if (!p) {
    p = (async () => {
      const res = await fetch(url);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const buf = await res.arrayBuffer();
      // OfflineAudioContext не держит аудиоустройство и не требует жеста пользователя
      const ctx = new OfflineAudioContext(1, 1, 44100);
      const audio = await ctx.decodeAudioData(buf);
      const channels = Array.from({ length: audio.numberOfChannels }, (_, i) => audio.getChannelData(i));
      return { peaks: computePeaks(channels, count), duration: audio.duration };
    })();
    cache.set(key, p);
    p.catch(() => cache.delete(key));
  }
  return p;
}

/**
 * Пики волны WebAudio-декодом прямо в браузере — для локального файла, пока сервер
 * не отдаёт готовые пики. null — ещё считаем или не вышло (волна рисуется ровной).
 */
export function useBrowserPeaks(url: string | null | undefined, count = 120): BrowserPeaks | null {
  const [state, setState] = useState<{ key: string; value: BrowserPeaks } | null>(null);
  const key = url ? `${count}|${url}` : '';
  useEffect(() => {
    if (!url) return;
    let alive = true;
    decode(url, count).then(
      value => { if (alive) setState({ key: `${count}|${url}`, value }); },
      () => { /* волна останется ровной: плеер работает и без неё */ },
    );
    return () => { alive = false; };
  }, [url, count]);
  return state && state.key === key ? state.value : null;
}
