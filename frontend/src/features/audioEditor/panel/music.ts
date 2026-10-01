// Режим «Музыка» панели «Звук» (макет audio-editor-v2-proposal.md, таблица «Музыка»; ADR-021):
// что умеет выбранная модель — слова, инструментал, длительность, язык вокала, — лицензия и
// «тяжёлая» операция до запуска и причина, по которой запуск невозможен. Чистые функции.

import type { AudioModelInfo, AudioOp } from '../api';
import type { AudioSelection } from '../player/selection';

// Модели local-media с особыми правилами (id из AudioCatalog на бэкенде)
export const YUE2 = 'yue2-3b';
// Язык вокала параметром задаёт только ACE-Step 1.5 XL: у YuE2 и MiniMax язык — словами песни
const VOCAL_LANGUAGE_MODELS: ReadonlySet<string> = new Set(['ace-step-1.5-xl']);

// Операции, где у песни бывают слова
const LYRICS_OPS: ReadonlySet<AudioOp> = new Set(['song', 'cover', 'repaint']);
// Операции, где человек задаёт длину результата
const DURATION_OPS: ReadonlySet<AudioOp> = new Set(['song', 'sfx', 'outpaint']);

export const SECTIONS = ['[Verse]', '[Chorus]', '[Bridge]', '[Intro]', '[Outro]'] as const;

export const TRACK_LABEL: Record<string, string> = {
  vocals: 'Вокал', backing_vocals: 'Бэк-вокал', drums: 'Барабаны', bass: 'Бас', guitar: 'Гитара',
  keyboard: 'Клавишные', percussion: 'Перкуссия', strings: 'Струнные', synth: 'Синтезатор', fx: 'Эффекты',
  brass: 'Медные', woodwinds: 'Деревянные духовые',
};
export const trackLabel = (t: string) => TRACK_LABEL[t] ?? t;

// Модель поёт по словам: у неё есть языки, и язык ей не безразличен (инструментальные — languageNeutral)
export const singsLyrics = (m: AudioModelInfo | null) => !!m && !m.caps.languageNeutral && m.caps.languages.length > 0;

export const lyricsField = (op: AudioOp, m: AudioModelInfo | null) => LYRICS_OPS.has(op) && singsLyrics(m);

// YuE2 поёт только по словам: и песня, и кавер без слов не запустятся
export const lyricsRequired = (op: AudioOp, m: AudioModelInfo | null) => m?.id === YUE2 && (op === 'song' || op === 'cover');

// Почему «Инструментал» нельзя; null — можно
export const instrumentalBlocked = (op: AudioOp, m: AudioModelInfo | null): string | null =>
  lyricsRequired(op, m) ? 'YuE2 поёт только со словами' : null;

export const vocalLanguage = (op: AudioOp, m: AudioModelInfo | null) => op === 'song' && !!m && VOCAL_LANGUAGE_MODELS.has(m.id);

export function durationRange(op: AudioOp, m: AudioModelInfo | null): { min: number; max: number } | null {
  if (!DURATION_OPS.has(op) || !m?.caps.maxDurationSec) return null;
  return { min: m.caps.minDurationSec ?? 1, max: m.caps.maxDurationSec };
}

// Некоммерческая лицензия — видна ДО запуска, а не только у готовой версии
export function licenseWarning(m: AudioModelInfo | null): string | null {
  if (m?.caps.license.kind !== 'nonCommercial') return null;
  return `Лицензия ${m.caps.license.label}: только некоммерческое использование — результат нельзя продавать и ставить в рекламу`;
}

export function heavyWarning(op: AudioOp, m: AudioModelInfo | null): string | null {
  if (!m?.caps.heavyOps?.includes(op)) return null;
  return 'Тяжёлая операция: на видеокарте идёт одна за раз и ждёт, пока закончатся задачи перед ней, — минуты';
}

export interface MusicInputs {
  lyrics: string;
  instrumental: boolean;
  durationSec: number | null;
}

// Слова, которые уйдут модели: инструментал и пустое поле — без слов
export function lyricsToSend(op: AudioOp, m: AudioModelInfo | null, i: MusicInputs): string | null {
  if (!lyricsField(op, m)) return null;
  if (op === 'song' && i.instrumental && !instrumentalBlocked(op, m)) return null;
  return i.lyrics.trim() || null;
}

export interface MusicReasonInput {
  op: AudioOp;
  model: AudioModelInfo | null;
  inputs: MusicInputs;
  piece: AudioSelection | null;
  fields: Record<string, unknown>;
}

export function musicReason({ op, model, inputs, piece, fields }: MusicReasonInput): string | null {
  if (op === 'repaint' && !piece) return 'Задайте кусок: выделите его на волне в ленте или впишите начало и конец';
  if (lyricsRequired(op, model) && !inputs.lyrics.trim()) return 'YuE2 поёт по словам — напишите слова песни';
  const max = model?.caps.maxTextChars;
  const lyrics = lyricsToSend(op, model, inputs);
  if (max && lyrics && lyrics.length > max) return `Слова длиннее ${max} символов — сократите`;
  const range = durationRange(op, model);
  const d = inputs.durationSec;
  if (range && d !== null && (d < range.min || d > range.max)) return `Длительность — от ${range.min} до ${range.max} с`;
  if (op === 'complete' && Array.isArray(fields.tracks) && fields.tracks.length === 0) return 'Выберите хотя бы один инструмент';
  return null;
}

// Вставка секции в слова: с новой строки, курсор — после неё
export function insertSection(lyrics: string, section: string): string {
  const head = lyrics.replace(/\s+$/, '');
  return head ? `${head}\n\n${section}\n` : `${section}\n`;
}
