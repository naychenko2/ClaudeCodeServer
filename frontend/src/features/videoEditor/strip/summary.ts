// Сводки полосы «Видео»: чип сцены и чип фильма (макет v7, «Полоса «Видео»»)

import { clock, sceneNotReady, type ResolvedScene } from '../scene/model';
import { filmDuration } from '../film/model';
import type { FilmState, VideoScene } from '../api';

// Точка «изменён после сборки» — только у фильма, который уже собирали: у несобранного нечего пересобирать
export const staleFilm = (f: FilmState | null): boolean => {
  if (!f) return false;
  const built = f.document.builds.length > 0;
  // Признак сервера покрывает и правки склеек, подрезки, музыки и порядка — метки строк их не видят
  const changed = f.stale ?? f.marks.some(m => m.updated || m.stale);
  return (built && changed) || f.build?.state === 'failed';
};

// Пока цены нет в сторе: честное «уточняется» (как у «Картинок»), а не пустое место
export const PRICE_UNKNOWN = 'цена уточняется';

export interface SceneChip {
  text: string; short: string; warn: boolean;
  // Части для узкой строки: имя модели режется первым, цена остаётся
  name: string; model: string; meta: string; price: string;
  // Узкий вид: имя режется, цена остаётся целой — поэтому они раздельно
  shortName: string; shortPrice: string;
}

// «Сцена 5 · Veo 3.1 · 8 с · 2 вар. · ≈ $3.20»
export function sceneChip(scene: VideoScene | null, r: ResolvedScene, price: string | null): SceneChip {
  const model = r.model?.label ?? 'Авто';
  const name = scene?.name ?? 'Новая сцена';
  const shownPrice = price ?? (scene ? PRICE_UNKNOWN : '');
  const meta = [`${r.durationSec} с`, r.count > 1 ? `${r.count} вар.` : null].filter(Boolean).join(' · ');
  const text = [name, model, meta, shownPrice].filter(Boolean).join(' · ');
  const shortName = scene ? scene.name.replace(/^Сцена\s*/i, 'Сц. ') : 'Новая';
  const shortPrice = price ?? (scene ? 'цена…' : '');
  const short = [shortName, shortPrice].filter(Boolean).join(' · ');
  return { text, short, warn: sceneNotReady(r), name, model, meta, price: shownPrice, shortName, shortPrice };
}

// «утро-в-горах · 4 сцены · 0:32»
export function filmChip(name: string, f: FilmState | null): { text: string; short: string; meta: string } {
  if (!f) return { text: name, short: name, meta: '' };
  const n = f.document.items.length;
  const word = n % 10 === 1 && n % 100 !== 11 ? 'сцена' : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? 'сцены' : 'сцен';
  const d = clock(filmDuration(f.document));
  return { text: `${name} · ${n} ${word} · ${d}`, short: d, meta: `${n} ${word} · ${d}` };
}
