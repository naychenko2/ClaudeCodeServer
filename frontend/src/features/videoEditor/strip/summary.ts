// Сводки полосы «Видео»: чип сцены и чип фильма (макет v7, «Полоса «Видео»»)

import { clock, sceneNotReady, type ResolvedScene } from '../scene/model';
import type { FilmDocument, FilmState, VideoScene } from '../api';

// Длительность фильма: клипы с подрезкой минус наплывы и затемнения (они накладывают клипы)
export function filmDuration(doc: FilmDocument): number {
  const clips = doc.items.reduce((s, it) => s + Math.max(0, (it.trim[1] ?? 0) - (it.trim[0] ?? 0)), 0);
  const overlap = doc.cuts.reduce((s, c) => s + (c.type === 'dissolve' ? c.sec : 0), 0);
  return Math.max(0, clips - overlap);
}

export const staleFilm = (f: FilmState | null): boolean => {
  if (!f) return false;
  const b = f.document.builds[f.document.builds.length - 1];
  return !b || f.marks.some(m => m.updated || m.stale) || f.build?.state === 'failed';
};

export interface SceneChip { text: string; short: string; warn: boolean }

// «Сцена 5 · Veo 3.1 · 8 с · 2 вар. · ≈ $3.20»
export function sceneChip(scene: VideoScene | null, r: ResolvedScene, price: string | null): SceneChip {
  const model = r.model?.label ?? 'Авто';
  const parts = [scene?.name ?? 'Новая сцена', model, `${r.durationSec} с`];
  if (r.count > 1) parts.push(`${r.count} вар.`);
  if (price) parts.push(price);
  const short = [scene ? scene.name.replace(/^Сцена\s*/i, 'Сц. ') : 'Новая', price].filter(Boolean).join(' · ');
  return { text: parts.join(' · '), short, warn: sceneNotReady(r) };
}

// «утро-в-горах · 4 сцены · 0:32»
export function filmChip(name: string, f: FilmState | null): { text: string; short: string } {
  if (!f) return { text: name, short: name };
  const n = f.document.items.length;
  const word = n % 10 === 1 && n % 100 !== 11 ? 'сцена' : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? 'сцены' : 'сцен';
  const d = clock(filmDuration(f.document));
  return { text: `${name} · ${n} ${word} · ${d}`, short: d };
}
