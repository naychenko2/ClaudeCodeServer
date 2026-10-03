// Чистая модель вкладки «Фильм» (макет v7): подписи склеек, длительности строк, причины пересборки,
// снимок сцены для «В фильм →», заготовка «Сочинить под фильм…» и данные закреплённого низа.

import type { FilmCut, FilmCutType, FilmDocument, FilmItem, FilmMusicDraft, FilmSceneSnapshot, FilmSummary, FilmSpent, FilmState, VideoScene } from '../api';
import { clock, plural } from '../scene/model';

export const CUT_LABEL: Record<FilmCutType, string> = { butt: 'встык', dissolve: 'наплыв', fade: 'затемнение' };
export const CUT_MARK: Record<FilmCutType, string> = { butt: '|', dissolve: '≈', fade: '■' };
export const CUT_SECS = [0.5, 1, 2] as const;
export const TRIM_STEP = 0.5;

const sec = (n: number) => `${String(n).replace('.', ',')} с`;

// «| встык», «≈ наплыв 1 с», «■ затемнение 2 с»
export const cutLabel = (c: FilmCut | undefined): string => {
  if (!c || c.type === 'butt') return `${CUT_MARK.butt} ${CUT_LABEL.butt}`;
  return `${CUT_MARK[c.type]} ${CUT_LABEL[c.type]} ${sec(c.sec)}`;
};

export const fileName = (path: string) => path.split('/').pop() ?? path;

// Длина клипа строки: снимок сцены, иначе конец подрезки
export const clipLength = (it: FilmItem): number => Math.max(it.scene?.durationSec ?? 0, it.trim[1] ?? 0);
export const trimmedLength = (it: FilmItem): number => Math.max(0, (it.trim[1] ?? 0) - (it.trim[0] ?? 0));

// «✂ 8 с» или «✂ 7 с из 8 с»
export function trimLabel(it: FilmItem): string {
  const full = clipLength(it);
  const t = trimmedLength(it);
  return Math.abs(full - t) < 0.01 ? `✂ ${sec(full)}` : `✂ ${sec(t)} из ${sec(full)}`;
}

// Подрезка шагом 0,5 с: края не заходят друг за друга, клип не короче шага
export function clampTrim(trim: [number, number], full: number): [number, number] {
  const round = (n: number) => Math.round(n / TRIM_STEP) * TRIM_STEP;
  let a = Math.max(0, round(trim[0]));
  let b = Math.min(full, round(trim[1]));
  if (b - a < TRIM_STEP) { if (trim[0] !== a) a = Math.max(0, b - TRIM_STEP); else b = Math.min(full, a + TRIM_STEP); }
  return [a, b];
}

// Длительность фильма: клипы с подрезкой минус наплывы и затемнения (они накладывают клипы)
export function filmDuration(doc: FilmDocument): number {
  const clips = doc.items.reduce((s, it) => s + Math.max(0, (it.trim[1] ?? 0) - (it.trim[0] ?? 0)), 0);
  const overlap = doc.cuts.reduce((s, c) => s + (c.type === 'dissolve' ? c.sec : 0), 0);
  return Math.max(0, clips - overlap);
}

export const filmClock = (doc: FilmDocument) => clock(filmDuration(doc));

export const scenesWord = (n: number) => plural(n, 'сцена', 'сцены', 'сцен');

// «$13.00 · 12 кр · GPU 5 мин»; пусто — ничего не потрачено
export function spentText(s: FilmSpent | undefined): string {
  if (!s) return '';
  const parts: string[] = [];
  if (s.usd > 0) parts.push(`$${s.usd.toFixed(2)}`);
  if (s.credits > 0) parts.push(`${Math.round(s.credits)} кр`);
  if (s.gpuSeconds > 0) parts.push(`GPU ${Math.max(1, Math.round(s.gpuSeconds / 60))} мин`);
  return parts.join(' · ');
}

// Что изменилось после последней сборки — по меткам сервера
export function staleReasons(f: FilmState): string[] {
  if (!f.document.builds.length) return [];
  const out: string[] = [];
  for (const m of f.marks) {
    if (m.updated) out.push(`сцена ${m.index + 1} обновлена`);
    else if (m.stale) out.push(`сцена ${m.index + 1}: текст или кадр изменён`);
  }
  // Устарел по признаку сервера, а причин по строкам нет (правили склейки, подрезку, музыку, порядок)
  if (!out.length && f.stale) out.push('порядок, склейки, подрезка или музыка');
  return out;
}

export const lastBuild = (doc: FilmDocument) => doc.builds[doc.builds.length - 1] ?? null;

export type BuildView =
  | { kind: 'empty' }
  | { kind: 'idle'; rebuild: boolean }
  | { kind: 'waiting' }
  | { kind: 'running'; progress: number }
  | { kind: 'done'; file: string; stale: string[] }
  | { kind: 'failed'; error: string };

// Состояние низа сборки: идущая сборка старше готового файла, «устарел» — из меток
export function buildView(f: FilmState): BuildView {
  const b = f.build;
  if (b?.state === 'waiting') return { kind: 'waiting' };
  if (b?.state === 'running') return { kind: 'running', progress: b.progress };
  if (!f.document.items.length) return { kind: 'empty' };
  if (b?.state === 'failed') return { kind: 'failed', error: b.error || 'Сборка не получилась' };
  const last = lastBuild(f.document);
  if (last) return { kind: 'done', file: last.file, stale: staleReasons(f) };
  return { kind: 'idle', rebuild: false };
}

// «Сочинить под фильм…»: заготовка панели «Звук» — длина фильма, стиль по текстам сцен, инструментал
// Модели музыки не снимают короче этого (сервер отдаёт то же в FilmMusicDraft.minDurationSec)
export const MUSIC_MIN_SEC = 10;

export function soundPreset(name: string, f: FilmState, draft?: FilmMusicDraft | null): Record<string, unknown> {
  const texts = f.document.items.map(i => i.scene?.text?.trim()).filter(Boolean) as string[];
  const style = texts.length
    ? `Инструментальная музыка под фильм «${name}»: ${texts.join('; ').slice(0, 400)}`
    : `Инструментальная музыка под фильм «${name}»`;
  return {
    mode: 'music', op: 'song', duration: draft?.actualDurationSec || Math.max(MUSIC_MIN_SEC, Math.round(filmDuration(f.document))),
    style: draft?.styleText?.trim() || style, instrumental: true,
    from: `под фильм «${name}»`, bindTo: f.path,
  };
}

// Снимок сцены для строки фильма: кадры — только файлы проекта
export function snapshotOf(s: VideoScene): FilmSceneSnapshot {
  const file = (f: VideoScene['settings']['frameA']) => (f?.kind === 'file' ? f.path : undefined);
  return {
    text: s.settings.text,
    ...(file(s.settings.frameA) ? { frameA: file(s.settings.frameA) } : {}),
    ...(file(s.settings.frameB) ? { frameB: file(s.settings.frameB) } : {}),
    ...(s.settings.provider ? { provider: s.settings.provider } : {}),
    ...(s.settings.model ? { model: s.settings.model } : {}),
    ...(s.settings.durationSec ? { durationSec: s.settings.durationSec } : {}),
  };
}

// Сцена чата, чей сохранённый файл стоит в строке фильма
export const sceneOfItem = (scenes: readonly VideoScene[], it: FilmItem) =>
  scenes.find(s => s.savedFiles.some(f => f.path === it.file)) ?? null;

// Новый путь фильма по имени: video/<имя>/<имя>.film
export function newFilmPath(name: string): string | null {
  const clean = name.trim().replace(/[\\/:*?"<>|]+/g, '-').replace(/\s+/g, '-');
  return clean ? `video/${clean}/${clean}.film` : null;
}

export const filmFolder = (path: string) => path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '';

// Куда класть сцену при «Сохранить сцену» / «В фильм →»: открытый фильм старше папки сцены — уходит его
// полный путь (сервер игнорирует folder); иначе папка сцены; null — сцена нигде не лежит, человека спрашивают
export type SaveTarget = { filmPath: string } | { folder: string };
export const saveTargetFor = (filmPath: string | null, scene: Pick<VideoScene, 'folder'>): SaveTarget | null =>
  filmPath ? { filmPath } : scene.folder ? { folder: scene.folder } : null;

// Путь фильма, который сервер ведёт в папке: video/утро → video/утро/утро.film
export const filmPathOf = (folder: string) => `${folder}/${folder.slice(folder.lastIndexOf('/') + 1)}.film`;

// Хвост подсказки строки меню фильмов: «устарел» старше «собран», пустой фильм не собран никогда
export const filmStatusSuffix = (x: Pick<FilmSummary, 'stale' | 'itemCount'>) =>
  x.stale ? ' · устарел' : x.itemCount === 0 ? ' · пустой' : ' · собран';
