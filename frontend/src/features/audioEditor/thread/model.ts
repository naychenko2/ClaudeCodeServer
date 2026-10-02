// Модель карточки нити звука в ленте (макет audio-editor-v2-proposal.md, «Карточка нити в ленте»):
// подпись версии, пара A/B, файлы версии по ролям, значок лицензии, цена, сохранение. Чистые
// функции — под юнит-тестом; карточка только рисует то, что они вернули.

import type {
  AudioMixRequest, AudioPrice, AudioThread, AudioThreadEvent, AudioThreadLaunch, AudioThreadVersion, AudioVersionFile,
} from '../api';
import type { MixPlan } from '../player/mix';

export const ORIGIN = 'origin';
export const MAIN = 'main';
export const STEM_PREFIX = 'stem:';

const baseName = (path: string) => path.split('/').pop() ?? path;
const extOf = (path: string) => {
  const name = baseName(path);
  const i = name.lastIndexOf('.');
  return i > 0 ? name.slice(i + 1).toLowerCase() : '';
};
const stemOf = (path: string) => {
  const name = baseName(path);
  const i = name.lastIndexOf('.');
  return i > 0 ? name.slice(0, i) : name;
};

export function threadName(thread: AudioThread): string {
  return thread.file ? baseName(thread.file) : thread.name?.trim() || 'Новый звук';
}

export const versionLabel = (v: AudioThreadVersion) => (v.id === ORIGIN ? 'исходник' : `версия ${v.number}`);

// Версии по порядку: исходник первым, дальше по номеру
export const orderedVersions = (thread: AudioThread): AudioThreadVersion[] =>
  [...thread.versions].sort((a, b) => a.number - b.number);

// Подпись версии в шапке карточки: «исходник», «версия 3», у варианта запуска — «версия 3 · вариант 2 из 2»
export function versionTag(thread: AudioThread, v: AudioThreadVersion): string {
  if (v.id === ORIGIN) return 'исходник';
  const siblings = v.jobId ? launchVersions(thread, v.jobId) : [];
  if (siblings.length < 2) return `версия ${v.number}`;
  return `версия ${v.number} · вариант ${siblings.findIndex(x => x.id === v.id) + 1} из ${siblings.length}`;
}

// ── A/B ──

export interface AbSide { versionId: string; label: string }

const short = (v: AudioThreadVersion) => (v.id === ORIGIN ? 'исх.' : `в${v.number}`);

export const hasMain = (v: AudioThreadVersion | null | undefined) => !!v?.files.some(f => f.role === MAIN);

// Версия, с которой сравнивать: основа запуска или правки, иначе предыдущая по номеру
export function compareBase(thread: AudioThread, v: AudioThreadVersion): AudioThreadVersion | null {
  if (v.baseVersionId) {
    const b = thread.versions.find(x => x.id === v.baseVersionId);
    if (b && b.id !== v.id) return b;
  }
  const prev = orderedVersions(thread).filter(x => x.number < v.number);
  return prev[prev.length - 1] ?? null;
}

// Стороны A/B: A — основа, B — смотримая версия. Одна сторона — сравнивать не с чем (у основы нет звука)
export function abSides(thread: AudioThread, v: AudioThreadVersion): AbSide[] {
  if (!hasMain(v)) return [];
  const base = compareBase(thread, v);
  const b = { versionId: v.id, label: `B · ${short(v)}` };
  if (!base || !hasMain(base)) return [{ versionId: v.id, label: short(v) }];
  return [{ versionId: base.id, label: `A · ${short(base)}` }, b];
}

// ── Файлы версии ──

export interface StemFile { id: string; name: string; path: string }
export interface ExtraFile { role: string; label: string; ext: string; name: string }

export const isStem = (f: AudioVersionFile) => f.role.startsWith(STEM_PREFIX);

// Подпись стема в интерфейсе — по-русски; роль (stem:vocals) и имя файла остаются латиницей:
// по роли сводит сервер и обращается агент
const STEM_LABEL: Record<string, string> = {
  vocals: 'вокал', lead_vocals: 'основной вокал', lead: 'основной вокал', backing_vocals: 'бэк-вокал', backing: 'бэк-вокал',
  instrumental: 'минус', accompaniment: 'минус', no_vocals: 'минус', other: 'прочее', drums: 'барабаны', bass: 'бас',
  guitar: 'гитара', piano: 'пианино', keyboard: 'клавишные', percussion: 'перкуссия', strings: 'струнные',
  synth: 'синтезатор', fx: 'эффекты', brass: 'медные', woodwinds: 'деревянные духовые', music: 'музыка', speech: 'речь',
};
export const stemLabel = (name: string): string => STEM_LABEL[name.toLowerCase()] ?? name;

export const versionStems = (v: AudioThreadVersion): StemFile[] =>
  v.files.filter(isStem).map(f => ({ id: f.role, name: stemLabel(f.role.slice(STEM_PREFIX.length)), path: f.path }));

const EXTRA_LABEL: Record<string, string> = {
  score: 'Ноты ABC',
  subtitles: 'Субтитры',
  lyrics: 'Слова со временем',
  text: 'Текст',
  midi: 'MIDI',
  model: 'Модель голоса',
  index: 'Индекс голоса',
};
// Порядок строк: что человек открывает чаще — выше
const EXTRA_ORDER = ['text', 'subtitles', 'lyrics', 'score', 'midi', 'model', 'index'];

// Всё, кроме главного звука и стемов: .abc, .txt/.srt/.lrc, .mid, .pth/.index — списком со скачиванием
export function extraFiles(v: AudioThreadVersion): ExtraFile[] {
  return v.files
    .filter(f => f.role !== MAIN && !isStem(f))
    .sort((a, b) => rank(a.role) - rank(b.role))
    .map(f => ({ role: f.role, label: EXTRA_LABEL[f.role] ?? f.role, ext: extOf(f.path), name: baseName(f.path) }));
}
const rank = (role: string) => { const i = EXTRA_ORDER.indexOf(role); return i < 0 ? EXTRA_ORDER.length : i; };

// Версия «в MIDI» для просмотра нот: файл роли midi без главного звука
export const midiFileOf = (v: AudioThreadVersion): AudioVersionFile | null =>
  hasMain(v) ? null : v.files.find(f => f.role === 'midi') ?? null;

// Имя файла черновика без файла при «Сохранить в проект» — по имени нити («anthem.mp3» → «anthem»):
// без него сервер звал группу «audio», и стемы песни ложились в «audio.stems/». Запрещённые в
// имени файла символы заменяем, пустое имя — «audio», как у сервера
export function draftStem(thread: AudioThread): string {
  const raw = (thread.name ?? '').trim();
  const noExt = /\.[A-Za-z0-9]{1,5}$/.test(raw) ? raw.slice(0, raw.lastIndexOf('.')) : raw;
  const clean = noExt.replace(/[<>:"/\\|?*]/g, '-').replace(/^\.+/, '').trim();
  return clean || 'audio';
}

// Папка стемов при сохранении в проект: «podcast-intro.stems/»
export function stemsFolder(thread: AudioThread): string {
  return `${thread.file ? stemOf(thread.file) : draftStem(thread)}.stems/`;
}

// ── Лицензия ──

export type LicenseTone = 'warning' | 'danger' | 'success' | 'neutral';
export interface LicenseBadge { label: string; tone: LicenseTone; hint: string }

// Значок рисуется из версии (лицензия модели на момент запуска), а не из текущего каталога.
// У исходника лицензии нет — это файл человека, значка тоже нет
export function licenseBadge(v: AudioThreadVersion): LicenseBadge | null {
  return v.id === ORIGIN ? null : licenseBadgeOf(v.license);
}

// Подпись лицензии («CC BY-NC 4.0», «GPL-3.0», «watermark»…) → значок; пусто — значка нет
export function licenseBadgeOf(license: string | null | undefined): LicenseBadge | null {
  const l = (license ?? '').trim();
  const low = l.toLowerCase();
  if (/by-nc|non-?commercial/.test(low)) return { label: 'CC BY-NC', tone: 'warning', hint: 'Только некоммерческое использование' };
  if (low.includes('gpl')) return { label: 'GPL-3.0', tone: 'warning', hint: 'Модель под GPL-3.0: проверьте условия перед распространением' };
  if (low.includes('watermark') || low.includes('водян')) return { label: 'водяной знак', tone: 'danger', hint: 'В звук вшит неслышимый водяной знак поставщика' };
  if (low.includes('mit') || low.includes('apache')) return { label: 'коммерчески чистая', tone: 'success', hint: `Лицензия модели — ${l}` };
  // Правка без ИИ лицензии не меняет и своей не пишет
  if (!l) return null;
  return { label: 'лицензия не указана', tone: 'neutral', hint: 'Поставщик не указал лицензию результата' };
}

// ── Цена ──

const money = (amount: number, unit: string) => {
  const n = String(Math.round(amount * 10_000) / 10_000);
  if (unit === 'usd') return `$${n}`;
  if (unit === 'rub') return `${n.replace('.', ',')} ₽`;
  if (unit === 'credits') return `${n.replace('.', ',')} кред.`;
  return `${n} ${unit}`;
};

// «бесплатно» у локальных и без ИИ; «≈ $0.12» у оценки; null — цены нет
export function priceText(p: Partial<AudioPrice> | null | undefined): string | null {
  if (!p || typeof p.unit !== 'string') return null;
  if (p.unit === 'free' || p.amount === 0) return 'бесплатно';
  if (typeof p.amount !== 'number') return null;
  return `${p.approx ? '≈ ' : ''}${money(p.amount, p.unit)}`;
}

// ── «Что сделано» ──

// Правка без ИИ пишет событие «Человек свёл стемы (…) (без ИИ): имя, от: версия 2»; в карточке — «Без ИИ · Свёл стемы (…)»
export function doneText(thread: AudioThread, v: AudioThreadVersion, events: readonly AudioThreadEvent[] | undefined): string | null {
  if (v.id === ORIGIN || !v.jobId) return null;
  const launch = thread.launches.find(l => l.jobId === v.jobId);
  if (launch) {
    const who = launch.initiator === 'agent' ? 'Claude' : null;
    return [launch.prompt ? `«${launch.prompt}»` : null, who].filter(Boolean).join(' · ') || null;
  }
  const ev = events?.find(e => e.jobId === v.jobId && e.kind === 'edited');
  if (!ev) return null;
  const agent = ev.text.startsWith('Ты ');
  const what = ev.text.replace(/^(Ты|Человек)\s+/, '').replace(/\s*\(без ИИ\).*$/s, '').trim();
  if (!what) return 'Без ИИ';
  return ['Без ИИ', what.charAt(0).toUpperCase() + what.slice(1), agent ? 'Claude' : null].filter(Boolean).join(' · ');
}

// ── Запуск и его варианты ──

export const launchOf = (thread: AudioThread, jobId: string): AudioThreadLaunch | null =>
  thread.launches.find(l => l.jobId === jobId) ?? null;

export const launchVersions = (thread: AudioThread, jobId: string): AudioThreadVersion[] =>
  thread.versions.filter(v => v.jobId === jobId).sort((a, b) => (a.variant ?? 0) - (b.variant ?? 0));

export function launchEndNote(status: AudioThreadLaunch['status'], got: number, count: number): string | null {
  if (status === 'running') return null;
  if (status === 'cancelled') return got ? `Запуск отменён, готово вариантов: ${got}` : 'Запуск отменён';
  if (status === 'interrupted') return 'Задачу оборвал перезапуск сервера';
  if (status === 'failed' || !got) return 'Запуск не получился';
  return got < count ? `Готово вариантов: ${got} из ${count}` : null;
}

// ── Действия ──

// Главная кнопка сохранения: в проекте — «Сохранить в проект» и «Сохранить как…», в личном чате
// проекта нет — только «Скачать»
export type SaveKind = 'project' | 'download';
export const saveKind = (personal: boolean): SaveKind => (personal ? 'download' : 'project');

// Сводим то, что звучит в микшере: стемы плана с громкостью, без заглушённых
export function mixRequest(plan: MixPlan, baseVersionId: string, revision: number): AudioMixRequest {
  return {
    stems: plan.stems.map(s => ({ role: s.id, gainDb: s.gainDb })),
    baseVersionId,
    revision,
  };
}

// Подсказка сервера «music/intro.v2.mp3» → папка и имя для повтора «Сохранить как…»
export function splitSuggestion(path: string): { folder: string; fileName: string } {
  const i = path.lastIndexOf('/');
  return i < 0 ? { folder: '', fileName: path } : { folder: path.slice(0, i), fileName: path.slice(i + 1) };
}

// Имя по умолчанию в «Сохранить как…»: «intro.v3» у версии 3, «audio» у черновика
export function defaultSaveName(thread: AudioThread, v: AudioThreadVersion): string {
  const stem = thread.file ? stemOf(thread.file).replace(/\.v\d+$/, '') : (thread.name?.trim() || 'audio');
  return v.id === ORIGIN ? stem : `${stem}.v${v.number}`;
}

export const defaultSaveFolder = (thread: AudioThread): string => {
  if (thread.file) { const i = thread.file.lastIndexOf('/'); return i < 0 ? '' : thread.file.slice(0, i); }
  return thread.draftFolder ?? '';
};
